using System;
using System.Collections.Generic;
using System.Linq;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities
{
    // Nemesis System — enemy phantom heroes that kill the player earn a
    // spot on this list, and the next Rogue Encounter has a weighted chance
    // to send one of them back (buffed by Rank, name-suffixed). Killing the
    // nemesis clears the entry; the app can also banish from the list.
    //
    // Persistence is via MigrationData.Nemeses so revenge survives region
    // hops the same way RogueEncounterEnabled does.
    public partial class Player
    {
        private static readonly Logger NemesisLogger = LogManager.CreateLogger();

        // 1..5 rank cap. Each rank adds a name suffix + HP/damage buff.
        public const int NemesisMaxRank = 5;

        // Weighted chance a Rogue Encounter draws from the nemesis roster
        // instead of picking a random hero. Only checked when the roster
        // has at least one entry.
        internal const double NemesisRogueChance = 0.40;

        // Rank → suffix. Kept short so nameplates read like "PhantomWolverine
        // the Slayer" not a paragraph.
        public static readonly string[] NemesisSuffixes =
        {
            "",                 // rank 0 (never used — entries start at rank 1)
            "the Vengeful",     // rank 1
            "the Undying",      // rank 2
            "the Slayer",       // rank 3
            "the Reaver",       // rank 4
            "the Nemesis",      // rank 5 (cap)
        };

        private readonly List<NemesisEntry> _nemeses = new();

        public IReadOnlyList<NemesisEntry> Nemeses => _nemeses;

        /// <summary>
        /// Register a nemesis kill. Called from Avatar.OnKilled when the
        /// human's avatar is downed by an enemy phantom hero. Adds the hero
        /// to the roster or bumps their rank if they were already on it.
        /// </summary>
        public void RegisterNemesisKill(Avatar killerAvatar)
        {
            if (killerAvatar == null) return;

            PrototypeId heroRef = killerAvatar.PrototypeDataRef;
            if (heroRef == PrototypeId.Invalid) return;

            string killerName = killerAvatar.GetOwnerOfType<Player>()?.GetName() ?? string.Empty;
            long nowMs = Game?.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond ?? 0;

            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == (ulong)heroRef);
            if (entry == null)
            {
                entry = new NemesisEntry
                {
                    HeroRef        = (ulong)heroRef,
                    Rank           = 1,
                    Kills          = 1,
                    LastKillerName = killerName,
                    LastKillMs     = nowMs,
                };
                _nemeses.Add(entry);
                NemesisLogger.Info($"[Nemesis] {GetName()}: new nemesis '{heroRef.GetName()}' registered ({killerName})");
            }
            else
            {
                entry.Kills++;
                if (entry.Rank < NemesisMaxRank) entry.Rank++;
                entry.LastKillerName = killerName;
                entry.LastKillMs = nowMs;
                NemesisLogger.Info($"[Nemesis] {GetName()}: '{heroRef.GetName()}' rank → {entry.Rank} (kills {entry.Kills})");
            }
        }

        /// <summary>
        /// Called when the player successfully kills a nemesis phantom.
        /// Removes the entry if found — the loop closes.
        /// </summary>
        public bool RetireNemesis(ulong heroRef)
        {
            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == heroRef);
            if (entry == null) return false;
            _nemeses.Remove(entry);
            NemesisLogger.Info($"[Nemesis] {GetName()}: retired '{((PrototypeId)heroRef).GetName()}' after revenge kill");
            return true;
        }

        /// <summary>Banish from the app — same effect as a revenge kill.</summary>
        public bool BanishNemesis(ulong heroRef) => RetireNemesis(heroRef);

        /// <summary>
        /// Roll the roster for the next Rogue Encounter spawn. Returns null
        /// when the roster is empty or the roll fails; otherwise the entry
        /// to use for one of the spawns (higher-rank nemeses weighted more
        /// heavily since they've proven they can kill this player).
        /// </summary>
        public NemesisEntry TryPickNemesisForRogue(MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (_nemeses.Count == 0 || rng == null) return null;
            if (rng.NextDouble() >= NemesisRogueChance) return null;

            long totalWeight = 0;
            for (int i = 0; i < _nemeses.Count; i++) totalWeight += _nemeses[i].Rank;
            if (totalWeight <= 0) return _nemeses[0];

            long roll = (long)(rng.NextDouble() * totalWeight);
            long acc = 0;
            for (int i = 0; i < _nemeses.Count; i++)
            {
                acc += _nemeses[i].Rank;
                if (roll < acc) return _nemeses[i];
            }
            return _nemeses[^1];
        }

        /// <summary>Snapshot the nemesis list into MigrationData before a region hop.</summary>
        internal void SnapshotNemesesForTransfer(MigrationData mig)
        {
            if (mig == null) return;
            mig.Nemeses.Clear();
            foreach (var n in _nemeses) mig.Nemeses.Add(n);
        }

        /// <summary>Restore the nemesis list from MigrationData after a region hop.</summary>
        internal void RestoreNemesesFromMigration(MigrationData mig)
        {
            if (mig == null || mig.Nemeses.Count == 0) return;
            _nemeses.Clear();
            foreach (var n in mig.Nemeses) _nemeses.Add(n);
        }

        // ---- Helpers exposed for the rogue-encounter integration ----

        internal static float NemesisHealthMultForRank(int rank)
        {
            // Rank 1 = 1.5x baseline (already 2x for enemy phantoms), stacking
            // to 3.0x at rank 5. Enough to feel dangerous, not unkillable.
            int r = Math.Clamp(rank, 1, NemesisMaxRank);
            return 1.0f + 0.5f * r;
        }

        internal static string NemesisSuffixForRank(int rank)
        {
            int r = Math.Clamp(rank, 1, NemesisMaxRank);
            return NemesisSuffixes[r];
        }
    }
}
