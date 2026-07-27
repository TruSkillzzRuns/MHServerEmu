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

        // 2026-07-27 — Endless Wave-only extension of the same rank curve up
        // to 10, for players who go deep into a long run. Deliberately NOT
        // used by NemesisMaxRank/the persistent Rogue Encounter system — a
        // revenge nemesis's own Rank field is separately capped at
        // NemesisMaxRank (see RestoreNemesesFromMigration-adjacent rank++
        // logic), so it can never actually reach ranks 6-10 even though
        // NemesisHealthMultForRank/NemesisDmgBoostForRank below now have
        // data for them — those cases are only ever reachable via Endless
        // mode's own synthetic scaleIndex-driven rank (Player.WaveDirector.cs).
        public const int EndlessMaxRank = 10;

        // Per-escape HP bonus (see Avatar.Nemesis.cs) — +2% HealthMaxMult on
        // top of the rank curve for every time this nemesis has escaped
        // after killing the player, applied multiplicatively at spawn.
        internal const float NemesisEscapeHealthBonusPerEscape = 0.02f;

        // Grudge score (Kills - RevengeKills, floored at 0) — how far ahead
        // a nemesis is in the rivalry right now. Stacks multiplicatively on
        // top of the rank/escape curve, same pattern as the escape bonus
        // above, so a nemesis who's beaten you more than you've beaten them
        // comes back hitting harder without needing a whole new stat table.
        internal const float NemesisGrudgeHealthBonusPerPoint = 0.03f;
        internal const float NemesisGrudgeDmgBoostPerPoint = 0.02f;

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

        // Bounty Board: which nemesis (if any) is currently flagged as the
        // active bounty target. Not persisted across region hops on purpose —
        // it's a short-lived "hunt this one right now" flag the app sets,
        // not part of the permanent roster record.
        private ulong _bountyTargetHeroRef;
        private ulong _bountyRewardLootTableRef;
        private const int BountyRewardRolls = 5;

        // Nemesis Family Tree — a chance that defeating an avatar nemesis
        // queues up a team-up "avenger" for the NEXT Rogue Encounter's
        // team-up cameo slot (see Player.RogueEncounter.cs), flavored as
        // avenging the one you just took down. No real lore/family data
        // exists anywhere in this codebase linking specific avatars to
        // specific team-ups, so the "avenger" is a random team-up from the
        // normal pool — the narrative link is purely in the display text,
        // not a real data relationship.
        internal const double NemesisAvengerChance = 0.35;
        private ulong _pendingAvengerTeamUpRef;
        private string _pendingAvengerFallenName;
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

            // Nemesis Family Tree roll.
            try
            {
                var rng = Game?.Random;
                if (rng != null && rng.NextDouble() < NemesisAvengerChance)
                {
                    PrototypeId avengerRef = Avatar.GetAllPhantomTeamUpRefs() is { Count: > 0 } pool
                        ? pool[rng.Next(0, pool.Count)].TeamUpRef
                        : PrototypeId.Invalid;
                    if (avengerRef != PrototypeId.Invalid)
                    {
                        _pendingAvengerTeamUpRef = (ulong)avengerRef;
                        _pendingAvengerFallenName = ((PrototypeId)heroRef).GetName();
                        NemesisLogger.Info($"[Nemesis] {GetName()}: an avenger for '{_pendingAvengerFallenName}' is queued for the next Rogue Encounter");
                    }
                }
            }
            catch (Exception ex) { NemesisLogger.Warn($"[Nemesis] avenger roll failed: {ex.Message}"); }

            return true;
        }

        /// <summary>Consume the pending Family Tree avenger (if any) for the next Rogue Encounter team-up cameo slot.</summary>
        internal bool TryConsumePendingAvenger(out ulong teamUpRef, out string fallenName)
        {
            teamUpRef = _pendingAvengerTeamUpRef;
            fallenName = _pendingAvengerFallenName;
            bool had = teamUpRef != 0;
            _pendingAvengerTeamUpRef = 0;
            _pendingAvengerFallenName = null;
            return had;
        }

        /// <summary>
        /// Net grudge score for a nemesis entry — how far ahead they are in
        /// the rivalry (how many more times they've killed you than you've
        /// killed them), floored at 0. Drives NemesisGrudgeHealthBonusPerPoint
        /// /NemesisGrudgeDmgBoostPerPoint, Public Enemy #1 selection, and the
        /// Bounty Board's default sort.
        /// </summary>
        public static int GrudgeScore(NemesisEntry entry)
            => entry == null ? 0 : Math.Max(0, entry.Kills - entry.RevengeKills);

        /// <summary>
        /// "Public Enemy #1" — the active (non-Defeated) nemesis with the
        /// highest grudge score right now, or null if there isn't one (empty
        /// roster, or every active entry is tied at 0). Ties broken by most
        /// recent kill so the most currently-threatening one wins.
        /// </summary>
        public NemesisEntry GetPublicEnemyNumberOne()
        {
            NemesisEntry best = null;
            int bestScore = 0;
            foreach (var n in _nemeses)
            {
                if (n.Defeated) continue;
                int score = GrudgeScore(n);
                if (score <= 0) continue;
                if (best == null || score > bestScore || (score == bestScore && n.LastKillMs > best.LastKillMs))
                {
                    best = n;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>Currently active Bounty Board target, or null if none is set / the target has left the roster.</summary>
        public NemesisEntry GetBountyTarget()
            => _bountyTargetHeroRef == 0 ? null : _nemeses.FirstOrDefault(n => n.HeroRef == _bountyTargetHeroRef);

        /// <summary>Set the active Bounty Board target + the loot table its reward pays out from. Pass heroRef=0 to clear.</summary>
        public string SetBountyTarget(ulong heroRef, ulong rewardLootTableRef = 0)
        {
            if (heroRef == 0)
            {
                _bountyTargetHeroRef = 0;
                _bountyRewardLootTableRef = 0;
                return "bounty cleared";
            }
            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == heroRef);
            if (entry == null) return "that nemesis isn't on your roster";
            if (entry.Defeated) return "that nemesis is already defeated — pick an active one";
            _bountyTargetHeroRef = heroRef;
            _bountyRewardLootTableRef = rewardLootTableRef;
            NemesisLogger.Info($"[Nemesis] {GetName()}: bounty set on '{((PrototypeId)heroRef).GetName()}'");
            return $"bounty set on {((PrototypeId)heroRef).GetName()}";
        }

        /// <summary>
        /// Called right after RetireNemesis when the nemesis just defeated
        /// was the active bounty target — rolls the configured bounty
        /// reward loot table several times and clears the bounty. No-op
        /// (returns false) if there was no bounty on this hero, or no
        /// reward loot table was configured for it.
        /// </summary>
        internal bool TryClaimBountyReward(ulong heroRef)
        {
            bool wasBountyTarget = _bountyTargetHeroRef != 0 && _bountyTargetHeroRef == heroRef;
            if (wasBountyTarget == false) return false;

            ulong rewardRef = _bountyRewardLootTableRef;
            _bountyTargetHeroRef = 0;
            _bountyRewardLootTableRef = 0;
            if (rewardRef == 0) return true;

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return true;

            try
            {
                using var inputSettings = MHServerEmu.Core.Memory.ObjectPoolManager.Instance.Get<Loot.LootInputSettings>();
                inputSettings.Initialize(Loot.LootContext.Drop, this, avatar);
                for (int i = 0; i < BountyRewardRolls; i++)
                    Game.LootManager.SpawnLootFromTable((PrototypeId)rewardRef, inputSettings, 1);
                NemesisLogger.Info($"[Nemesis] {GetName()}: bounty claimed on '{((PrototypeId)heroRef).GetName()}' — {BountyRewardRolls} reward roll(s)");
            }
            catch (Exception ex)
            {
                NemesisLogger.Warn($"[Nemesis] bounty reward drop failed: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// Spare a downed nemesis instead of finishing them off normally.
        /// Unlike RetireNemesis (which keeps rank at its current value for
        /// the historical record), sparing knocks rank down by 1 — mercy
        /// costs them some of their edge instead of leaving it untouched —
        /// and tracks a separate MercyCount so a Bounty Board/UI can show
        /// "spared 3 times" distinctly from "defeated 3 times." Idempotent
        /// same as RetireNemesis: a no-op if already Defeated.
        /// </summary>
        public bool SpareNemesis(ulong heroRef)
        {
            NemesisEntry entry = _nemeses.FirstOrDefault(n => n.HeroRef == heroRef);
            if (entry == null) return false;
            if (entry.Defeated) return false;
            entry.Defeated = true;
            entry.MercyCount++;
            entry.Rank = Math.Max(1, entry.Rank - 1);
            NemesisLogger.Info($"[Nemesis] {GetName()}: SPARED '{((PrototypeId)heroRef).GetName()}' — mercy #{entry.MercyCount}, rank knocked down to {entry.Rank}");
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
            //
            // Ranks 6-10 (2026-07-27) are Endless Wave-only — see
            // EndlessMaxRank's doc comment. HP keeps accelerating past rank 5
            // deliberately (a player this deep into a run has had far more
            // chest tiers/gear by then), while the damage-boost curve below
            // decelerates in comparison, so surviving this deep reads as a
            // sustained-DPS marathon rather than an instant kill either way.
            int r = Math.Clamp(rank, 0, EndlessMaxRank);
            return r switch
            {
                0 => 8.0f,   // baseline enemy — matches EnemyPhantomHealthMult
                1 => 8.5f,
                2 => 10.0f,
                3 => 12.0f,
                4 => 22.0f,  // buffed — a genuine wall
                5 => 32.0f,  // buffed — proper raid-boss HP pool
                6 => 45.0f,
                7 => 60.0f,
                8 => 80.0f,
                9 => 105.0f,
                10 => 140.0f,
                _ => 8.0f,
            };
        }

        // Fractional damage boost on top of the enemy phantom base curve —
        // multiplicative on top of DamageMult, which already includes
        // whatever the nemesis's equipped gear contributes.
        //
        // Rank 4/5 were rescaled down (2026-07-19) after two other fixes
        // landed this same session: rank 5 nemeses now correctly wear their
        // full BiS gear set with Legendaries actually leveled to max rank —
        // previously Legendaries silently stayed Unranked, so the 0.60/0.80
        // values here were tuned against a nemesis with a much weaker real
        // gear contribution than what's now landing. With working gear
        // added on top, the old values were "instantly destroying" friendly
        // hero/team-up phantoms (confirmed live). Halved to compensate for
        // the compounding instead of re-guessing a number from scratch.
        internal static float NemesisDmgBoostForRank(int rank)
        {
            // Ranks 6-10 are Endless Wave-only — see EndlessMaxRank.
            int r = Math.Clamp(rank, 0, EndlessMaxRank);
            return r switch
            {
                0 => 0.00f,
                1 => 0.05f,
                2 => 0.15f,
                3 => 0.25f,
                4 => 0.30f,  // rescaled down — was 0.60 before gear/Legendary-rank fixes
                5 => 0.40f,  // rescaled down — was 0.80 before gear/Legendary-rank fixes
                6 => 0.48f,
                7 => 0.55f,
                8 => 0.62f,
                9 => 0.70f,
                10 => 0.80f,
                _ => 0.00f,
            };
        }

        internal static string NemesisSuffixForRank(int rank)
        {
            int r = Math.Clamp(rank, 1, NemesisMaxRank);
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
