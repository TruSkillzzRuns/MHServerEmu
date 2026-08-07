using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Powers;
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
        private readonly EventPointer<BountyHuntHazardTickEvent> _bountyHuntHazardTick = new();
        private readonly EventPointer<PhantomRequiemTickEvent> _phantomRequiemTick = new();

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
        /// True from the moment the player is killed by their own tracked
        /// Bounty Hunt spawn until their next DoDeathRelease consumes it —
        /// redirects that release to Avengers Tower instead of the normal
        /// checkpoint/corpse release, same mechanism Trial of the
        /// Impossible's 3-death limit already uses (Avatar.DoDeathRelease).
        /// Without this, the player could just release at the nearby
        /// checkpoint and carry on in the (now-sterilized) arena without
        /// ever having to pay to re-engage — the whole point of the
        /// "pay again to try once more" rule.
        /// </summary>
        internal bool IsBountyHuntDeathPending { get; private set; }

        /// <summary>
        /// True while a BOUNTY BOARD hunt is in flight (board slot 0-5). False
        /// for personal-nemesis hunts and for normal play. This is what scopes
        /// the squad-size / solo-only rule to Bounty Board mode - everywhere
        /// else phantom counts are unrestricted, exactly as before.
        /// </summary>
        internal bool IsBountyBoardHuntActive => _bountyHuntBoardSlot >= 0;

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

            // Board hunts run under the board's current theme (Bounty Board
            // mode only) — arena comes from that theme's own region list
            // instead of the full 57-arena pool. Personal-nemesis hunts
            // (boardSlot < 0) always use the full pool, unchanged.
            int themeIndex = boardSlot >= 0 ? _bountyThemeIndex : -1;

            // Bounty Board only: a bounty is a 3-member fight (you + 2
            // phantoms, or a party of up to 3 real players). Checked here so
            // the player is told BEFORE paying, rather than having their squad
            // silently trimmed on arrival. Personal-nemesis hunts skip this.
            if (boardSlot >= 0)
            {
                string boardSquadGate = Avatar.CheckPhantomSquadGateForBountyBoard(this, spawningAnother: false);
                if (boardSquadGate != null) return boardSquadGate;
            }

            RegionPrototypeId[] pool;
            if (themeIndex >= 0)
            {
                var themeRegions = GetThemeRegions(themeIndex);
                // A theme whose arenas don't exist on this version falls back
                // to the full pool rather than blocking the hunt outright.
                pool = themeRegions.Count > 0 ? themeRegions.ToArray() : GetBountyHuntArenaPool();
            }
            else
            {
                pool = GetBountyHuntArenaPool();
            }
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
            // Snapshot the theme for THIS hunt so a board re-roll while the
            // hunt is in flight can't swap the arena/powers/costume mid-fight.
            _bountyHuntThemeIndex = themeIndex;
            // Any portal left over from the previous hunt goes away now.
            DespawnBountyReturnPortal();
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
            mig.BountyHuntThemeIndex = _bountyHuntThemeIndex;
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
            _bountyHuntThemeIndex = mig.BountyHuntThemeIndex;
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

            // Themed board hunts refill the just-sterilized arena with
            // faction-matched mobs. Board hunts only — a personal-nemesis hunt
            // (_bountyHuntBoardSlot < 0) or an untheme(d) board keeps the
            // existing empty-arena behaviour exactly as before.
            if (_bountyHuntBoardSlot >= 0 && _bountyHuntThemeIndex >= 0)
                RepopulateThemedArena(avatar, region, _bountyHuntThemeIndex, _bountyHuntRank);

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
                // Themed board hunts dress the target in the theme's costume
                // (e.g. Thing/FearItself = Angrir). 0 = the existing random-
                // costume behaviour, which is what every non-themed and every
                // personal-nemesis hunt still passes.
                ulong themedCostumeRef = ThemedCostumeForTarget(_bountyHuntThemeIndex, _bountyHuntHeroRef);
                id = avatar.SpawnNemesisPhantomHero((PrototypeId)_bountyHuntHeroRef, 0, displayName, _bountyHuntRank, out err, escapeCount, grudge, themedCostumeRef);
            }

            if (id == 0)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: spawn failed for {((PrototypeId)_bountyHuntHeroRef).GetName()}: {err}");
                ClearBountyHuntState();
                return;
            }

            // Board-only extra toughness, stacked ON TOP of the normal
            // rank curve — deliberately NOT folded into
            // NemesisHealthMultForRank/NemesisDmgBoostForRank/
            // BossNemesisExtraHealthMultForRank/BossNemesisExtraDamageMultForRank
            // themselves, since those are shared by Endless Wave, Rogue
            // Encounter, and personal-nemesis Bounty Hunt too — this only
            // applies when _bountyHuntBoardSlot >= 0, so every other mode's
            // difficulty is completely untouched. Confirmed live 2026-08-03:
            // a rank 10 board bounty felt no tougher than the personal-hunt
            // equivalent and its reward didn't feel like a real BiS drop —
            // this is the "way stronger" half of that fix; CollectBountyBoardReward
            // is the loot-quality half.
            if (_bountyHuntBoardSlot >= 0)
            {
                ApplyBountyBoardExtraScaling(id, _bountyHuntRank);
                ScheduleBountyHuntHazardTick();
                SchedulePhantomRequiemTick();
            }

            _bountyHuntSpawnedId = id;
            DetachBountyHuntDeadAction();
            _bountyHuntDeadAction = OnBountyHuntEntityDead;
            _bountyHuntRegion.EntityDeadEvent.AddActionBack(_bountyHuntDeadAction);

            try { SendBannerLines($"🎯 {displayName} has found you!"); } catch { }
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: {displayName} spawned (id={id:X}), tier {_bountyHuntRank}");
        }

        /// <summary>
        /// Extra HP/damage multiplier applied ON TOP of the entity's normal
        /// rank curve, board hunts only. Barely moves rank 1 (board bounties
        /// should still feel "Trivial"); scales hard toward rank 10 so the
        /// top of the board is a genuinely dangerous fight, not just a
        /// bigger number on the same curve every other mode already uses.
        ///   Rank        1     3     5     7     10
        ///   HP mult     1.0x  2.0x  3.0x  4.0x  5.5x
        ///   Dmg mult    1.0x  1.7x  2.4x  3.1x  4.15x
        /// </summary>
        private static float BountyBoardExtraHealthMultForRank(int rank) => 1f + (Math.Clamp(rank, 1, EndlessMaxRank) - 1) * 0.5f;
        private static float BountyBoardExtraDamageMultForRank(int rank) => 1f + (Math.Clamp(rank, 1, EndlessMaxRank) - 1) * 0.35f;

        private void ApplyBountyBoardExtraScaling(ulong entityId, int rank)
        {
            try
            {
                WorldEntity entity = Game?.EntityManager?.GetEntity<WorldEntity>(entityId);
                if (entity == null) return;

                float extraHealthMult = BountyBoardExtraHealthMultForRank(rank);
                float extraDamageMult = BountyBoardExtraDamageMultForRank(rank);
                if (extraHealthMult <= 1f && extraDamageMult <= 1f) return; // rank 1 — nothing to add

                float currentHealthMult = entity.Properties[PropertyEnum.HealthMaxMult];
                entity.Properties[PropertyEnum.HealthMaxMult] = (currentHealthMult <= 0f ? 1f : currentHealthMult) * extraHealthMult;
                entity.Properties[PropertyEnum.Health] = entity.Properties[PropertyEnum.HealthMax];

                float currentDamageMult = entity.Properties[PropertyEnum.DamageMult];
                entity.Properties[PropertyEnum.DamageMult] = (currentDamageMult <= 0f ? 1f : currentDamageMult) * extraDamageMult;

                BountyHuntLogger.Info($"[BountyHunt] {GetName()}: board extra scaling applied — rank {rank}, HP x{extraHealthMult:0.00}, DMG x{extraDamageMult:0.00}");
            }
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: board extra scaling failed: {ex.Message}");
            }
        }

        // ---------------- Board hazards: random environmental effects ----------------
        //
        // Reuses the exact real Danger Room ground-hazard hotspots (fire/ice/
        // poison patches, traps) Player.WaveDirector.cs's Endless Challenge
        // hazard tick already spawns — same technique (a casterless
        // HotspotPrototype WorldEntity via EntitySettings+CreateEntity, no
        // visible enemy), same s_endlessHazardHotspots pool (this file and
        // Player.WaveDirector.cs are both partial Player, so it's directly
        // visible here without duplicating the array). Board hunts only
        // (_bountyHuntBoardSlot >= 0) — personal-nemesis Bounty Hunt is
        // untouched.
        //
        // Gated by rank band per 2026-08-03 design: none at rank 1-2 (keep
        // "Trivial"/"Easy" actually easy), light presence from rank 3
        // ("lower to mid" bounties) up through a much heavier presence at
        // rank 7-10 ("med-high" bounties).

        private const int BountyHuntHazardMinRank = 3;
        private const int BountyHuntHazardLifespanSec = 10;
        private const float BountyHuntHazardPlacementSlack = 300f;
        private const float BountyHuntHazardBoundsInset = 0.15f;

        /// <summary>Per-tick (chance to fire, hazard count, next-tick delay range) for the given rank — see the rank-band comment above.</summary>
        private static (double Chance, int MinCount, int MaxCount, int MinDelayMs, int MaxDelayMs) BountyHuntHazardProfileForRank(int rank)
        {
            if (rank >= 7) return (0.65, 2, 3, 12_000, 20_000);  // high (med-high bounties): frequent, heavier
            if (rank >= BountyHuntHazardMinRank) return (0.35, 1, 1, 20_000, 32_000); // low-mid/medium: occasional, light
            return (0.0, 0, 0, 0, 0); // rank 1-2: no hazards
        }

        /// <summary>
        /// Arenas a Bounty Hunt may warp into. This is the shared Trial arena
        /// pool minus regions that are broken for combat, filtered on a COPY so
        /// Trial of the Impossible's own pool is not modified.
        ///
        /// CH0305ReconPostRegion (S.H.I.E.L.D. Recon Post) is listed here as
        /// defence in depth: nothing in that region can attack at all,
        /// including the player, so a bounty there is unwinnable (reported live
        /// 2026-08-03). It has ALSO been removed from s_trialArenaPool itself,
        /// which fixes Trial of the Impossible for the same reason — this entry
        /// just guarantees Bounty Board stays protected if it is ever re-added
        /// upstream.
        /// </summary>
        private static readonly RegionPrototypeId[] s_bountyHuntArenaBlacklist =
        {
            RegionPrototypeId.CH0305ReconPostRegion,
        };

        private static RegionPrototypeId[] s_bountyHuntArenaPool;
        private static readonly object s_bountyHuntArenaPoolLock = new();

        private static RegionPrototypeId[] GetBountyHuntArenaPool()
        {
            if (s_bountyHuntArenaPool != null) return s_bountyHuntArenaPool;
            lock (s_bountyHuntArenaPoolLock)
            {
                if (s_bountyHuntArenaPool != null) return s_bountyHuntArenaPool;

                var trialPool = GetValidTrialArenaPool();
                var filtered = new List<RegionPrototypeId>(trialPool.Length);
                foreach (RegionPrototypeId id in trialPool)
                {
                    bool blocked = false;
                    foreach (RegionPrototypeId bad in s_bountyHuntArenaBlacklist)
                        if (id == bad) { blocked = true; break; }
                    if (blocked == false) filtered.Add(id);
                }

                s_bountyHuntArenaPool = filtered.Count > 0 ? filtered.ToArray() : trialPool;
                BountyHuntLogger.Info($"[BountyHunt] arena pool: {s_bountyHuntArenaPool.Length} of {trialPool.Length} trial arenas (blacklist removed {trialPool.Length - s_bountyHuntArenaPool.Length})");
                return s_bountyHuntArenaPool;
            }
        }

        private void ScheduleBountyHuntHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            var profile = BountyHuntHazardProfileForRank(_bountyHuntRank);
            if (profile.Chance <= 0.0) return; // rank too low — hazards off entirely
            if (_bountyHuntHazardTick.IsValid) scheduler.CancelEvent(_bountyHuntHazardTick);
            int delayMs = Game.Random.Next(profile.MinDelayMs, profile.MaxDelayMs);
            scheduler.ScheduleEvent(_bountyHuntHazardTick, TimeSpan.FromMilliseconds(delayMs), _bountyHuntEvents);
            _bountyHuntHazardTick.Get().Initialize(this);
        }

        private void CancelBountyHuntHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _bountyHuntHazardTick.IsValid) scheduler.CancelEvent(_bountyHuntHazardTick);
        }

        private void OnBountyHuntHazardTick()
        {
            try
            {
                // Hunt already resolved (win/loss/left) between scheduling
                // and firing — nothing to do, and no more ticks to chain.
                if (_bountyHuntSpawnedId == 0 || _bountyHuntBoardSlot < 0) return;

                var profile = BountyHuntHazardProfileForRank(_bountyHuntRank);
                if (profile.Chance <= 0.0) return;

                if (Game.Random.NextDouble() < profile.Chance)
                {
                    Avatar avatar = CurrentAvatar;
                    if (avatar != null && avatar.IsInWorld && avatar.Region == _bountyHuntRegion)
                    {
                        Region region = avatar.Region;
                        var rng = Game.Random;

                        var regionAabb = region.Aabb;
                        float insetX = regionAabb.Width * BountyHuntHazardBoundsInset;
                        float insetY = regionAabb.Length * BountyHuntHazardBoundsInset;
                        float minX = regionAabb.Min.X + insetX, maxX = regionAabb.Max.X - insetX;
                        float minY = regionAabb.Min.Y + insetY, maxY = regionAabb.Max.Y - insetY;

                        int spawnCount = rng.Next(profile.MinCount, profile.MaxCount + 1);
                        var spawnedNames = new List<string>();

                        for (int i = 0; i < spawnCount; i++)
                        {
                            ulong hotspotRef = s_endlessHazardHotspots[rng.Next(s_endlessHazardHotspots.Length)];
                            var hotspotProto = ((PrototypeId)hotspotRef).As<WorldEntityPrototype>();
                            if (hotspotProto == null) continue;

                            float x = minX + (float)(rng.NextDouble() * Math.Max(0f, maxX - minX));
                            float y = minY + (float)(rng.NextDouble() * Math.Max(0f, maxY - minY));
                            Vector3 candidateCenter = new(x, y, avatar.RegionLocation.Position.Z);

                            MHServerEmu.Games.Entities.Bounds entityBounds = new();
                            entityBounds.InitializeFromPrototype(hotspotProto.Bounds);
                            entityBounds.Center = candidateCenter;

                            if (region.ChoosePositionAtOrNearPoint(ref entityBounds, avatar.Locomotor.PathFlags,
                                PositionCheckFlags.CanBeBlockedEntity, BlockingCheckFlags.None,
                                BountyHuntHazardPlacementSlack, out Vector3 pos, maxPositionTests: 32) == false)
                            {
                                continue;
                            }

                            pos = RegionLocation.ProjectToFloor(region, pos);

                            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                            settings.EntityRef = (PrototypeId)hotspotRef;
                            settings.Position = pos;
                            settings.Orientation = Orientation.Zero;
                            settings.RegionId = region.Id;
                            settings.Lifespan = TimeSpan.FromSeconds(BountyHuntHazardLifespanSec);

                            WorldEntity hazard = Game.EntityManager.CreateEntity(settings) as WorldEntity;
                            if (hazard != null)
                            {
                                string name = LeafHeroName((PrototypeId)hotspotRef).Replace("Hotspot", "").Replace("Entity", "");
                                spawnedNames.Add(name);
                            }
                        }

                        if (spawnedNames.Count > 0)
                        {
                            // No banner — hazards and requiem strikes are
                            // meant to be read off the arena itself in this
                            // mode, not announced. Log line kept for diagnostics.
                            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: board hazard(s) spawned — {string.Join(", ", spawnedNames)} (rank {_bountyHuntRank})");
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: hazard tick failed: {ex.Message}");
            }
            finally
            {
                // Keep chaining as long as the hunt is still in flight —
                // ClearBountyHuntState (win/loss/leave) is what actually
                // stops this via CancelBountyHuntHazardTick.
                if (_bountyHuntSpawnedId != 0 && _bountyHuntBoardSlot >= 0)
                    ScheduleBountyHuntHazardTick();
            }
        }

        private sealed class BountyHuntHazardTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnBountyHuntHazardTick();
        }

        // Requiem strikes run on their own fast cadence (every ~2s), fully
        // independent of the much slower ground-hotspot hazard tick they
        // originally piggybacked on — riding that tick meant a strike only
        // every 12-20s, far too sparse to read as a live barrage.
        private const int PhantomRequiemIntervalMs = 2_000;

        private void SchedulePhantomRequiemTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_bountyHuntRank < PhantomRequiemMinRank) return; // low/mid ranks: hotspot hazards only
            if (_phantomRequiemTick.IsValid) scheduler.CancelEvent(_phantomRequiemTick);
            scheduler.ScheduleEvent(_phantomRequiemTick, TimeSpan.FromMilliseconds(PhantomRequiemIntervalMs), _bountyHuntEvents);
            _phantomRequiemTick.Get().Initialize(this);
        }

        private void CancelPhantomRequiemTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _phantomRequiemTick.IsValid) scheduler.CancelEvent(_phantomRequiemTick);
        }

        private void OnPhantomRequiemTick()
        {
            try
            {
                if (_bountyHuntSpawnedId == 0 || _bountyHuntBoardSlot < 0) return;
                if (_bountyHuntRank < PhantomRequiemMinRank) return;

                Avatar avatar = CurrentAvatar;
                if (avatar != null && avatar.IsInWorld && avatar.Region == _bountyHuntRegion)
                    FirePhantomRequiemStrikes(avatar, avatar.Region, 1);
            }
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: requiem tick failed: {ex.Message}");
            }
            finally
            {
                if (_bountyHuntSpawnedId != 0 && _bountyHuntBoardSlot >= 0)
                    SchedulePhantomRequiemTick();
            }
        }

        private sealed class PhantomRequiemTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnPhantomRequiemTick();
        }

        // ---------------- Board hazards: real-power ambient strikes ----------------
        //
        // "Requiem" strikes are a second, independent hazard layer only for
        // med-high rank board hunts (rank >= PhantomRequiemMinRank): a real
        // EnemyPowers power (an actual ice orb, fireball, etc. from the
        // game's own data) fired at the player's position from an invisible,
        // AI-disabled, harmless throwaway body instead of a scripted ground
        // hotspot. Original implementation — spawns one of the game's own
        // hazard caster entities, assigns a random real EnemyPowers missile
        // power to it, fires it once at the avatar, then lets it expire via
        // the native ResetLifespan timer. Every step is best-effort and
        // independently wrapped — a power that fails to assign/activate is
        // simply skipped, never crashes the tick.
        //
        // Caster body choice matters and was gotten wrong first: the initial
        // attempt used Entity/Characters/Mobs/test/PracticeDummy, a genuinely
        // VISIBLE and KILLABLE training dummy (Rank=Popcorn, its own
        // DisplayName plate, a real dummy UnrealClass), and tried to patch it
        // invisible/immortal with per-instance Properties after spawning.
        // That does not work — confirmed live 2026-08-03 the dummies rendered
        // in-world and the bounty target simply killed them before they could
        // fire, because the prototype's own Rank and Properties are applied
        // during entity initialization and win over settings patched around
        // them. The real game already ships purpose-built casters for exactly
        // this job under Powers/DangerRoomModifierPowers/HazardPowers/ (they
        // are what the live Danger Room WreckingBall / FallingDebris /
        // LightningStorm hazards cast from — players see the hazard, never a
        // caster body). Verified live via /webapi/protoeditor/fields, each
        // one has, in the prototype data itself: Rank=Mods/Ranks/
        // InvulnerablePet, DisplayName=0 (no name plate), HealthBase=0, and
        // Untargetable + Unaffectable + Invulnerable + InvalidBounceTarget +
        // NoForcedMovement all true, plus Alliance=Entity/Alliances/
        // Enemies.prototype (hostile to players only — so their powers hit
        // the player, phantom heroes and team-ups, and nothing else).
        // Nothing has to be patched on afterwards, and nothing can kill them.

        private const int PhantomRequiemMinRank = 7;
        private const float PhantomRequiemSpawnMaxDistance = 250f;
        private const int PhantomRequiemCasterLifespanMs = 2_500;

        /// <summary>
        /// The game's own invisible hazard-power caster bodies. All three are
        /// Alliance=Enemies; the sibling ThunderstormCasterEntity is
        /// deliberately excluded because it is Alliance=Friendlies and would
        /// fire at the wrong side.
        /// </summary>
        private static readonly string[] PhantomRequiemCasterBodyPaths =
        {
            "Powers/DangerRoomModifierPowers/HazardPowers/WreckingBallCasterEntity.prototype",
            "Powers/DangerRoomModifierPowers/HazardPowers/FallingDebrisCasterEntity.prototype",
            "Powers/DangerRoomModifierPowers/HazardPowers/LightningStormCasterEntity.prototype",
        };

        private static List<PrototypeId> s_phantomRequiemCasterBodyRefs;
        private static readonly object s_phantomRequiemCasterBodyLock = new();

        /// <summary>Resolves and caches whichever hazard caster bodies exist in the loaded data (paths are checked per game version rather than assumed).</summary>
        private static List<PrototypeId> GetPhantomRequiemCasterBodyRefs()
        {
            if (s_phantomRequiemCasterBodyRefs != null) return s_phantomRequiemCasterBodyRefs;
            lock (s_phantomRequiemCasterBodyLock)
            {
                if (s_phantomRequiemCasterBodyRefs != null) return s_phantomRequiemCasterBodyRefs;

                var refs = new List<PrototypeId>(PhantomRequiemCasterBodyPaths.Length);
                foreach (string path in PhantomRequiemCasterBodyPaths)
                {
                    PrototypeId bodyRef = GameDatabase.GetPrototypeRefByName(path);
                    if (bodyRef != PrototypeId.Invalid && bodyRef.As<AgentPrototype>() != null)
                        refs.Add(bodyRef);
                }

                s_phantomRequiemCasterBodyRefs = refs;
                BountyHuntLogger.Info($"[BountyHunt] Requiem caster bodies resolved: {refs.Count}/{PhantomRequiemCasterBodyPaths.Length}");
                return refs;
            }
        }

        private static List<PrototypeId> s_phantomRequiemPowerPool;
        private static readonly object s_phantomRequiemPowerPoolLock = new();

        /// <summary>
        /// Every real non-abstract MissilePowerPrototype under
        /// Powers/EnemyPowers/ — actual thrown/launched projectiles (ice
        /// orbs, fireballs, etc.), not the full EnemyPowers pool. Deliberately
        /// narrower than "every power" for two reasons confirmed live
        /// 2026-08-03: (1) generic/buff/condition/proc powers (auras, heals,
        /// on-death triggers) fire successfully but have no visible strike —
        /// nothing for the player to see or react to; (2) SummonPowerPrototype
        /// entries spawn a real, persistent extra creature into the world
        /// (e.g. a live Hydra from ViperSummonHydra) — the opposite of an
        /// ephemeral environmental strike. A missile power's projectile visual
        /// is tied to the missile's own prototype, not the caster's model, so
        /// it renders correctly even fired from a generic proxy body. A pick
        /// that still fails to assign/activate at runtime is simply skipped.
        /// </summary>
        private static List<PrototypeId> GetPhantomRequiemPowerPool()
        {
            if (s_phantomRequiemPowerPool != null) return s_phantomRequiemPowerPool;
            lock (s_phantomRequiemPowerPoolLock)
            {
                if (s_phantomRequiemPowerPool != null) return s_phantomRequiemPowerPool;

                var pool = new List<PrototypeId>(1_500);
                foreach (PrototypeId powerRef in DataDirectory.Instance.IteratePrototypesInHierarchy<MissilePowerPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (powerRef == PrototypeId.Invalid) continue;

                    string path = GameDatabase.GetPrototypeName(powerRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("Powers/EnemyPowers/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // NormalPower only. Measured live 2026-08-03: 67 of the 278
                    // EnemyPowers missile powers are PowerCategoryType.ComboEffect
                    // — sub-powers that only ever exist as one step of a parent
                    // power's combo sequence, never activated on their own.
                    // Firing those standalone is invalid use: PowerCollection.
                    // OnOwnerExitedWorld skips unassigning combo effects, so
                    // every expiring caster that held one tripped the
                    // "_owner is Avatar" Verify (visible in the log on exactly
                    // the strike cadence), and the client was being sent
                    // activations for powers it only expects mid-combo — the
                    // prime suspect for the client-side fatal crash.
                    var powerProto = powerRef.As<PowerPrototype>();
                    if (powerProto == null) continue;
                    if (Power.GetPowerCategory(powerProto) != PowerCategoryType.NormalPower) continue;

                    pool.Add(powerRef);
                }

                s_phantomRequiemPowerPool = pool;
                BountyHuntLogger.Info($"[BountyHunt] Requiem power pool built: {pool.Count} power(s)");
                return pool;
            }
        }

        /// <summary>
        /// Spawns up to <paramref name="strikeCount"/> hazard casters near the
        /// avatar, each firing one random real EnemyPowers missile power at
        /// the avatar's position. Fully ephemeral — the caster bodies are the
        /// game's own invisible/invulnerable hazard casters (see the block
        /// comment above), their AI is disabled, and they self-expire, so
        /// nothing here can leak into story or other game content.
        /// </summary>
        private void FirePhantomRequiemStrikes(Avatar avatar, Region region, int strikeCount)
        {
            var casterBodyRefs = GetPhantomRequiemCasterBodyRefs();
            if (casterBodyRefs.Count == 0) return;

            // Themed board hunts fire only their theme's powers (e.g. Winter's
            // Wrath = Boss/Blizzard + FrostGiants + FrostGolem). Untheme(d) and
            // personal-nemesis hunts use the full 211-power pool as before.
            var pool = _bountyHuntThemeIndex >= 0
                ? GetThemePowerPool(_bountyHuntThemeIndex)
                : GetPhantomRequiemPowerPool();
            if (pool.Count == 0) return;

            var rng = Game.Random;
            var struckNames = new List<string>();

            for (int i = 0; i < strikeCount; i++)
            {
                Agent caster = null;
                try
                {
                    var casterProto = casterBodyRefs[rng.Next(casterBodyRefs.Count)].As<AgentPrototype>();
                    if (casterProto == null) continue;

                    if (EntityHelper.GetSpawnPositionNearAvatar(avatar, region, casterProto.Bounds, PhantomRequiemSpawnMaxDistance, out Vector3 casterPos) == false)
                        continue;

                    Orientation orientation = Orientation.FromDeltaVector(avatar.RegionLocation.Position - casterPos);

                    // Built here rather than via EntityHelper.CreateAgent only
                    // so AI never starts and the level matches the avatar's —
                    // the hidden/untargetable/invulnerable behaviour all comes
                    // from the caster prototype itself, not from anything
                    // patched on here (see the block comment above for why
                    // patching a visible prototype does not work).
                    using EntitySettings casterSettings = ObjectPoolManager.Instance.Get<EntitySettings>();
                    casterSettings.EntityRef = casterProto.DataRef;
                    casterSettings.Position = casterPos;
                    casterSettings.Orientation = orientation;
                    casterSettings.RegionId = region.Id;

                    using PropertyCollection casterProps = ObjectPoolManager.Instance.Get<PropertyCollection>();
                    casterProps[PropertyEnum.CharacterLevel] = avatar.CharacterLevel;
                    casterProps[PropertyEnum.CombatLevel] = avatar.CharacterLevel;
                    casterProps[PropertyEnum.AIStartsEnabled] = false;
                    casterSettings.Properties = casterProps;

                    caster = Game.EntityManager.CreateEntity(casterSettings) as Agent;
                    if (caster == null) continue;

                    // Clear Dormant so the caster is simulated — CanActivatePower
                    // hard-fails with OwnerNotSimulated otherwise.
                    caster.Properties[PropertyEnum.Dormant] = false;
                    caster.AIController?.SetIsEnabled(false);

                    PrototypeId powerRef = pool[rng.Next(pool.Count)];
                    Power power = caster.AssignPower(powerRef, new PowerIndexProperties(0, avatar.CharacterLevel, avatar.CharacterLevel));
                    if (power == null)
                    {
                        BountyHuntLogger.Info($"[BountyHunt] requiem: AssignPower failed for {LeafHeroName(powerRef)}");
                        caster.Destroy();
                        continue;
                    }

                    Vector3 targetPos = avatar.RegionLocation.Position;
                    PowerUseResult canUse = caster.CanActivatePower(power, avatar.Id, targetPos);
                    if (canUse != PowerUseResult.Success)
                    {
                        BountyHuntLogger.Info($"[BountyHunt] requiem: CanActivatePower={canUse} for {LeafHeroName(powerRef)}");
                        caster.Destroy();
                        continue;
                    }

                    var activation = new PowerActivationSettings(avatar.Id, targetPos, caster.RegionLocation.Position);
                    PowerUseResult activated = caster.ActivatePower(powerRef, ref activation);
                    if (activated != PowerUseResult.Success)
                    {
                        BountyHuntLogger.Info($"[BountyHunt] requiem: ActivatePower={activated} for {LeafHeroName(powerRef)}");
                        caster.Destroy();
                        continue;
                    }

                    caster.ResetLifespan(TimeSpan.FromMilliseconds(PhantomRequiemCasterLifespanMs));
                    struckNames.Add(LeafHeroName(powerRef));
                }
                catch (Exception ex)
                {
                    caster?.Destroy();
                    BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: requiem strike failed: {ex.Message}");
                }
            }

            if (struckNames.Count > 0)
            {
                // No banner here — at a 2s cadence a per-strike banner would
                // spam the screen continuously. The projectiles themselves are
                // the feedback; the log line stays for diagnostics.
                BountyHuntLogger.Info($"[BountyHunt] {GetName()}: requiem strike(s) fired — {string.Join(", ", struckNames)} (rank {_bountyHuntRank})");
            }
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
                    // Drop the way home where the bounty fell (board hunts only).
                    if (evt.Defender != null && evt.Defender.IsInWorld)
                        SpawnBountyReturnPortal(evt.Defender.Region, evt.Defender.RegionLocation.Position);

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
            IsBountyHuntDeathPending = true;

            if (boardSlot >= 0) ResolveBountyBoardLoss(boardSlot);
        }

        /// <summary>Called from Avatar.DoDeathRelease instead of the normal checkpoint/corpse release, once IsBountyHuntDeathPending is confirmed true.</summary>
        // ---------------- Victory return portal ----------------
        //
        // On a Bounty Board kill the arena is sterile and the player is a long
        // way from anything, so a real base-game "return to town" Transition is
        // dropped where the bounty died. Reuses the exact pattern
        // Player.DangerRoomEndlessTerminal.cs's loot-break portal already
        // proved out: spawn the Transition as a real entity and Avatar.cs's
        // generic OnPlayerInteracted calls UseTransition() on it automatically
        // (ReturnToLastTown -> Teleporter.TeleportToLastTown), so there's no
        // manual destination wiring — only a just-in-time LastTownRegionForAccount
        // override so it always lands on Avengers Tower specifically rather
        // than whatever town the player happened to visit last.
        //
        // Board hunts only. A personal-nemesis hunt never spawns one.

        private static readonly ulong[] s_bountyReturnPortalCandidates =
        {
            0x65013AB9D36D1394, // Entity/Transitions/ReturnToLastBaseDR.prototype        (1.48 / 1.52)
            0x68F74D36E02D18A7, // Entity/Transitions/ReturnToLastBaseHolosimVisible.prototype (1.53)
        };

        private static PrototypeId? s_bountyReturnPortalResolved;
        private ulong _bountyReturnPortalId;
        private Region _bountyReturnPortalRegion;
        private Event<PlayerInteractGameEvent>.Action _bountyReturnPortalAction;

        private static PrototypeId GetValidBountyReturnPortalRef()
        {
            if (s_bountyReturnPortalResolved != null) return s_bountyReturnPortalResolved.Value;
            foreach (ulong candidate in s_bountyReturnPortalCandidates)
            {
                if (GameDatabase.GetPrototype<Prototype>((PrototypeId)candidate) != null)
                {
                    s_bountyReturnPortalResolved = (PrototypeId)candidate;
                    return s_bountyReturnPortalResolved.Value;
                }
            }
            s_bountyReturnPortalResolved = PrototypeId.Invalid;
            BountyHuntLogger.Warn("[BountyHunt] no return-portal Transition resolves on this version — victory portal disabled");
            return PrototypeId.Invalid;
        }

        /// <summary>Drop the "return to Avengers Tower" portal where the defeated bounty fell.</summary>
        private void SpawnBountyReturnPortal(Region region, Vector3 position)
        {
            if (region == null) return;

            PrototypeId portalRef = GetValidBountyReturnPortalRef();
            if (portalRef == PrototypeId.Invalid) return;

            try
            {
                using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                settings.EntityRef = portalRef;
                settings.Position = RegionLocation.ProjectToFloor(region, position);
                settings.Orientation = Orientation.Zero;
                settings.RegionId = region.Id;

                WorldEntity portal = Game.EntityManager.CreateEntity(settings) as WorldEntity;
                if (portal == null)
                {
                    BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: return portal spawn failed (CreateEntity returned null)");
                    return;
                }

                portal.Properties[PropertyEnum.Interactable] = true;

                _bountyReturnPortalId = portal.Id;
                _bountyReturnPortalRegion = region;
                _bountyReturnPortalAction ??= OnBountyReturnPortalInteract;
                region.PlayerInteractEvent.AddActionBack(_bountyReturnPortalAction);

                try { SendBannerLines("🌀 A way home has opened where they fell."); } catch { }
                BountyHuntLogger.Info($"[BountyHunt] {GetName()}: return portal spawned (id={portal.Id:X})");
            }
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: return portal spawn threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Point LastTownRegionForAccount at Avengers Tower just before the
        /// engine's own UseTransition() reads it (this handler runs first,
        /// synchronously, inside the same PlayerInteractEvent.Invoke), so the
        /// portal always lands there rather than whatever town the player last
        /// visited. Same just-in-time override the Danger Room portal uses.
        /// </summary>
        private void OnBountyReturnPortalInteract(in PlayerInteractGameEvent evt)
        {
            if (_bountyReturnPortalId == 0 || evt.InteractableObject == null) return;
            if (evt.InteractableObject.Id != _bountyReturnPortalId) return;

            Properties[PropertyEnum.LastTownRegionForAccount] = (PrototypeId)(ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion;
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: return portal used — routing to Avengers Tower");

            // Detach the hook, but DO NOT destroy the portal entity here.
            // Avatar.UseInteractableObject fires PlayerInteractEvent.Invoke
            // (this handler) BEFORE it calls transition.UseTransition(player),
            // and Teleporter.CanTeleport re-checks
            // avatar.InInteractRange(TransitionEntity). A destroyed portal is
            // out of world, so that range check fails and the teleport is
            // abandoned with no log line at all — confirmed live 2026-08-04:
            // "return portal used" was logged and the player never left the
            // arena. The portal dies with the instance a moment later anyway.
            DetachBountyReturnPortalHook();
            _bountyReturnPortalId = 0;
        }

        private void DetachBountyReturnPortalHook()
        {
            if (_bountyReturnPortalRegion != null && _bountyReturnPortalAction != null)
                _bountyReturnPortalRegion.PlayerInteractEvent.RemoveAction(_bountyReturnPortalAction);
            _bountyReturnPortalRegion = null;
        }

        private void DespawnBountyReturnPortal()
        {
            DetachBountyReturnPortalHook();

            if (_bountyReturnPortalId != 0)
            {
                var portal = Game?.EntityManager?.GetEntity<WorldEntity>(_bountyReturnPortalId);
                try { portal?.Destroy(); } catch { }
                _bountyReturnPortalId = 0;
            }
        }

        internal void EndBountyHuntFromDeath(Avatar avatar)
        {
            IsBountyHuntDeathPending = false;
            try { SendBannerLines("🏢 Defeated — returning to Avengers Tower. Pay again to try that bounty once more."); } catch { }
            avatar.TeleportToRegionFromWeb((ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion);
            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: death release redirected to Avengers Tower");
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
            CancelBountyHuntHazardTick();
            CancelPhantomRequiemTick();
            // NOTE: the return portal is deliberately NOT despawned here.
            // ClearBountyHuntState runs at the tail of the same
            // OnBountyHuntEntityDead that spawns the portal, so despawning it
            // here destroyed the portal a few lines after it was created -
            // confirmed live 2026-08-03 ("return portal spawned" logged, but
            // nothing was ever visible in the arena). The portal is cleaned up
            // when it is used, when the next hunt starts, or on logout.
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
            // Logout/disconnect — ClearBountyHuntState deliberately leaves the
            // return portal alone (see its own note), so drop it here instead
            // rather than leaking the PlayerInteractEvent subscription.
            DespawnBountyReturnPortal();
            ClearBountyHuntState();
        }

        private sealed class BountyHuntSpawnTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnBountyHuntSpawnTick();
        }
    }
}
