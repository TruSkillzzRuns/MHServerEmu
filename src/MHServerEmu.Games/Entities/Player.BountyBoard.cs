using System;
using System.Collections.Generic;
using System.Linq;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    // Bounty Board — 6 randomly-rolled bounties shown at once, independent
    // of the player's personal Nemesis roster/kill history (that's still
    // Player.Nemesis.cs + Player.BountyHunt.cs's StartBountyHunt). Rolled
    // from the same pools Rogue Encounter/Endless Wave already draw from
    // (Avatar.GetAllPhantomHeroRefs + GetEndlessBossPool), so nothing new
    // needs to be curated.
    //
    // A slot starts at a low rank (1-3) and stays on the board — surviving
    // a loss to it — until it's resolved one of two ways:
    //   - Defeated: killed once, done, counts toward a board reroll.
    //   - Fled: lost to it 3 times (BountyBoardMaxLosses). Each of the
    //     first 2 losses instead ranks it up by 1 (capped at
    //     EndlessMaxRank=10) so a bounty you keep losing to gets
    //     genuinely tougher, not just relabeled.
    // Once every slot is Resolved (Defeated or Fled), the whole board
    // rerolls fresh. Losing a bounty also always costs the Credits already
    // spent to post it — there's no free revenge attempt, same "pay again
    // to re-engage" rule as a fresh accept.
    public partial class Player
    {
        private static readonly Logger BountyBoardLogger = LogManager.CreateLogger();

        public const int BountyBoardSize = 6;
        public const int BountyBoardMaxLosses = 3;

        // Rank bands a fresh roll draws from — 2 slots per band across the
        // 6-slot board, so there's always a real spread of difficulty to
        // choose from instead of everything clustering at 1-3. Bounties
        // only climb ABOVE their rolled band from there via loss-driven
        // rank-ups (ResolveBountyBoardLoss), never on generation.
        private static readonly (int Min, int Max)[] s_bountyBoardRankBands =
        {
            (1, 3), (1, 3),   // low
            (4, 6), (4, 6),   // medium
            (7, 10), (7, 10), // high
        };

        private readonly List<BountyBoardEntry> _bountyBoard = new();
        public IReadOnlyList<BountyBoardEntry> BountyBoardEntries => _bountyBoard;

        /// <summary>
        /// Credits required to post a Bounty Board bounty at the given
        /// rank. Steeper than the personal-nemesis BountyHuntAcceptCost
        /// (flat 250/tier) since board bounties can climb all the way to
        /// rank 10 purely from losses, without the player ever choosing
        /// that tier themselves — the curve needs to keep pace with
        /// NemesisHealthMultForRank's own acceleration past rank 3-4.
        /// Bumped 10x from the original 50/tier curve — the old top rank
        /// (5,500) was trivial pocket change against typical credit
        /// balances (six/seven figures), so the "wager" barely registered.
        ///   Rank   1     2     3     4      5      6      7      8      9      10
        ///   Cost   1000  3000  6000  10000  15000  21000  28000  36000  45000  55000
        /// </summary>
        public static int BountyBoardAcceptCost(int rank)
        {
            int r = Math.Clamp(rank, 1, EndlessMaxRank);
            return 500 * r * (r + 1);
        }

        /// <summary>
        /// Returns the current board, generating one first if it's empty or
        /// every slot is already Resolved. Suppresses the empty-board
        /// auto-generate until _phantomPersistLoaded is true — otherwise a
        /// poll landing in the window right after a region transfer (new
        /// Player instance, _bountyBoard not yet restored from either
        /// MigrationData or the PhantomPersist sidecar) would see Count==0,
        /// force-roll a throwaway board, and then have it silently
        /// overwritten a moment later once the real one restores. That
        /// showed up live as "the board flips to new bounties, then
        /// reverts" — and if a Post/Collect click landed in that same
        /// window, it acted on a slot that was about to be replaced,
        /// posting a fresh bounty and warping instead of collecting.
        /// </summary>
        public IReadOnlyList<BountyBoardEntry> GetBountyBoard()
        {
            if (_phantomPersistLoaded == false) return _bountyBoard;
            MaybeRerollBountyBoard(force: _bountyBoard.Count == 0);
            return _bountyBoard;
        }

        /// <summary>Force a fresh 6-slot roll regardless of current slot state — debug/admin escape hatch (e.g. clearing a board rolled before a naming/curation fix), not exposed anywhere in the normal player-facing flow.</summary>
        public string RerollBountyBoard()
        {
            GenerateBountyBoard();
            return $"board rerolled — {_bountyBoard.Count} new bounties";
        }

        /// <summary>
        /// Post a bounty against Bounty Board slot index (0-5). Same
        /// warp/ambush pipeline as a personal-nemesis hunt (Player.
        /// BountyHunt.cs's StartBountyHuntInternal) — the only difference
        /// is where the hero/rank/isBoss triplet and the accept-cost
        /// formula come from.
        /// </summary>
        public string StartBountyBoardHunt(int slotIndex)
        {
            GetBountyBoard();
            if (slotIndex < 0 || slotIndex >= _bountyBoard.Count) return "invalid bounty slot";

            BountyBoardEntry slot = _bountyBoard[slotIndex];
            if (slot.Defeated) return "that bounty is already defeated";
            if (slot.Fled) return "that bounty has fled — wait for the board to reroll";

            return StartBountyHuntInternal(slot.HeroRef, slot.Rank, slotIndex);
        }

        /// <summary>
        /// Called from Player.BountyHunt.cs's OnBountyHuntLoss once it's
        /// confirmed the player actually died to their own in-flight
        /// Bounty Hunt spawn AND that hunt was a board hunt (boardSlot
        /// >= 0). Escalates the slot's rank or makes it flee.
        /// </summary>
        private void ResolveBountyBoardLoss(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _bountyBoard.Count) return;
            BountyBoardEntry slot = _bountyBoard[slotIndex];
            if (slot.Defeated || slot.Fled) return;

            slot.LossCount++;
            if (slot.LossCount >= BountyBoardMaxLosses)
            {
                slot.Fled = true;
                try { SendBannerLines("💨 The bounty has fled — you lost the reward and the credits you paid."); } catch { }
                BountyBoardLogger.Info($"[BountyBoard] {GetName()}: slot {slotIndex} ('{((PrototypeId)slot.HeroRef).GetName()}') fled after {slot.LossCount} losses");
            }
            else
            {
                slot.Rank = Math.Min(slot.Rank + 1, EndlessMaxRank);
                // A slot that climbs into rank 9+ via losses locks in its
                // guaranteed BiS now, same as one rolled there directly.
                EnsureBountyBoardGuaranteedBis(slot);
                try { SendBannerLines($"☠️ Defeated by the bounty — now rank {slot.Rank}. Pay again to try once more."); } catch { }
                BountyBoardLogger.Info($"[BountyBoard] {GetName()}: slot {slotIndex} ('{((PrototypeId)slot.HeroRef).GetName()}') ranked up to {slot.Rank} after loss #{slot.LossCount}");
            }

            MaybeRerollBountyBoard();
        }

        /// <summary>Called from Player.BountyHunt.cs's OnBountyHuntEntityDead once it's confirmed the kill resolved a board slot.</summary>
        private void ResolveBountyBoardWin(int slotIndex, ulong heroRef)
        {
            if (slotIndex < 0 || slotIndex >= _bountyBoard.Count) return;
            BountyBoardEntry slot = _bountyBoard[slotIndex];
            if (slot.HeroRef != heroRef) return;
            slot.Defeated = true;
            MaybeRerollBountyBoard();
        }

        /// <summary>
        /// Grants a Defeated slot's currency reward (+ guaranteed BiS at
        /// rank 9-10, straight to inventory) and marks it collected —
        /// separate from the kill itself (ResolveBountyBoardWin) so the
        /// player gets an explicit "Collect Rewards" moment on the board
        /// rather than the payout landing silently mid-fight-cleanup.
        /// Reward amount is recomputed from the slot's Rank, which is
        /// frozen the moment it's Defeated (no more loss-driven rank-ups
        /// apply to a resolved slot).
        /// </summary>
        public string CollectBountyBoardReward(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _bountyBoard.Count) return "invalid bounty slot";
            BountyBoardEntry slot = _bountyBoard[slotIndex];
            if (slot.Defeated == false) return "that bounty hasn't been defeated yet";
            if (slot.RewardCollected) return "reward already collected";

            var currencyGlobals = GameDatabase.CurrencyGlobalsPrototype;
            Properties.AdjustProperty(BountyHuntEternitySplintersPerTier * slot.Rank, new(PropertyEnum.Currency, currencyGlobals.EternitySplinters));
            Properties.AdjustProperty(BountyHuntCubeShardsPerTier * slot.Rank, new(PropertyEnum.Currency, currencyGlobals.CubeShards));
            Properties.AdjustProperty(BountyHuntLegendaryMarksPerTier * slot.Rank, new(PropertyEnum.Currency, currencyGlobals.LegendaryMarks));

            int bisCount = 0;
            int artifactCount = 0;
            int gearCount = 0;
            int ringCount = 0;
            int legendaryCount = 0;
            Avatar rewardAvatar = CurrentAvatar;

            if (rewardAvatar != null)
            {
                int level = rewardAvatar.CharacterLevel;

                // --- The one guaranteed, pre-shown BiS piece. This is the
                // exact item the card advertised, granted first so it can't be
                // crowded out by anything else. ---
                if (slot.GuaranteedBisRef != 0)
                {
                    PrototypeId topRarity = BountyBoardRarityRef("R6Omega", "R6Unique", "R5Cosmic");
                    if (Game.LootManager.GiveItem((PrototypeId)slot.GuaranteedBisRef, LootContext.Drop, this, level, topRarity))
                        bisCount++;
                }

                // --- BiS gear, count scaled by rank ---
                if (slot.Rank >= BountyBoardBisMinRank
                    && PhantomBiSData.TryGetLoadout(rewardAvatar.PrototypeDataRef, Game, out var bisSlots)
                    && bisSlots.Count > 0)
                {
                    var pool = new List<PrototypeId>(bisSlots.Values);
                    int want = BountyBoardBisPieceCountForRank(slot.Rank);
                    for (int i = 0; i < want; i++)
                    {
                        PrototypeId itemRef = pool[Game.Random.Next(pool.Count)];
                        // GiveItem's default overload rolls at level 1 with a
                        // level-1 rarity (usually Common/Uncommon) — the curated
                        // BiS PrototypeId is the right ITEM, but without an
                        // explicit level/rarity override it lands nowhere near
                        // BiS quality. Force real level + a rank-rolled rarity.
                        PrototypeId rarityRef = RollBountyBoardRarity(slot.Rank);
                        if (Game.LootManager.GiveItem(itemRef, LootContext.Drop, this, level, rarityRef))
                            bisCount++;
                    }
                }

                // --- General slot 1-5 gear, rings and legendaries. Dropped
                // at every rank so a kill always produces a real haul. ---
                string heroLeaf = LeafHeroName(rewardAvatar.PrototypeDataRef);

                var armorPool = GetBountyBoardArmorPool(heroLeaf);
                if (armorPool.Count > 0)
                {
                    int wantArmor = BountyBoardArmorCountForRank(slot.Rank);
                    for (int i = 0; i < wantArmor; i++)
                    {
                        PrototypeId gearRef = armorPool[Game.Random.Next(armorPool.Count)];
                        if (Game.LootManager.GiveItem(gearRef, LootContext.Drop, this, level, RollBountyBoardRarity(slot.Rank)))
                            gearCount++;
                    }
                }

                if (s_bountyBoardRings != null && s_bountyBoardRings.Count > 0)
                {
                    int wantRings = BountyBoardRingCountForRank(slot.Rank);
                    for (int i = 0; i < wantRings; i++)
                    {
                        PrototypeId ringRef = s_bountyBoardRings[Game.Random.Next(s_bountyBoardRings.Count)];
                        if (Game.LootManager.GiveItem(ringRef, LootContext.Drop, this, level, RollBountyBoardRarity(slot.Rank)))
                            ringCount++;
                    }
                }

                if (s_bountyBoardLegendaries != null && s_bountyBoardLegendaries.Count > 0)
                {
                    int wantLegendary = BountyBoardLegendaryCountForRank(slot.Rank);
                    for (int i = 0; i < wantLegendary; i++)
                    {
                        PrototypeId legRef = s_bountyBoardLegendaries[Game.Random.Next(s_bountyBoardLegendaries.Count)];
                        if (Game.LootManager.GiveItem(legRef, LootContext.Drop, this, level, RollBountyBoardRarity(slot.Rank)))
                            legendaryCount++;
                    }
                }

                // --- Artifacts. Rank 7+ pulls from the Cosmic (boss-tier)
                // artifact pool; lower ranks from the Tier 1 pool. ---
                int wantArtifacts = BountyBoardArtifactCountForRank(slot.Rank);
                if (wantArtifacts > 0)
                {
                    var artifactPool = GetBountyBoardArtifactPool(slot.Rank >= BountyBoardCosmicMinRank);
                    if (artifactPool.Count > 0)
                    {
                        for (int i = 0; i < wantArtifacts; i++)
                        {
                            PrototypeId artRef = artifactPool[Game.Random.Next(artifactPool.Count)];
                            PrototypeId rarityRef = RollBountyBoardRarity(slot.Rank);
                            if (Game.LootManager.GiveItem(artRef, LootContext.Drop, this, level, rarityRef))
                                artifactCount++;
                        }
                    }
                }
            }

            slot.RewardCollected = true;
            MaybeRerollBountyBoard();

            var parts = new List<string>(5);
            if (bisCount > 0) parts.Add($"{bisCount} BiS");
            if (gearCount > 0) parts.Add($"{gearCount} gear");
            if (ringCount > 0) parts.Add($"{ringCount} ring{(ringCount > 1 ? "s" : "")}");
            if (legendaryCount > 0) parts.Add($"{legendaryCount} legendary");
            if (artifactCount > 0) parts.Add($"{artifactCount} artifact{(artifactCount > 1 ? "s" : "")}");
            string haul = parts.Count > 0 ? " + " + string.Join(" + ", parts) : string.Empty;

            try { SendBannerLines($"💰 Bounty reward collected — rank {slot.Rank} payout{haul}!"); } catch { }
            BountyBoardLogger.Info($"[BountyBoard] {GetName()}: slot {slotIndex} reward collected (rank {slot.Rank}, bis={bisCount}, gear={gearCount}, rings={ringCount}, legendaries={legendaryCount}, artifacts={artifactCount})");
            return parts.Count > 0 ? $"reward collected — currency{haul}" : "reward collected";
        }

        // ---------------- Bounty Board reward quality ----------------
        // Bounty Board mode only — none of this touches personal-nemesis
        // Bounty Hunt, Trial, Endless, or any native loot table.

        /// <summary>
        /// Rank at/above which a slot locks in a specific, visible guaranteed
        /// BiS drop from its own hero's loadout.
        /// </summary>
        public const int BountyBoardGuaranteedBisRank = 9;

        /// <summary>
        /// Lock in this slot's guaranteed BiS piece if it qualifies and hasn't
        /// already got one. Called on generation and after any rank-up, so a
        /// slot that climbs into rank 9 via losses gets one too. Idempotent -
        /// once rolled the ref never changes, which is the whole point: the
        /// card shows the player the exact item they are hunting.
        /// </summary>
        private void EnsureBountyBoardGuaranteedBis(BountyBoardEntry entry)
        {
            if (entry == null) return;
            if (entry.GuaranteedBisRef != 0) return;           // already locked in
            if (entry.Rank < BountyBoardGuaranteedBisRank) return;

            var heroRef = (PrototypeId)entry.HeroRef;
            if (heroRef == PrototypeId.Invalid) return;

            if (entry.IsBoss)
            {
                // Bosses have no BiS loadout to raid, so their guaranteed drop
                // is a boss-tier (Cosmic) artifact instead - same "you can see
                // exactly what you're hunting" contract as the nemesis BiS.
                var artifacts = GetBountyBoardArtifactPool(cosmic: true);
                if (artifacts.Count == 0) return;
                entry.GuaranteedBisRef = (ulong)artifacts[Game.Random.Next(artifacts.Count)];
                BountyBoardLogger.Info($"[BountyBoard] {GetName()}: boss slot locked guaranteed artifact '{((PrototypeId)entry.GuaranteedBisRef).GetName()}' for rank {entry.Rank}");
                return;
            }

            if (heroRef.As<AvatarPrototype>() == null) return;  // team-ups etc. have no loadout

            if (PhantomBiSData.TryGetLoadout(heroRef, Game, out var slots) == false || slots.Count == 0)
                return;

            var pool = new List<PrototypeId>(slots.Values);
            entry.GuaranteedBisRef = (ulong)pool[Game.Random.Next(pool.Count)];
            BountyBoardLogger.Info($"[BountyBoard] {GetName()}: slot locked guaranteed BiS '{((PrototypeId)entry.GuaranteedBisRef).GetName()}' for rank {entry.Rank} {LeafHeroName(heroRef)}");
        }

        /// <summary>Rank at/above which a bounty rolls from the Cosmic (boss-tier) artifact pool and can roll above Cosmic rarity.</summary>
        public const int BountyBoardCosmicMinRank = 7;

        /// <summary>Rank at/above which a bounty grants guaranteed BiS gear at all.</summary>
        public const int BountyBoardBisMinRank = 3;

        private static int BountyBoardBisPieceCountForRank(int rank)
        {
            if (rank >= 10) return 5;
            if (rank >= 8) return 4;
            if (rank >= 6) return 3;
            if (rank >= BountyBoardBisMinRank) return 2;
            return 1;
        }

        private static int BountyBoardArtifactCountForRank(int rank)
        {
            if (rank >= 10) return 4;
            if (rank >= 8) return 3;
            if (rank >= 6) return 2;
            if (rank >= 3) return 1;
            return 0;
        }

        /// <summary>
        /// Rarity for a Bounty Board reward roll. Rank 7+ is guaranteed at
        /// least Cosmic with a real chance at the "cosmically enhanced" tiers
        /// above it (Unique / Omega); below that it tops out at Cosmic.
        /// Rarity refs are resolved by path so a tier missing on a given
        /// client version degrades to the next one down instead of failing.
        /// </summary>
        private PrototypeId RollBountyBoardRarity(int rank)
        {
            var rng = Game.Random;
            if (rank >= BountyBoardCosmicMinRank)
            {
                int roll = rng.Next(100);
                if (roll < 10) return BountyBoardRarityRef("R6Omega", "R6Unique", "R5Cosmic");
                if (roll < 30) return BountyBoardRarityRef("R6Unique", "R5Cosmic");
                return BountyBoardRarityRef("R5Cosmic", "R4Epic");
            }
            if (rank >= 4)
            {
                return rng.Next(100) < 40
                    ? BountyBoardRarityRef("R5Cosmic", "R4Epic")
                    : BountyBoardRarityRef("R4Epic", "R3Rare");
            }
            return BountyBoardRarityRef("R4Epic", "R3Rare");
        }

        private static readonly Dictionary<string, PrototypeId> s_bountyBoardRarityCache = new();
        private static readonly object s_bountyBoardRarityLock = new();

        /// <summary>First of the given rarity leaf names that actually resolves on this server, or Invalid (which GiveItem treats as "roll normally").</summary>
        private static PrototypeId BountyBoardRarityRef(params string[] preferenceOrder)
        {
            lock (s_bountyBoardRarityLock)
            {
                foreach (string name in preferenceOrder)
                {
                    if (s_bountyBoardRarityCache.TryGetValue(name, out PrototypeId cached) == false)
                    {
                        cached = GameDatabase.GetPrototypeRefByName($"Entity/Items/Rarity/{name}.prototype");
                        s_bountyBoardRarityCache[name] = cached;
                    }
                    if (cached != PrototypeId.Invalid) return cached;
                }
            }
            return PrototypeId.Invalid;
        }

        // --- General gear pools: equipment slots 1-5, rings, legendaries ---
        //
        // Confirmed live 2026-08-03 against the loaded prototype tree:
        //   Entity/Items/Armor/Prototypes/<Hero>/o1Stamina .. o5Transformation
        //     -> the five equippable gear slots, 2231 ArmorPrototypes total,
        //        organised per hero (folder name matches the avatar leaf name)
        //   Entity/Items/Rings/            -> 17 ring ItemPrototypes
        //   Entity/Items/Legendaries/      -> 23 LegendaryPrototypes
        //
        // BiS + artifacts alone made a kill feel thin no matter the rarity, so
        // every rank now also drops a spread of ordinary slot gear on top.

        private static Dictionary<string, List<PrototypeId>> s_bountyBoardArmorByHero;
        private static List<PrototypeId> s_bountyBoardArmorAll;
        private static List<PrototypeId> s_bountyBoardRings;
        private static List<PrototypeId> s_bountyBoardLegendaries;
        private static readonly object s_bountyBoardGearLock = new();

        private static void EnsureBountyBoardGearPools()
        {
            if (s_bountyBoardArmorByHero != null) return;
            lock (s_bountyBoardGearLock)
            {
                if (s_bountyBoardArmorByHero != null) return;

                var byHero = new Dictionary<string, List<PrototypeId>>(StringComparer.OrdinalIgnoreCase);
                var armorAll = new List<PrototypeId>(2560);
                var rings = new List<PrototypeId>(32);
                var legendaries = new List<PrototypeId>(32);

                const string armorRoot = "Entity/Items/Armor/Prototypes/";
                const string ringRoot = "Entity/Items/Rings/";
                const string legendaryRoot = "Entity/Items/Legendaries/";

                foreach (PrototypeId itemRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<ItemPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (itemRef == PrototypeId.Invalid) continue;
                    string path = GameDatabase.GetPrototypeName(itemRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("zzz", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (path.IndexOf("Deprecated", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    if (path.StartsWith(armorRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Entity/Items/Armor/Prototypes/<Hero>/<oNSlot>/<Item>.prototype
                        string rest = path[armorRoot.Length..];
                        int slash = rest.IndexOf('/');
                        if (slash <= 0) continue;
                        string hero = rest[..slash];

                        if (byHero.TryGetValue(hero, out var list) == false)
                        {
                            list = new List<PrototypeId>(48);
                            byHero[hero] = list;
                        }
                        list.Add(itemRef);
                        armorAll.Add(itemRef);
                    }
                    else if (path.StartsWith(ringRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        rings.Add(itemRef);
                    }
                    else if (path.StartsWith(legendaryRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        legendaries.Add(itemRef);
                    }
                }

                s_bountyBoardArmorAll = armorAll;
                s_bountyBoardRings = rings;
                s_bountyBoardLegendaries = legendaries;
                s_bountyBoardArmorByHero = byHero;   // set last: it is the initialised flag

                BountyBoardLogger.Info(
                    $"[BountyBoard] gear pools built: {armorAll.Count} armor across {byHero.Count} hero(es), " +
                    $"{rings.Count} ring(s), {legendaries.Count} legendary(ies)");
            }
        }

        /// <summary>Slot 1-5 gear for this hero, falling back to the full armor pool if the hero has no folder of its own.</summary>
        private static List<PrototypeId> GetBountyBoardArmorPool(string heroLeafName)
        {
            EnsureBountyBoardGearPools();
            if (string.IsNullOrEmpty(heroLeafName) == false
                && s_bountyBoardArmorByHero.TryGetValue(heroLeafName, out var list)
                && list.Count > 0)
            {
                return list;
            }
            return s_bountyBoardArmorAll;
        }

        /// <summary>Slot 1-5 gear pieces dropped per kill. Deliberately generous at EVERY rank - quantity was the complaint, not rarity.</summary>
        private static int BountyBoardArmorCountForRank(int rank)
        {
            if (rank >= 10) return 12;
            if (rank >= 8) return 10;
            if (rank >= 6) return 8;
            if (rank >= 4) return 6;
            return 4;
        }

        private static int BountyBoardRingCountForRank(int rank)
        {
            if (rank >= 8) return 3;
            if (rank >= 5) return 2;
            return 1;
        }

        private static int BountyBoardLegendaryCountForRank(int rank)
        {
            if (rank >= 9) return 2;
            if (rank >= 5) return 1;
            return 0;
        }

        private static List<PrototypeId> s_bountyBoardCosmicArtifacts;
        private static List<PrototypeId> s_bountyBoardTier1Artifacts;
        private static readonly object s_bountyBoardArtifactLock = new();

        /// <summary>
        /// Artifact reward pool. Cosmic pool = Entity/Items/Artifacts/Prototypes/
        /// SpecialArtifacts/CosmicArtifacts (the boss-tier artifacts); otherwise
        /// the Tier1Artifacts pool. Built from live prototype data rather than a
        /// hardcoded list so it stays correct across client versions.
        /// </summary>
        private static List<PrototypeId> GetBountyBoardArtifactPool(bool cosmic)
        {
            lock (s_bountyBoardArtifactLock)
            {
                if (cosmic && s_bountyBoardCosmicArtifacts != null) return s_bountyBoardCosmicArtifacts;
                if (cosmic == false && s_bountyBoardTier1Artifacts != null) return s_bountyBoardTier1Artifacts;

                const string cosmicPath = "Artifacts/Prototypes/SpecialArtifacts/CosmicArtifacts/";
                const string tier1Path = "Artifacts/Prototypes/Tier1Artifacts/";

                var cosmicList = new List<PrototypeId>(96);
                var tier1List = new List<PrototypeId>(256);

                foreach (PrototypeId artRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<ArtifactPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (artRef == PrototypeId.Invalid) continue;
                    string path = GameDatabase.GetPrototypeName(artRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf(cosmicPath, StringComparison.OrdinalIgnoreCase) >= 0) cosmicList.Add(artRef);
                    else if (path.IndexOf(tier1Path, StringComparison.OrdinalIgnoreCase) >= 0) tier1List.Add(artRef);
                }

                s_bountyBoardCosmicArtifacts = cosmicList;
                s_bountyBoardTier1Artifacts = tier1List;
                BountyBoardLogger.Info($"[BountyBoard] artifact pools built: {cosmicList.Count} cosmic, {tier1List.Count} tier1");
                return cosmic ? s_bountyBoardCosmicArtifacts : s_bountyBoardTier1Artifacts;
            }
        }

        /// <summary>A slot is done with — eligible to count toward a board reroll — once it's Fled, or Defeated AND its reward has actually been collected. A Defeated-but-uncollected slot must stay put so the reward doesn't vanish into a reroll before the player claims it.</summary>
        private static bool IsSlotFullyResolved(BountyBoardEntry e) => e.Fled || (e.Defeated && e.RewardCollected);

        private void MaybeRerollBountyBoard(bool force = false)
        {
            if (force == false)
            {
                if (_bountyBoard.Count == 0) return;
                foreach (var e in _bountyBoard)
                    if (IsSlotFullyResolved(e) == false) return; // still an active or uncollected slot — no reroll yet
            }
            GenerateBountyBoard();
        }

        private static List<(PrototypeId ProtoRef, string DisplayName)> s_curatedBossPoolWithNames;
        private static readonly object s_curatedBossPoolWithNamesLock = new();

        /// <summary>
        /// Same curated boss selection GetEndlessBossPool() uses (one entry
        /// per recognizable villain, no per-chapter/event reskins), but
        /// paired with the ORIGINAL curated display name ("Doctor Doom")
        /// instead of the raw internal leaf ("DrDoomPhase1") — the Bounty
        /// Board shows this name directly, unlike Endless Wave which never
        /// surfaces boss names to the player.
        /// </summary>
        private static List<(PrototypeId ProtoRef, string DisplayName)> GetCuratedBossPoolWithNames()
        {
            if (s_curatedBossPoolWithNames != null) return s_curatedBossPoolWithNames;
            lock (s_curatedBossPoolWithNamesLock)
            {
                if (s_curatedBossPoolWithNames != null) return s_curatedBossPoolWithNames;
                s_curatedBossPoolWithNames = CuratedBossRoster.SelectCanonicalWithNames(
                    GetRawBossCandidatePool(), LeafHeroName, r => GameDatabase.GetPrototypeName(r));
                return s_curatedBossPoolWithNames;
            }
        }

        /// <summary>Localized display name for a playable avatar ("Thor"), same lookup PhantomsCatalogWebHandler uses for the hero catalog. Falls back to the short internal name if no locale string is set.</summary>
        private static string FriendlyAvatarDisplayName(PrototypeId avatarRef, string fallbackShortName)
        {
            var avatarProto = avatarRef.As<AvatarPrototype>();
            if (avatarProto == null) return fallbackShortName;

            var locale = LocaleManager.Instance.CurrentLocale;
            if (avatarProto.DisplayName != LocaleStringId.Invalid && locale != null)
            {
                string displayName = locale.GetLocaleString(avatarProto.DisplayName);
                if (string.IsNullOrWhiteSpace(displayName) == false) return displayName;
            }

            return fallbackShortName;
        }

        private void GenerateBountyBoard()
        {
            _bountyBoard.Clear();
            var rng = Game?.Random;
            if (rng == null) return;

            // Themed board roll (Bounty Board mode only) — pick ONE theme and
            // fill all six slots from its roster instead of the global
            // hero+boss pool. The chosen theme also drives arena, costume,
            // Requiem powers and arena repopulation for every hunt launched
            // off this board (see Player.BountyThemes.cs). Falls back to the
            // old global pool if the theme's roster can't fill a board on this
            // server version, so a thin/missing theme can never leave the
            // player with an empty board.
            var combined = new List<(ulong HeroRef, bool IsBoss, string DisplayName)>(256);
            _bountyThemeIndex = -1;

            if (BountyThemes.All.Length > 0)
            {
                int themeIndex = rng.Next(BountyThemes.All.Length);
                var roster = GetThemeRoster(themeIndex);
                if (roster.Count >= BountyBoardSize)
                {
                    _bountyThemeIndex = themeIndex;
                    foreach (var (heroRef, isBoss, displayName, _, _) in roster)
                        combined.Add((heroRef, isBoss, displayName));
                }
                else
                {
                    BountyBoardLogger.Warn($"[BountyBoard] {GetName()}: theme '{BountyThemes.All[themeIndex].Name}' only resolved {roster.Count} target(s) on this version — falling back to the untheme(d) pool");
                }
            }

            if (combined.Count == 0)
            {
                foreach (var (avatarRef, shortName) in Avatar.GetAllPhantomHeroRefs())
                    combined.Add(((ulong)avatarRef, false, FriendlyAvatarDisplayName(avatarRef, shortName)));
                foreach (var (bossRef, displayName) in GetCuratedBossPoolWithNames())
                    combined.Add(((ulong)bossRef, true, displayName));
            }
            if (combined.Count == 0) return;

            // Shuffle the fixed low/low/medium/medium/high/high band set so
            // which slot index gets which band varies roll to roll, while
            // still guaranteeing one of each band pair is always present.
            var shuffledBands = ((int Min, int Max)[])s_bountyBoardRankBands.Clone();
            for (int i = shuffledBands.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffledBands[i], shuffledBands[j]) = (shuffledBands[j], shuffledBands[i]);
            }

            var used = new HashSet<ulong>();
            int guard = 0;
            while (_bountyBoard.Count < BountyBoardSize && guard++ < BountyBoardSize * 20)
            {
                var (heroRef, isBoss, displayName) = combined[rng.Next(combined.Count)];
                if (used.Add(heroRef) == false) continue;

                var (bandMin, bandMax) = shuffledBands[_bountyBoard.Count];
                _bountyBoard.Add(new BountyBoardEntry
                {
                    HeroRef = heroRef,
                    IsBoss = isBoss,
                    Rank = rng.Next(bandMin, bandMax + 1),
                    LossCount = 0,
                    Defeated = false,
                    Fled = false,
                    LastKillerName = displayName,
                });
                EnsureBountyBoardGuaranteedBis(_bountyBoard[^1]);
            }

            string themeLabel = _bountyThemeIndex >= 0 ? $" — theme '{BountyThemeName(_bountyThemeIndex)}'" : " — untheme(d)";
            BountyBoardLogger.Info($"[BountyBoard] {GetName()}: rolled {_bountyBoard.Count} new bounties{themeLabel}");
        }

        /// <summary>Snapshot the board into MigrationData before a region hop.</summary>
        internal void SnapshotBountyBoardForTransfer(MigrationData mig)
        {
            if (mig == null) return;
            mig.BountyBoard.Clear();
            foreach (var e in _bountyBoard) mig.BountyBoard.Add(e);
            mig.BountyThemeIndex = _bountyThemeIndex;
        }

        /// <summary>Restore the board from MigrationData after a region hop.</summary>
        internal void RestoreBountyBoardFromMigration(MigrationData mig)
        {
            if (mig == null || mig.BountyBoard.Count == 0) return;
            _bountyBoard.Clear();
            foreach (var e in mig.BountyBoard) _bountyBoard.Add(e);
            _bountyThemeIndex = mig.BountyThemeIndex;
        }
    }
}
