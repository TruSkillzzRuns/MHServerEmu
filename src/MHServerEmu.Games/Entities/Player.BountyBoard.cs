using System;
using System.Collections.Generic;
using System.Linq;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;

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
        private const int BountyBoardInitialRankMin = 1;
        private const int BountyBoardInitialRankMax = 3;

        private readonly List<BountyBoardEntry> _bountyBoard = new();
        public IReadOnlyList<BountyBoardEntry> BountyBoardEntries => _bountyBoard;

        /// <summary>
        /// Credits required to post a Bounty Board bounty at the given
        /// rank. Steeper than the personal-nemesis BountyHuntAcceptCost
        /// (flat 250/tier) since board bounties can climb all the way to
        /// rank 10 purely from losses, without the player ever choosing
        /// that tier themselves — the curve needs to keep pace with
        /// NemesisHealthMultForRank's own acceleration past rank 3-4.
        ///   Rank   1    2    3     4     5     6     7     8     9     10
        ///   Cost   100  300  600   1000  1500  2100  2800  3600  4500  5500
        /// </summary>
        public static int BountyBoardAcceptCost(int rank)
        {
            int r = Math.Clamp(rank, 1, EndlessMaxRank);
            return 50 * r * (r + 1);
        }

        /// <summary>Returns the current board, generating one first if it's empty or every slot is already Resolved.</summary>
        public IReadOnlyList<BountyBoardEntry> GetBountyBoard()
        {
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

        private void MaybeRerollBountyBoard(bool force = false)
        {
            if (force == false)
            {
                if (_bountyBoard.Count == 0) return;
                foreach (var e in _bountyBoard)
                    if (e.Defeated == false && e.Fled == false) return; // still an active slot — no reroll yet
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

            var used = new HashSet<ulong>();
            int guard = 0;
            while (_bountyBoard.Count < BountyBoardSize && guard++ < BountyBoardSize * 20)
            {
                var (heroRef, isBoss, displayName) = combined[rng.Next(combined.Count)];
                if (used.Add(heroRef) == false) continue;

                _bountyBoard.Add(new BountyBoardEntry
                {
                    HeroRef = heroRef,
                    IsBoss = isBoss,
                    Rank = rng.Next(BountyBoardInitialRankMin, BountyBoardInitialRankMax + 1),
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
