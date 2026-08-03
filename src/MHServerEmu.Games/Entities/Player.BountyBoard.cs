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

            bool gotBis = false;
            if (slot.Rank >= BountyHuntGuaranteedBisTier)
            {
                Avatar avatar = CurrentAvatar;
                if (avatar != null && PhantomBiSData.TryGetLoadout(avatar.PrototypeDataRef, Game, out var bisSlots) && bisSlots.Count > 0)
                {
                    var pool = new List<PrototypeId>(bisSlots.Values);
                    PrototypeId itemRef = pool[Game.Random.Next(pool.Count)];

                    // GiveItem's default overload rolls the item at level 1
                    // with whatever rarity a level-1 roll produces (usually
                    // Common/Uncommon) -- the curated BiS PrototypeId is the
                    // right ITEM, but without an explicit level/rarity
                    // override it was landing nowhere near BiS quality.
                    // Force the player's real level and top rarity instead.
                    var lootGlobals = GameDatabase.LootGlobalsPrototype;
                    PrototypeId rarityRef = lootGlobals.RarityCosmic != PrototypeId.Invalid
                        ? lootGlobals.RarityCosmic
                        : lootGlobals.RarityUnique;
                    gotBis = Game.LootManager.GiveItem(itemRef, LootContext.Drop, this, avatar.CharacterLevel, rarityRef);
                }
            }

            slot.RewardCollected = true;
            MaybeRerollBountyBoard();

            try { SendBannerLines($"💰 Bounty reward collected — rank {slot.Rank} payout{(gotBis ? " + guaranteed BiS!" : "!")}"); } catch { }
            BountyBoardLogger.Info($"[BountyBoard] {GetName()}: slot {slotIndex} reward collected (rank {slot.Rank}, bis={gotBis})");
            return gotBis ? "reward collected — currency + a guaranteed BiS item" : "reward collected";
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

            var combined = new List<(ulong HeroRef, bool IsBoss, string DisplayName)>(256);
            foreach (var (avatarRef, shortName) in Avatar.GetAllPhantomHeroRefs())
                combined.Add(((ulong)avatarRef, false, FriendlyAvatarDisplayName(avatarRef, shortName)));
            foreach (var (bossRef, displayName) in GetCuratedBossPoolWithNames())
                combined.Add(((ulong)bossRef, true, displayName));
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
            }

            BountyBoardLogger.Info($"[BountyBoard] {GetName()}: rolled {_bountyBoard.Count} new bounties");
        }

        /// <summary>Snapshot the board into MigrationData before a region hop.</summary>
        internal void SnapshotBountyBoardForTransfer(MigrationData mig)
        {
            if (mig == null) return;
            mig.BountyBoard.Clear();
            foreach (var e in _bountyBoard) mig.BountyBoard.Add(e);
        }

        /// <summary>Restore the board from MigrationData after a region hop.</summary>
        internal void RestoreBountyBoardFromMigration(MigrationData mig)
        {
            if (mig == null || mig.BountyBoard.Count == 0) return;
            _bountyBoard.Clear();
            foreach (var e in mig.BountyBoard) _bountyBoard.Add(e);
        }
    }
}
