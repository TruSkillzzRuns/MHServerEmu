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

        // Per-escape HP bonus (see Avatar.Nemesis.cs) — +2% HealthMaxMult on
        // top of the rank curve for every time this nemesis has escaped
        // after killing the player, applied multiplicatively at spawn.
        internal const float NemesisEscapeHealthBonusPerEscape = 0.02f;

        // Soft cap on roster size. Nemeses never expire on their own, so a
        // long-running account would otherwise accumulate an ever-growing
        // list. When a brand-new nemesis is registered past this count, the
        // oldest entry (preferring a Defeated one, since those are just
        // history) is evicted automatically — see EvictOldestNemesisIfOverCap.
        public const int NemesisMaxRosterSize = 15;

        // Weighted chance a Rogue Encounter draws from the nemesis roster
        // instead of picking a random hero. Rolled independently for EACH
        // spawn slot, so a 3-hostile encounter with an active nemesis on
        // the roster is very likely to include them — and if you have
        // multiple active nemeses, multiple slots can be nemeses in the
        // same encounter. Only checked when the roster has at least one
        // active (non-Defeated) entry.
        internal const double NemesisRogueChance = 0.80;

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
        // Debounce: OnKilled can fire multiple times during the death
        // sequence — real-player auto-revive triggers OnKilled once when
        // HP hits 0, then again if lingering damage puts them back at 0
        // during the resurrect window. Without this, one "real" death was
        // registering as rank 4-5. Only accept one kill per (heroRef,
        // 5-second window).
        private readonly Dictionary<ulong, long> _lastKillMsByHeroRef = new();
        private const long NemesisKillDebounceMs = 5_000;

        public IReadOnlyList<NemesisEntry> Nemeses => _nemeses;

        /// <summary>
        /// Register a nemesis kill. Called from Avatar.OnKilled when the
        /// human's avatar is downed by an enemy phantom hero — the killer
        /// may be an Avatar phantom OR a team-up phantom (both are Agent).
        /// Adds the hero/team-up to the roster or bumps their rank if they
        /// were already on it. Returns the (new or updated) entry so the
        /// caller can act on the post-kill rank — e.g. trigger an "escape"
        /// for rank 4/5 — or null if the kill was debounced/invalid.
        /// </summary>
        public NemesisEntry RegisterNemesisKill(Agent killer)
        {
            if (killer == null) return null;

            PrototypeId heroRef = killer.PrototypeDataRef;
            if (heroRef == PrototypeId.Invalid) return null;

            string killerName = killer.GetOwnerOfType<Player>()?.GetName() ?? string.Empty;
            long nowMs = Game?.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond ?? 0;

            // Debounce: swallow duplicate registers from the same hero
            // within the death-sequence window.
            if (_lastKillMsByHeroRef.TryGetValue((ulong)heroRef, out long lastMs)
                && nowMs - lastMs < NemesisKillDebounceMs)
            {
                return null;
            }
            _lastKillMsByHeroRef[(ulong)heroRef] = nowMs;

            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == (ulong)heroRef);
            if (entry == null)
            {
                EvictOldestNemesisIfOverCap();
                entry = new NemesisEntry
                {
                    HeroRef        = (ulong)heroRef,
                    Rank           = 1,
                    Kills          = 1,
                    Defeated       = false,
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
                bool wasDefeated = entry.Defeated;
                entry.Defeated = false;
                entry.LastKillerName = killerName;
                entry.LastKillMs = nowMs;
                if (wasDefeated)
                    NemesisLogger.Info($"[Nemesis] {GetName()}: DEFEATED nemesis '{heroRef.GetName()}' reactivated at rank {entry.Rank} (kills {entry.Kills})");
                else
                    NemesisLogger.Info($"[Nemesis] {GetName()}: '{heroRef.GetName()}' rank → {entry.Rank} (kills {entry.Kills})");
            }

            return entry;
        }

        /// <summary>
        /// Called when the player successfully kills a nemesis phantom.
        /// Marks the entry Defeated (kept in the history) instead of
        /// removing it — the roster is a permanent record.
        /// </summary>
        public bool RetireNemesis(ulong heroRef)
        {
            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == heroRef);
            if (entry == null) return false;
            if (entry.Defeated) return false; // idempotent — corpse tick fires more than once per phantom
            entry.Defeated = true;
            entry.RevengeKills++;
            NemesisLogger.Info($"[Nemesis] {GetName()}: DEFEATED '{((PrototypeId)heroRef).GetName()}' — revenge #{entry.RevengeKills}, rank retained at {entry.Rank}");
            return true;
        }

        /// <summary>
        /// Banish from the app — permanently removes the entry from the
        /// history. Distinct from RetireNemesis (which just marks the entry
        /// Defeated for the roster's historical record).
        /// </summary>
        public bool BanishNemesis(ulong heroRef)
        {
            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == heroRef);
            if (entry == null) return false;
            _nemeses.Remove(entry);
            NemesisLogger.Info($"[Nemesis] {GetName()}: banished '{((PrototypeId)heroRef).GetName()}' from the history");
            return true;
        }

        /// <summary>
        /// Evicts the oldest nemesis entry if the roster is at/over
        /// <see cref="NemesisMaxRosterSize"/>. Prefers evicting the oldest
        /// Defeated entry (pure history, no active threat lost); only
        /// touches an active entry if every entry is currently active.
        /// Called automatically before a new nemesis is added.
        /// </summary>
        private void EvictOldestNemesisIfOverCap()
        {
            if (_nemeses.Count < NemesisMaxRosterSize) return;

            NemesisEntry oldest = null;
            foreach (var n in _nemeses)
            {
                if (n.Defeated == false) continue;
                if (oldest == null || n.LastKillMs < oldest.LastKillMs) oldest = n;
            }
            if (oldest == null)
            {
                foreach (var n in _nemeses)
                    if (oldest == null || n.LastKillMs < oldest.LastKillMs) oldest = n;
            }
            if (oldest == null) return;

            _nemeses.Remove(oldest);
            NemesisLogger.Info($"[Nemesis] {GetName()}: roster at cap ({NemesisMaxRosterSize}) — auto-banished oldest '{((PrototypeId)oldest.HeroRef).GetName()}'");
        }

        /// <summary>
        /// Chat banner for a rank 4/5 nemesis escape — see
        /// Avatar.Nemesis.cs. Reuses the same banner channel Rogue Encounter
        /// uses for its ambush notifications.
        /// </summary>
        internal void AnnounceNemesisEscape(string nemesisName)
        {
            try { SendBannerLines($"💨 {nemesisName} has ESCAPED — they'll be back stronger."); }
            catch (Exception ex) { NemesisLogger.Warn($"[Nemesis] escape banner failed: {ex.Message}"); }
        }

        /// <summary>
        /// Chat banner for the rank-0 reset after too many rank 4/5 escapes
        /// — see Avatar.Nemesis.cs.
        /// </summary>
        internal void AnnounceNemesisRankReset(string nemesisName)
        {
            try { SendBannerLines($"⚠ {nemesisName} has evaded you too many times and lost their edge — rank reset."); }
            catch (Exception ex) { NemesisLogger.Warn($"[Nemesis] rank-reset banner failed: {ex.Message}"); }
        }

        /// <summary>
        /// Manually banish the single oldest entry on the roster (by
        /// LastKillMs), regardless of cap. Returns the banished hero's
        /// PrototypeId, or PrototypeId.Invalid if the roster is empty.
        /// </summary>
        public PrototypeId BanishOldestNemesis()
        {
            if (_nemeses.Count == 0) return PrototypeId.Invalid;

            NemesisEntry oldest = _nemeses[0];
            foreach (var n in _nemeses)
                if (n.LastKillMs < oldest.LastKillMs) oldest = n;

            ulong heroRef = oldest.HeroRef;
            _nemeses.Remove(oldest);
            NemesisLogger.Info($"[Nemesis] {GetName()}: banished oldest '{((PrototypeId)heroRef).GetName()}'");
            return (PrototypeId)heroRef;
        }

        /// <summary>
        /// Roll the roster for the next Rogue Encounter spawn. Returns null
        /// when the roster is empty or the roll fails; otherwise the entry
        /// to use for one of the spawns (higher-rank nemeses weighted more
        /// heavily since they've proven they can kill this player).
        /// </summary>
        public NemesisEntry TryPickNemesisForRogue(MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (_nemeses.Count == 0 || rng == null) return null;

            // Only ACTIVE nemeses are eligible for a Rogue Encounter respawn.
            // Defeated ones stay in the history for display but sit out —
            // if they're going to reactivate it has to be from killing the
            // player again, not from a random roll.
            long totalWeight = 0;
            for (int i = 0; i < _nemeses.Count; i++)
                if (_nemeses[i].Defeated == false) totalWeight += _nemeses[i].Rank;
            if (totalWeight <= 0) return null;

            if (rng.NextDouble() >= NemesisRogueChance) return null;

            long roll = (long)(rng.NextDouble() * totalWeight);
            long acc = 0;
            for (int i = 0; i < _nemeses.Count; i++)
            {
                if (_nemeses[i].Defeated) continue;
                acc += _nemeses[i].Rank;
                if (roll < acc) return _nemeses[i];
            }
            // Fallback: return the last active one (guarded above so this exists).
            for (int i = _nemeses.Count - 1; i >= 0; i--)
                if (_nemeses[i].Defeated == false) return _nemeses[i];
            return null;
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

        // TOTAL HealthMaxMult by rank (replaces the base enemy multiplier
        // for nemesis spawns, doesn't stack on top of it). Rank 0 is used
        // only as a lookup fallback — the callsite skips this method for
        // fresh non-nemesis rogues and uses EnemyPhantomHealthMult (3.0×)
        // directly. Calibrated for the user's play pattern of 2-3 friendly
        // phantoms — soft ramp so early ranks are barely noticeable and
        // rank 5 caps at "tough mini-boss" not "solo raid boss".
        internal static float NemesisHealthMultForRank(int rank)
        {
            // Rescaled after the PvP-damage-scaling bug fix — enemy phantoms
            // now take full damage, so the rank curve is steeper. Rank 5
            // nemesis lands at 20× HealthMax — a proper mini-boss encounter
            // that can't be one-shot by a well-geared 60.
            int r = Math.Clamp(rank, 0, NemesisMaxRank);
            return r switch
            {
                0 => 8.0f,   // baseline enemy — matches EnemyPhantomHealthMult
                1 => 8.5f,
                2 => 10.0f,
                3 => 12.0f,
                4 => 22.0f,  // buffed — a genuine wall
                5 => 32.0f,  // buffed — proper raid-boss HP pool
                _ => 8.0f,
            };
        }

        // Fractional damage boost on top of the enemy phantom base curve.
        // Rank 1 = +5%, up to rank 5 = +60%. Numbers calibrated so a rank
        // 5 nemesis lands roughly 1.6× the damage of a fresh rogue — a
        // meaningful threat without one-shotting a well-geared 60.
        internal static float NemesisDmgBoostForRank(int rank)
        {
            int r = Math.Clamp(rank, 0, NemesisMaxRank);
            return r switch
            {
                0 => 0.00f,
                1 => 0.05f,
                2 => 0.15f,
                3 => 0.25f,
                4 => 0.60f,  // buffed — hits noticeably harder
                5 => 0.80f,  // buffed — a hard hitter without one-shotting
                _ => 0.00f,
            };
        }

        internal static string NemesisSuffixForRank(int rank)
        {
            int r = Math.Clamp(rank, 1, NemesisMaxRank);
            return NemesisSuffixes[r];
        }
    }
}
