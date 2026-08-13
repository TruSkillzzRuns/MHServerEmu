using System;

namespace MHServerEmu.Games.Features
{
    /// <summary>
    /// Nemesis rank constants and the pure functions over them.
    ///
    /// Extracted from Player.Nemesis.cs (2026-08-12). Nothing here touches
    /// player state - these are constants and pure functions of an int rank -
    /// but living on Player meant five other features (BountyBoard, BountyHunt,
    /// RogueEncounter, TrialOfImpossible, WaveDirector) had to reach into
    /// Nemesis to do rank arithmetic, which registered as a feature dependency
    /// it never really was.
    ///
    /// Call sites are unchanged: consumers add
    /// `using static MHServerEmu.Games.Features.NemesisRank;` and keep calling
    /// these by their original names.
    /// </summary>
    public static class NemesisRank
    {
        // 1..5 rank cap. Each rank adds a name suffix + HP/damage buff.
        public const int NemesisMaxRank = 5;

        public const int EndlessMaxRank = 10;

        /// <summary>Alias of EndlessMaxRank for Bounty Hunt's ephemeral rank — same underlying curve data, just named for its own caller.</summary>
        public const int BountyHuntMaxRank = EndlessMaxRank;

        public static readonly string[] NemesisSuffixes =
        {
            "",                 // rank 0 (never used — entries start at rank 1)
            "the Vengeful",     // rank 1
            "the Undying",      // rank 2
            "the Slayer",       // rank 3
            "the Reaver",       // rank 4
            "the Nemesis",      // rank 5 (persistent roster cap)
            "the Feared",       // rank 6
            "the Dreaded",      // rank 7
            "the Merciless",    // rank 8
            "the Apex Predator",// rank 9
            "the World-Ender",  // rank 10 (Bounty Hunt cap)
        };

        internal static string NemesisSuffixForRank(int rank)
        {
            // Clamped to 10 (not NemesisMaxRank=5) so Bounty Hunt's ephemeral
            // rank (see Player.BountyHunt.cs) gets a real suffix instead of
            // silently reusing rank 5's "the Nemesis" for everything above it.
            // The persistent roster's own Rank field never exceeds 5 anyway,
            // so this is a no-op change for every existing caller.
            int r = Math.Clamp(rank, 1, BountyHuntMaxRank);
            return NemesisSuffixes[r];
        }

        // Rank curve for BOSS nemeses (NemesisEntry.IsBoss) — real bosses
        // don't go through SpawnNemesisPhantomHero/SpawnPhantomHeroCore's
        // rank-buff pipeline at all (they're plain Agents, not phantom
        // Avatars), so NemesisHealthMultForRank/NemesisDmgBoostForRank above
        // don't apply. This is a separate, much lighter curve applied
        // multiplicatively on top of the boss's own already-substantial
        // native stats (see Player.WaveDirector.cs's SpawnCuratedBoss) —
        // real story/raid bosses are already tuned as a real fight, so this
        // only needs to make repeat-kill rank escalation feel meaningful,
        // not carry the whole difficulty curve the way the phantom numbers do.
        // Clamped to EndlessMaxRank (10), not NemesisMaxRank (5) — a real
        // story/raid boss nemesis's own persistent Rank field is still
        // separately capped at NemesisMaxRank, so this only actually
        // extends past 5 for Endless Wave's periodic real-boss spawn
        // (Player.WaveDirector.cs), which passes _endlessPeakRank here.
        public static float BossNemesisExtraHealthMultForRank(int rank) => 1f + Math.Clamp(rank, 0, EndlessMaxRank) * 0.15f;
        public static float BossNemesisExtraDamageMultForRank(int rank) => 1f + Math.Clamp(rank, 0, EndlessMaxRank) * 0.10f;
    }
}
