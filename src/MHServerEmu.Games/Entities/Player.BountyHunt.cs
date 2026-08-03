using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    // Bounty Hunt — pick an active (non-Defeated) entry off the existing
    // Bounty Board / Nemesis roster (Player.Nemesis.cs) at a chosen
    // difficulty tier (1-10, ephemeral — independent of the nemesis's own
    // persisted Rank, which stays capped at NemesisMaxRank=5), warp to a
    // random arena (reuses Trial of the Impossible's own arena pool), and
    // the target ambushes the player there some time after arrival instead
    // of instantly. Killing it pays the existing Bounty Board loot-table
    // reward (TryClaimBountyReward, unchanged) PLUS tier-scaled currency and
    // (tier 9-10 only) one guaranteed BiS piece — so a harder-tier bounty is
    // both a harder fight (real HP/damage curve, not cosmetic) and a bigger
    // payout, using data that already exists rather than inventing anything.
    public partial class Player
    {
        private static readonly Logger BountyHuntLogger = LogManager.CreateLogger();

        // Baseline reward table for every successful hunt regardless of
        // tier — the same confirmed-real table Trial of the Impossible's
        // finale chest uses (Loot/Tables/Mob/Bosses/EndgameDailies/Subtables/
        // SharedEndgameDailiesCosmicBUFFED.prototype). Deliberately reused
        // rather than inventing new loot table refs — tier differentiation
        // comes from the currency/BiS bonus below, not from picking between
        // multiple unverified tables.
        private const ulong BountyHuntBaselineLootTableRef = 0x0520D1A142CA23CD;

        // Longer than Trial's 15-30s hazard tick — this is meant to read as
        // "hunting", the target isn't guaranteed to be waiting right there.
        private const int BountyHuntSpawnMinDelayMs = 30_000;
        private const int BountyHuntSpawnMaxDelayMs = 60_000;

        // Flat currency-per-tier scaling, reusing the exact same currency
        // fields Trial's finale grant already uses.
        public const int BountyHuntEternitySplintersPerTier = 5;
        public const int BountyHuntCubeShardsPerTier = 2;
        public const int BountyHuntLegendaryMarksPerTier = 1;

        // Tier at/above which a kill also grants one guaranteed BiS piece
        // for the player's own current hero — same mechanism Trial's finale
        // chest uses (PhantomBiSData + LootManager.SpawnItem), just a single
        // piece instead of Trial's multi-hero spread.
        public const int BountyHuntGuaranteedBisTier = 9;

        // Posting a bounty costs Credits up front, scaled by tier — this is
        // what makes it a real "bounty" (a wager) rather than a free
        // difficulty picker. Paid on StartBountyHunt, before the warp;
        // never refunded if the player leaves without the target finding
        // them (same no-penalty design as the rest of Bounty Hunt — the
        // penalty for backing out is just losing the credits already spent).
        public const int BountyHuntAcceptCostCreditsPerTier = 250;

        /// <summary>Credits required to post a bounty at the given tier. Public/static so the WebAPI can echo it back to the client for display without duplicating the formula.</summary>
        public static int BountyHuntAcceptCost(int tier) => BountyHuntAcceptCostCreditsPerTier * Math.Clamp(tier, 1, BountyHuntMaxRank);

        private readonly EventGroup _bountyHuntEvents = new();
        private readonly EventPointer<BountyHuntSpawnTickEvent> _bountyHuntSpawnTick = new();

        private bool _bountyHuntWarpPending;
        private ulong _bountyHuntHeroRef;
        private int _bountyHuntRank;
        private Region _bountyHuntRegion;
        private ulong _bountyHuntSpawnedId;
        private Event<EntityDeadGameEvent>.Action _bountyHuntDeadAction;

        /// <summary>
        /// Which Bounty Board slot (0-5) the in-flight hunt was launched
        /// from, or -1 for a personal-nemesis-roster hunt. See
        /// Player.BountyBoard.cs.
        /// </summary>
        private int _bountyHuntBoardSlot = -1;

        /// <summary>
        /// RegionPrototypeId (as ulong) of the arena the LAST Bounty Hunt
        /// warp landed the player in — excluded from the pick on the next
        /// hunt so two hunts in a row can't send them back into a region
        /// that may not have fully torn down. Snapshotted to/restored from
        /// MigrationData.LastBountyHuntRegionId since it needs to survive
        /// the very transfer it's about to inform.
        /// </summary>
        private ulong _lastBountyHuntRegionId;

        /// <summary>
        /// Minimum gap between the START of one Bounty Hunt warp and the
        /// next. Mitigation for a live-repro'd client freeze (2026-08-03):
        /// two hunts posted ~90s apart (post, lose, immediately post again)
        /// left the client hung mid-load on the second warp — the server
        /// itself behaved correctly throughout (clean, instant shutdown the
        /// moment the dead connection was detected, no sign of a stuck game
        /// thread), so this doesn't fix a proven server bug, it just slows
        /// down the one variable that was different about that incident:
        /// back-to-back full region generate/teardown cycles faster than
        /// normal play would ever produce.
        /// </summary>
        private const int BountyHuntMinIntervalMs = 15_000;
        private long _lastBountyHuntStartMs;

        /// <summary>
        /// Start a Bounty Hunt against an existing active (non-Defeated)
        /// nemesis roster entry at the given tier (1-10). Validates the same
        /// way SetBountyTarget does, then hands off to StartBountyHuntInternal
        /// for the actual cost/warp mechanics shared with Bounty Board hunts.
        /// </summary>
        public string StartBountyHunt(ulong heroRef, int rank)
        {
            NemesisEntry entry = null;
            foreach (var n in _nemeses) { if (n.HeroRef == heroRef) { entry = n; break; } }
            if (entry == null) return "that nemesis isn't on your roster";
            if (entry.Defeated) return "that nemesis is already defeated — pick an active one";

            return StartBountyHuntInternal(heroRef, rank, -1);
        }

        /// <summary>
        /// Shared cost/warp mechanics for both a personal-nemesis hunt
        /// (Player.BountyHunt.StartBountyHunt, boardSlot -1) and a Bounty
        /// Board hunt (Player.BountyBoard.StartBountyBoardHunt, boardSlot
        /// 0-5) — picks a random arena from Trial of the Impossible's own
        /// arena pool and warps. The actual spawn happens later, on
        /// arrival (see OnAvatarEnteredRegionForBountyHunt) — a
        /// cross-region transfer destroys this Game instance, so nothing
        /// beyond the MigrationData snapshot below survives the warp.
        /// </summary>
        private string StartBountyHuntInternal(ulong heroRef, int rank, int boardSlot)
        {
            if (rank < 1 || rank > BountyHuntMaxRank)
                return $"rank must be 1-{BountyHuntMaxRank}";

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "no avatar in world";

            long nowMs = Game?.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond ?? 0;
            long sinceLastMs = nowMs - _lastBountyHuntStartMs;
            if (_lastBountyHuntStartMs > 0 && sinceLastMs < BountyHuntMinIntervalMs)
            {
                int waitSec = (int)Math.Ceiling((BountyHuntMinIntervalMs - sinceLastMs) / 1000.0);
                return $"too soon after your last bounty hunt — wait {waitSec}s and try again";
            }

            RegionPrototypeId[] pool = GetValidTrialArenaPool();
            if (pool.Length == 0) return "no valid arena regions available";

            PrototypeId creditsProtoRef = GameDatabase.CurrencyGlobalsPrototype.Credits;
            int cost = boardSlot >= 0 ? BountyBoardAcceptCost(rank) : BountyHuntAcceptCost(rank);
            int currentCredits = Properties[PropertyEnum.Currency, creditsProtoRef];
            if (currentCredits < cost)
                return $"not enough credits — bounty costs {cost}, you have {currentCredits}";
            Properties.AdjustProperty(-cost, new(PropertyEnum.Currency, creditsProtoRef));

            // Exclude the region the last hunt landed in, if any and if the
            // pool is big enough to still leave a real choice — avoids
            // sending the player right back into a region that may not
            // have fully torn down from that last visit yet.
            RegionPrototypeId chosen;
            if (_lastBountyHuntRegionId != 0 && pool.Length > 1)
            {
                var filtered = new List<RegionPrototypeId>(pool.Length - 1);
                foreach (var r in pool) if ((ulong)r != _lastBountyHuntRegionId) filtered.Add(r);
                chosen = filtered.Count > 0 ? filtered[Game.Random.Next(filtered.Count)] : pool[Game.Random.Next(pool.Length)];
            }
            else
            {
                chosen = pool[Game.Random.Next(pool.Length)];
            }
            _lastBountyHuntRegionId = (ulong)chosen;
            _lastBountyHuntStartMs = nowMs;

            DetachBountyHuntDeadAction();
            _bountyHuntHeroRef = heroRef;
            _bountyHuntRank = rank;
            _bountyHuntBoardSlot = boardSlot;
            _bountyHuntRegion = null;
            _bountyHuntSpawnedId = 0;
            _bountyHuntWarpPending = true;

            avatar.TeleportToRegionFromWeb((ulong)chosen);

            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: hunt started on '{((PrototypeId)heroRef).GetName()}' tier {rank} (board slot {boardSlot}), warping to {chosen}");
            return $"hunt started on {((PrototypeId)heroRef).GetName()} (tier {rank}) — warping...";
        }

        /// <summary>
        /// Snapshot the pending Bounty Hunt warp onto MigrationData so it
        /// survives the cross-region Game-instance destroy/recreate. Called
        /// from PlayerConnection.BeginRegionTransfer — unconditionally,
        /// on EVERY transfer (not just a bounty-hunt-triggered one), since
        /// _lastBountyHuntRegionId needs to keep riding along through any
        /// unrelated hops the player takes between hunts, not just the
        /// hunt's own warp.
        /// </summary>
        internal void SnapshotBountyHuntForTransfer()
        {
            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;
            mig.LastBountyHuntRegionId = _lastBountyHuntRegionId;
            mig.LastBountyHuntStartMs = _lastBountyHuntStartMs;

            if (_bountyHuntWarpPending == false) return;
            mig.PendingBountyHuntWarp = true;
            mig.BountyHuntHeroRef = _bountyHuntHeroRef;
            mig.BountyHuntRank = _bountyHuntRank;
            mig.BountyHuntBoardSlot = _bountyHuntBoardSlot;
            _bountyHuntWarpPending = false; // this Game instance is going away
        }

        /// <summary>Called from Avatar.OnEnteredWorld on every region entry for a real (non-phantom) player avatar — mirrors Player.TrialOfImpossible.cs's OnAvatarEnteredRegionForTrial call site.</summary>
        internal void OnAvatarEnteredRegionForBountyHunt(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;

            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;
            _lastBountyHuntRegionId = mig.LastBountyHuntRegionId;
            _lastBountyHuntStartMs = mig.LastBountyHuntStartMs;

            if (mig.PendingBountyHuntWarp == false) return;

            mig.PendingBountyHuntWarp = false;
            _bountyHuntHeroRef = mig.BountyHuntHeroRef;
            _bountyHuntRank = mig.BountyHuntRank;
            _bountyHuntBoardSlot = mig.BountyHuntBoardSlot;
            _bountyHuntRegion = region;

            // Baseline reward reuses the existing Bounty Board claim flow —
            // fires automatically on kill via the corpse-cleanup tick
            // (avatar-type target) or TrackBossNemesisForRetire (boss-type
            // target), whichever applies. Set here (after arrival), not
            // before departure — _bountyTargetHeroRef is a plain Player
            // field with no MigrationData snapshot of its own (deliberately
            // short-lived per its own doc comment), so setting it before the
            // warp would just be lost when this Game instance is destroyed.
            // Board hunts skip this entirely — SetBountyTarget requires an
            // active Nemesis roster entry, which a board slot deliberately
            // isn't; board hunts pay out through the tier-scaled currency
            // (+ guaranteed BiS at rank 9-10) below instead.
            if (_bountyHuntBoardSlot < 0)
                SetBountyTarget(_bountyHuntHeroRef, BountyHuntBaselineLootTableRef);

            int removed = ClearArena(avatar);
            if (removed > 0)
                BountyHuntLogger.Info($"[BountyHunt] {GetName()}: arena sterilized — {removed} native entity(ies) removed");

            ScheduleBountyHuntSpawn();

            try { SendBannerLines($"🎯 Bounty Hunt: tier {_bountyHuntRank} — they're out here somewhere..."); } catch { }
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: arrived in {region.PrototypeName}, spawn scheduled");
        }

        private void ScheduleBountyHuntSpawn()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: ScheduleBountyHuntSpawn — no GameEventScheduler available, spawn will never fire");
                return;
            }
            if (_bountyHuntSpawnTick.IsValid) scheduler.CancelEvent(_bountyHuntSpawnTick);
            int delayMs = Game.Random.Next(BountyHuntSpawnMinDelayMs, BountyHuntSpawnMaxDelayMs);
            scheduler.ScheduleEvent(_bountyHuntSpawnTick, TimeSpan.FromMilliseconds(delayMs), _bountyHuntEvents);
            _bountyHuntSpawnTick.Get().Initialize(this);
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: spawn tick scheduled in {delayMs}ms");
        }

        private void OnBountyHuntSpawnTick()
        {
            if (_bountyHuntHeroRef == 0 || _bountyHuntRegion == null)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn tick fired with no hunt in flight (heroRef=0x{_bountyHuntHeroRef:X}, region={(_bountyHuntRegion == null ? "null" : _bountyHuntRegion.PrototypeName)}) — bailing");
                return;
            }

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false || avatar.Region != _bountyHuntRegion)
            {
                // Left before the hunt triggered — bounty flag stays set
                // (no penalty), but this specific hunt attempt is over.
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn tick bailed — avatar={(avatar == null ? "null" : "present")}, inWorld={avatar?.IsInWorld}, avatarRegion={avatar?.Region?.PrototypeName ?? "null"}, expectedRegion={_bountyHuntRegion.PrototypeName}");
                ClearBountyHuntState();
                return;
            }

            bool isBoss;
            int escapeCount = 0;
            int grudge = 0;
            string killerBase;

            if (_bountyHuntBoardSlot >= 0)
            {
                if (_bountyHuntBoardSlot >= _bountyBoard.Count)
                {
                    BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn tick bailed — board slot {_bountyHuntBoardSlot} out of range (board has {_bountyBoard.Count} slots)");
                    ClearBountyHuntState();
                    return;
                }
                BountyBoardEntry slot = _bountyBoard[_bountyHuntBoardSlot];
                if (slot.HeroRef != _bountyHuntHeroRef || slot.Defeated || slot.Fled)
                {
                    BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn tick bailed — board slot {_bountyHuntBoardSlot} mismatch (slot.HeroRef=0x{slot.HeroRef:X}, expected=0x{_bountyHuntHeroRef:X}, Defeated={slot.Defeated}, Fled={slot.Fled})");
                    ClearBountyHuntState();
                    return;
                }
                isBoss = slot.IsBoss;
                killerBase = string.IsNullOrEmpty(slot.LastKillerName) ? FriendlyNameFromRef((PrototypeId)_bountyHuntHeroRef) : slot.LastKillerName;
            }
            else
            {
                NemesisEntry entry = null;
                foreach (var n in _nemeses) { if (n.HeroRef == _bountyHuntHeroRef) { entry = n; break; } }
                if (entry == null || entry.Defeated)
                {
                    BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn tick bailed — nemesis roster entry for 0x{_bountyHuntHeroRef:X} {(entry == null ? "not found" : "already Defeated")}");
                    ClearBountyHuntState();
                    return;
                }
                isBoss = entry.IsBoss;
                escapeCount = entry.EscapeCount;
                grudge = GrudgeScore(entry);
                killerBase = string.IsNullOrEmpty(entry.LastKillerName) ? "Phantom" : entry.LastKillerName;
            }

            string suffix = NemesisSuffixForRank(_bountyHuntRank);
            string stars = new string('★', Math.Clamp(_bountyHuntRank, 1, BountyHuntMaxRank));
            string displayName = string.IsNullOrEmpty(suffix) ? $"{stars} {killerBase}" : $"{stars} {killerBase} {suffix}";

            ulong id;
            string err;
            if (isBoss)
            {
                id = SpawnCuratedBoss(avatar, (PrototypeId)_bountyHuntHeroRef, out err,
                    BossNemesisExtraHealthMultForRank(_bountyHuntRank), BossNemesisExtraDamageMultForRank(_bountyHuntRank));
                if (id != 0) TrackBossNemesisForRetire(id, _bountyHuntHeroRef, _bountyHuntRegion);
            }
            else
            {
                id = avatar.SpawnNemesisPhantomHero((PrototypeId)_bountyHuntHeroRef, 0, displayName, _bountyHuntRank, out err, escapeCount, grudge);
            }

            if (id == 0)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn failed for {((PrototypeId)_bountyHuntHeroRef).GetName()}: {err}");
                ClearBountyHuntState();
                return;
            }

            _bountyHuntSpawnedId = id;
            DetachBountyHuntDeadAction();
            _bountyHuntDeadAction = OnBountyHuntEntityDead;
            _bountyHuntRegion.EntityDeadEvent.AddActionBack(_bountyHuntDeadAction);

            try { SendBannerLines($"🎯 {displayName} has found you!"); } catch { }
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: {displayName} spawned (id={id:X}), tier {_bountyHuntRank}");
        }

        /// <summary>
        /// Fires when the tracked Bounty Hunt spawn dies. Personal-nemesis
        /// hunts (boardSlot -1) still pay out immediately, same as always —
        /// there's no "collect" UI for those. Board hunts instead just mark
        /// the slot Defeated here; the tier-scaled currency + guaranteed
        /// BiS grant moves to CollectBountyBoardReward (Player.
        /// BountyBoard.cs), fired by an explicit "Collect Rewards" click so
        /// the reward visibly lands rather than vanishing mid-fight-cleanup.
        /// </summary>
        private void OnBountyHuntEntityDead(in EntityDeadGameEvent evt)
        {
            if (evt.Defender == null || evt.Defender.Id != _bountyHuntSpawnedId) return;

            int tier = _bountyHuntRank;
            ulong heroRef = _bountyHuntHeroRef;
            int boardSlot = _bountyHuntBoardSlot;
            DetachBountyHuntDeadAction();

            try
            {
                if (boardSlot >= 0)
                {
                    ResolveBountyBoardWin(boardSlot, heroRef);
                    try { SendBannerLines("💰 Bounty defeated — collect your reward from the Bounty Board!"); } catch { }
                    BountyHuntLogger.Info($"[BountyHunt] {GetName()}: board slot {boardSlot} defeated on '{((PrototypeId)heroRef).GetName()}', reward pending collection");
                }
                else
                {
                    var currencyGlobals = GameDatabase.CurrencyGlobalsPrototype;
                    Properties.AdjustProperty(BountyHuntEternitySplintersPerTier * tier, new(PropertyEnum.Currency, currencyGlobals.EternitySplinters));
                    Properties.AdjustProperty(BountyHuntCubeShardsPerTier * tier, new(PropertyEnum.Currency, currencyGlobals.CubeShards));
                    Properties.AdjustProperty(BountyHuntLegendaryMarksPerTier * tier, new(PropertyEnum.Currency, currencyGlobals.LegendaryMarks));

                    if (tier >= BountyHuntGuaranteedBisTier)
                    {
                        Avatar avatar = CurrentAvatar;
                        if (avatar != null && PhantomBiSData.TryGetLoadout(avatar.PrototypeDataRef, Game, out var slots) && slots.Count > 0)
                        {
                            var pool = new List<PrototypeId>(slots.Values);
                            PrototypeId itemRef = pool[Game.Random.Next(pool.Count)];
                            Game.LootManager.SpawnItem(itemRef, LootContext.Drop, this, evt.Defender);
                        }
                    }

                    try { SendBannerLines($"💰 Bounty claimed — tier {tier} payout!"); } catch { }
                    BountyHuntLogger.Info($"[BountyHunt] {GetName()}: bounty tier {tier} claimed on '{((PrototypeId)heroRef).GetName()}'");
                }
            }
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: reward grant failed: {ex.Message}");
            }
            finally
            {
                ClearBountyHuntState();
            }
        }

        /// <summary>
        /// Called from Avatar.TryRegisterNemesisKill whenever the PLAYER
        /// dies to some agent — no-ops unless that agent is the specific
        /// entity this player's in-flight Bounty Hunt spawned
        /// (killerAgent.Id == _bountyHuntSpawnedId). Always clears the hunt
        /// state on a match — losing means the credits already spent are
        /// gone and the player must pay again to re-engage, whether this
        /// was a personal-nemesis hunt or a board hunt. Board hunts
        /// additionally escalate/flee that slot via ResolveBountyBoardLoss.
        /// </summary>
        internal void OnBountyHuntLoss(Agent killerAgent)
        {
            if (killerAgent == null || _bountyHuntSpawnedId == 0 || killerAgent.Id != _bountyHuntSpawnedId) return;

            int boardSlot = _bountyHuntBoardSlot;
            DetachBountyHuntDeadAction();
            ClearBountyHuntState();

            if (boardSlot >= 0) ResolveBountyBoardLoss(boardSlot);
        }

        private void DetachBountyHuntDeadAction()
        {
            if (_bountyHuntRegion != null && _bountyHuntDeadAction != null)
                _bountyHuntRegion.EntityDeadEvent.RemoveAction(_bountyHuntDeadAction);
            _bountyHuntDeadAction = null;
        }

        private void ClearBountyHuntState()
        {
            DetachBountyHuntDeadAction();
            _bountyHuntHeroRef = 0;
            _bountyHuntRank = 0;
            _bountyHuntBoardSlot = -1;
            _bountyHuntRegion = null;
            _bountyHuntSpawnedId = 0;
        }

        /// <summary>Called from Player.OnDeallocate so dangling event subscriptions/scheduled ticks can't outlive the player (logout/disconnect mid-hunt).</summary>
        internal void UnsubscribeBountyHuntTracking()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _bountyHuntSpawnTick.IsValid) scheduler.CancelEvent(_bountyHuntSpawnTick);
            ClearBountyHuntState();
        }

        private sealed class BountyHuntSpawnTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnBountyHuntSpawnTick();
        }
    }
}
