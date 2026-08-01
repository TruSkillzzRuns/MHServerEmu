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
        private const int BountyHuntEternitySplintersPerTier = 5;
        private const int BountyHuntCubeShardsPerTier = 2;
        private const int BountyHuntLegendaryMarksPerTier = 1;

        // Tier at/above which a kill also grants one guaranteed BiS piece
        // for the player's own current hero — same mechanism Trial's finale
        // chest uses (PhantomBiSData + LootManager.SpawnItem), just a single
        // piece instead of Trial's multi-hero spread.
        private const int BountyHuntGuaranteedBisTier = 9;

        private readonly EventGroup _bountyHuntEvents = new();
        private readonly EventPointer<BountyHuntSpawnTickEvent> _bountyHuntSpawnTick = new();

        private bool _bountyHuntWarpPending;
        private ulong _bountyHuntHeroRef;
        private int _bountyHuntRank;
        private Region _bountyHuntRegion;
        private ulong _bountyHuntSpawnedId;
        private Event<EntityDeadGameEvent>.Action _bountyHuntDeadAction;

        /// <summary>
        /// Start a Bounty Hunt against an existing active (non-Defeated)
        /// nemesis roster entry at the given tier (1-10). Validates the same
        /// way SetBountyTarget does, picks a random arena from Trial of the
        /// Impossible's own arena pool, and warps. The actual spawn happens
        /// later, on arrival (see OnAvatarEnteredRegionForBountyHunt) — a
        /// cross-region transfer destroys this Game instance, so nothing
        /// beyond the MigrationData snapshot below survives the warp.
        /// </summary>
        public string StartBountyHunt(ulong heroRef, int rank)
        {
            if (rank < 1 || rank > BountyHuntMaxRank)
                return $"rank must be 1-{BountyHuntMaxRank}";

            NemesisEntry entry = null;
            foreach (var n in _nemeses) { if (n.HeroRef == heroRef) { entry = n; break; } }
            if (entry == null) return "that nemesis isn't on your roster";
            if (entry.Defeated) return "that nemesis is already defeated — pick an active one";

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "no avatar in world";

            RegionPrototypeId[] pool = GetValidTrialArenaPool();
            if (pool.Length == 0) return "no valid arena regions available";
            RegionPrototypeId chosen = pool[Game.Random.Next(pool.Length)];

            DetachBountyHuntDeadAction();
            _bountyHuntHeroRef = heroRef;
            _bountyHuntRank = rank;
            _bountyHuntRegion = null;
            _bountyHuntSpawnedId = 0;
            _bountyHuntWarpPending = true;

            avatar.TeleportToRegionFromWeb((ulong)chosen);

            BountyHuntLogger.Info($"[BountyHunt] {GetName()}: hunt started on '{((PrototypeId)heroRef).GetName()}' tier {rank}, warping to {chosen}");
            return $"hunt started on {((PrototypeId)heroRef).GetName()} (tier {rank}) — warping...";
        }

        /// <summary>Snapshot the pending Bounty Hunt warp onto MigrationData so it survives the cross-region Game-instance destroy/recreate. Called from PlayerConnection.BeginRegionTransfer.</summary>
        internal void SnapshotBountyHuntForTransfer()
        {
            if (_bountyHuntWarpPending == false) return;
            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;
            mig.PendingBountyHuntWarp = true;
            mig.BountyHuntHeroRef = _bountyHuntHeroRef;
            mig.BountyHuntRank = _bountyHuntRank;
            _bountyHuntWarpPending = false; // this Game instance is going away
        }

        /// <summary>Called from Avatar.OnEnteredWorld on every region entry for a real (non-phantom) player avatar — mirrors Player.TrialOfImpossible.cs's OnAvatarEnteredRegionForTrial call site.</summary>
        internal void OnAvatarEnteredRegionForBountyHunt(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;

            var mig = PlayerConnection?.MigrationData;
            if (mig == null || mig.PendingBountyHuntWarp == false) return;

            mig.PendingBountyHuntWarp = false;
            _bountyHuntHeroRef = mig.BountyHuntHeroRef;
            _bountyHuntRank = mig.BountyHuntRank;
            _bountyHuntRegion = region;

            // Baseline reward reuses the existing Bounty Board claim flow —
            // fires automatically on kill via the corpse-cleanup tick
            // (avatar-type target) or TrackBossNemesisForRetire (boss-type
            // target), whichever applies. Set here (after arrival), not
            // before departure — _bountyTargetHeroRef is a plain Player
            // field with no MigrationData snapshot of its own (deliberately
            // short-lived per its own doc comment), so setting it before the
            // warp would just be lost when this Game instance is destroyed.
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
            if (scheduler == null) return;
            if (_bountyHuntSpawnTick.IsValid) scheduler.CancelEvent(_bountyHuntSpawnTick);
            int delayMs = Game.Random.Next(BountyHuntSpawnMinDelayMs, BountyHuntSpawnMaxDelayMs);
            scheduler.ScheduleEvent(_bountyHuntSpawnTick, TimeSpan.FromMilliseconds(delayMs), _bountyHuntEvents);
            _bountyHuntSpawnTick.Get().Initialize(this);
        }

        private void OnBountyHuntSpawnTick()
        {
            if (_bountyHuntHeroRef == 0 || _bountyHuntRegion == null) return;

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false || avatar.Region != _bountyHuntRegion)
            {
                // Left before the hunt triggered — bounty flag stays set
                // (no penalty), but this specific hunt attempt is over.
                ClearBountyHuntState();
                return;
            }

            NemesisEntry entry = null;
            foreach (var n in _nemeses) { if (n.HeroRef == _bountyHuntHeroRef) { entry = n; break; } }
            if (entry == null || entry.Defeated)
            {
                ClearBountyHuntState();
                return;
            }

            string killerBase = string.IsNullOrEmpty(entry.LastKillerName) ? "Phantom" : entry.LastKillerName;
            string suffix = NemesisSuffixForRank(_bountyHuntRank);
            string stars = new string('★', Math.Clamp(_bountyHuntRank, 1, BountyHuntMaxRank));
            string displayName = string.IsNullOrEmpty(suffix) ? $"{stars} {killerBase}" : $"{stars} {killerBase} {suffix}";

            ulong id;
            string err;
            if (entry.IsBoss)
            {
                id = SpawnCuratedBoss(avatar, (PrototypeId)entry.HeroRef, out err,
                    BossNemesisExtraHealthMultForRank(_bountyHuntRank), BossNemesisExtraDamageMultForRank(_bountyHuntRank));
                if (id != 0) TrackBossNemesisForRetire(id, entry.HeroRef, _bountyHuntRegion);
            }
            else
            {
                int grudge = GrudgeScore(entry);
                id = avatar.SpawnNemesisPhantomHero((PrototypeId)entry.HeroRef, 0, displayName, _bountyHuntRank, out err, entry.EscapeCount, grudge);
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

        /// <summary>Fires when the tracked Bounty Hunt spawn dies — grants the tier-scaled currency and (tier 9-10) guaranteed BiS bonus on top of the baseline Bounty Board reward, which is claimed separately via the existing RetireNemesis/TryClaimBountyReward chain.</summary>
        private void OnBountyHuntEntityDead(in EntityDeadGameEvent evt)
        {
            if (evt.Defender == null || evt.Defender.Id != _bountyHuntSpawnedId) return;

            int tier = _bountyHuntRank;
            ulong heroRef = _bountyHuntHeroRef;
            DetachBountyHuntDeadAction();

            try
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
            catch (Exception ex)
            {
                BountyHuntLogger.Warn($"[BountyHunt] {GetName()}: reward grant failed: {ex.Message}");
            }
            finally
            {
                ClearBountyHuntState();
            }
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
