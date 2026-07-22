using System;
using System.Collections.Generic;
using System.Text;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities.Avatars
{
    // Whole-roster offline power audit — answers "which enemy phantom
    // powers can one-shot a friendly phantom" for EVERY hero/team-up in
    // one static pass, instead of waiting to see which one happens to
    // come up in a live fight (2026-07-21 balance investigation).
    //
    // Deliberately does not spawn anything: IsUltimate,
    // DamageBasePctTargetHealthCur/Max, AoE shape, and range are all
    // baked into PowerPrototype's own PrototypePropertyCollection and
    // resolve without any owning entity — unlike DamageBase (a curve
    // property indexed by live PowerRank, see EstimatePhantomPowerDamage),
    // these are the two categories that are structurally dangerous
    // REGARDLESS of stat/gear scaling: an Ultimate is a big scripted burst,
    // and a %-health execute deals damage proportional to the TARGET's own
    // HP pool, so raising phantom HealthMult can never neutralize one —
    // doubling the target's HP just requires double the damage to still
    // land the same percentage.
    public partial class Avatar
    {
        public sealed class PowerAuditEntry
        {
            public string HeroName;
            public bool IsTeamUp;
            public string PowerName;
            public bool IsUltimate;
            public bool IsAoE;
            public float PctCurHealthDmg;
            public float PctMaxHealthDmg;
            public float Range;
        }

        /// <summary>
        /// Scans the entire phantom-spawnable roster (every playable avatar
        /// and every team-up) and logs every enemy-targeting, non-passive
        /// power's IsUltimate/percent-health-execute/AoE profile. Fully
        /// static — reads prototype data only, spawns nothing, has no
        /// gameplay side effects, and is safe to run at any time.
        /// </summary>
        public static void RunEnemyPhantomPowerAudit(out int heroCount, out int powerCount, out List<PowerAuditEntry> dangerous)
        {
            EnsureResolvedPool();

            var logger = PhantomLogger;
            heroCount = 0;
            powerCount = 0;
            dangerous = new List<PowerAuditEntry>();

            logger.Info("[PowerAudit] ===== Enemy phantom power audit starting =====");

            foreach (PrototypeId avatarRef in s_phantomResolved)
            {
                AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
                if (avatarProto == null) continue;
                heroCount++;
                string heroName = avatarRef.GetName() ?? avatarRef.ToString();

                if (avatarProto.PowerProgressionTables == null) continue;
                foreach (var table in avatarProto.PowerProgressionTables)
                {
                    if (table?.PowerProgressionEntries == null) continue;
                    foreach (var entry in table.PowerProgressionEntries)
                    {
                        PrototypeId powerRef = entry?.PowerAssignment?.Ability ?? PrototypeId.Invalid;
                        if (powerRef == PrototypeId.Invalid) continue;

                        AuditOnePower(heroName, isTeamUp: false, powerRef, ref powerCount, dangerous, logger);
                    }
                }
            }

            foreach (PrototypeId teamUpRef in s_phantomTeamUpResolved)
            {
                AgentTeamUpPrototype teamUpProto = teamUpRef.As<AgentTeamUpPrototype>();
                if (teamUpProto == null) continue;
                heroCount++;
                string teamUpName = teamUpRef.GetName() ?? teamUpRef.ToString();

                if (teamUpProto.PowerProgression == null) continue;
                foreach (var entry in teamUpProto.PowerProgression)
                {
                    PrototypeId powerRef = entry?.Power ?? PrototypeId.Invalid;
                    if (powerRef == PrototypeId.Invalid) continue;

                    AuditOnePower(teamUpName, isTeamUp: true, powerRef, ref powerCount, dangerous, logger);
                }
            }

            var summary = new StringBuilder();
            summary.Append($"[PowerAudit] ===== SUMMARY: {heroCount} heroes/team-ups, {powerCount} enemy-targeting powers scanned, {dangerous.Count} flagged as structurally dangerous (Ultimate or %-health execute) =====\n");
            foreach (var d in dangerous)
            {
                string tag = d.PctMaxHealthDmg > 0f || d.PctCurHealthDmg > 0f ? "PCT-HEALTH-EXECUTE" : "ULTIMATE";
                summary.Append($"  [{tag}] {d.HeroName}{(d.IsTeamUp ? " (team-up)" : "")} — {d.PowerName}");
                if (d.PctCurHealthDmg > 0f) summary.Append($" pctCurHealthDmg={d.PctCurHealthDmg:F2}");
                if (d.PctMaxHealthDmg > 0f) summary.Append($" pctMaxHealthDmg={d.PctMaxHealthDmg:F2}");
                if (d.IsAoE) summary.Append(" [AoE]");
                summary.Append('\n');
            }
            logger.Info(summary.ToString());
            logger.Info("[PowerAudit] ===== Enemy phantom power audit complete =====");
        }

        public sealed class DamageAuditEntry
        {
            public string HeroName;
            public bool IsTeamUp;
            public int Rank;
            public string PowerName;
            public bool IsUltimate;
            public bool IsAoE;
            public float EstBaseDamage;
            public float DmgMult;
            public float DmgPctBonus;
            public float DmgRating;
            public float EffectiveEstimate;
        }

        // Ranks sampled per hero: 0 (plain rogue floor) and 5 (worst-case
        // ceiling — full nemesis gear/rank stacking). Not all 6 ranks,
        // because this spawns a REAL phantom per (hero, rank) pair to get
        // REAL numbers (see the method doc below for why), and 129 heroes x
        // 6 ranks would be 774 spawn/despawn cycles for marginal extra
        // coverage over just the two extremes.
        private static readonly int[] DamageAuditRanks = { 0, 5 };

        /// <summary>
        /// Comprehensive per-power REAL damage audit across the entire
        /// phantom-spawnable roster — every hero, every team-up, every
        /// power they could actually use in combat, not just the Ultimate/
        /// %-health-execute subset RunEnemyPhantomPowerAudit flags.
        /// </summary>
        /// <remarks>
        /// Unlike the structural scan above, this ACTUALLY SPAWNS a real
        /// enemy phantom per (hero, rank) pair — briefly, immediately
        /// despawned — because DamageBase is a curve property indexed by
        /// live PowerRank, and DamageMult/DamagePctBonus/DamageRating only
        /// exist on a live, gear-equipped entity. Trying to read these
        /// offline from the bare prototype (the way the structural scan
        /// reads IsUltimate/AoE/pct-health, which ARE static prototype
        /// fields) would mean either guessing at an unverified curve-index
        /// fallback or just being wrong — exactly the mistake the earlier
        /// version of this tool made by only checking two structural flags
        /// and silently missing every plain-high-damage power (confirmed
        /// live: SolarOvercharge, GammaPunch, LeapImplodeEnd, Loki's
        /// IllusionRushDecoyPowerCollide all hit 50-100% of a phantom's max
        /// HP without being an Ultimate or a %-health power).
        ///
        /// Requires the calling avatar to be in-world (spawns position
        /// relative to the caller) — this WILL cause a brief, rapid flicker
        /// of phantoms spawning and despawning near you as it works through
        /// the roster. Gear is randomly rolled per spawn (same as any real
        /// spawn), so a given run is one real sample of possible output, not
        /// a hard ceiling — re-running can surface different numbers for
        /// the same hero/rank if a different gear roll lands.
        /// </remarks>
        public void RunEnemyPhantomPowerDamageAudit(out int spawnAttempts, out int spawnFailures, out List<DamageAuditEntry> allEntries)
        {
            EnsureResolvedPool();

            var logger = PhantomLogger;
            spawnAttempts = 0;
            spawnFailures = 0;
            allEntries = new List<DamageAuditEntry>();

            logger.Info("[PowerDamageAudit] ===== Real per-power damage audit starting (this will spawn/despawn phantoms rapidly near you) =====");

            foreach (PrototypeId avatarRef in s_phantomResolved)
                AuditHeroDamage(avatarRef, isTeamUp: false, ref spawnAttempts, ref spawnFailures, allEntries, logger);

            foreach (PrototypeId teamUpRef in s_phantomTeamUpResolved)
                AuditHeroDamage(teamUpRef, isTeamUp: true, ref spawnAttempts, ref spawnFailures, allEntries, logger);

            // Full per-power detail — every single measured power, not just
            // the top 40. Grouped by (rank, hero) so the log reads in spawn
            // order rather than requiring the summary alone to be trusted.
            var byHeroRank = new StringBuilder();
            byHeroRank.Append($"[PowerDamageAudit] ===== FULL DETAIL: all {allEntries.Count} measured powers =====\n");
            foreach (var d in allEntries)
            {
                byHeroRank.Append($"  {d.HeroName}{(d.IsTeamUp ? " (team-up)" : "")} rank={d.Rank} :: {d.PowerName} " +
                    $"estBaseDamage={d.EstBaseDamage:F0} dmgMult={d.DmgMult:F2} dmgPctBonus={d.DmgPctBonus:F2} " +
                    $"effectiveEstimate={d.EffectiveEstimate:F0} isUltimate={d.IsUltimate} isAoE={d.IsAoE}\n");
            }
            logger.Info(byHeroRank.ToString());

            // Distribution check — BEFORE trusting any ranking, verify
            // whether EstBaseDamage is genuinely varied across the roster or
            // whether most powers collapse onto a handful of shared values.
            // A small number of buckets covering most of the roster could
            // mean either (a) real shared client-data baselines for filler/
            // combo attacks (this exact clustering, e.g. repeated "3024" at
            // level 60, already appeared in the FIRST live-combat damage-
            // scoring verification this session and tracked correctly
            // against real applied hits there), or (b) EstimatePhantomPowerDamage
            // silently defaulting for powers whose real damage comes from a
            // mechanism other than DamageBase (DoT/summon/proc). This report
            // doesn't resolve which one it is by itself — it's the evidence
            // needed to investigate that honestly instead of assuming either
            // way.
            var buckets = new Dictionary<float, int>();
            foreach (var d in allEntries)
                buckets[d.EstBaseDamage] = buckets.TryGetValue(d.EstBaseDamage, out int c) ? c + 1 : 1;
            var bucketList = new List<KeyValuePair<float, int>>(buckets);
            bucketList.Sort((a, b) => b.Value.CompareTo(a.Value));

            var dist = new StringBuilder();
            dist.Append($"[PowerDamageAudit] ===== DISTRIBUTION: {bucketList.Count} distinct estBaseDamage values across {allEntries.Count} measured powers =====\n");
            int distShown = Math.Min(20, bucketList.Count);
            for (int i = 0; i < distShown; i++)
                dist.Append($"  estBaseDamage={bucketList[i].Key:F0} :: {bucketList[i].Value} powers ({(100.0 * bucketList[i].Value / allEntries.Count):F1}%)\n");
            logger.Info(dist.ToString());

            allEntries.Sort((a, b) => b.EffectiveEstimate.CompareTo(a.EffectiveEstimate));

            var summary = new StringBuilder();
            summary.Append($"[PowerDamageAudit] ===== SUMMARY: {spawnAttempts} spawn attempts ({spawnFailures} failed), {allEntries.Count} powers measured. Top 40 by estimated real hit, worst first: =====\n");
            int shown = Math.Min(40, allEntries.Count);
            for (int i = 0; i < shown; i++)
            {
                var d = allEntries[i];
                summary.Append($"  #{i + 1,2} {d.HeroName}{(d.IsTeamUp ? " (team-up)" : "")} rank={d.Rank} — {d.PowerName} " +
                    $"estEffectiveHit={d.EffectiveEstimate:F0} (base={d.EstBaseDamage:F0} x dmgMult={d.DmgMult:F2} x (1+pctBonus={d.DmgPctBonus:F2})) " +
                    $"dmgRating={d.DmgRating:F0} isUltimate={d.IsUltimate} isAoE={d.IsAoE}\n");
            }
            logger.Info(summary.ToString());
            logger.Info("[PowerDamageAudit] ===== Real per-power damage audit complete =====");
        }

        private void AuditHeroDamage(PrototypeId heroRef, bool isTeamUp, ref int spawnAttempts, ref int spawnFailures,
            List<DamageAuditEntry> allEntries, Logger logger)
        {
            string heroName = heroRef.GetName() ?? heroRef.ToString();

            foreach (int rank in DamageAuditRanks)
            {
                spawnAttempts++;
                ulong id;
                string error;
                try
                {
                    id = isTeamUp
                        ? SpawnTeamUpPhantomHero(heroRef, 60, out error, enemy: true, nemesisRank: rank, usernameOverride: "PowerAuditBot")
                        : (rank > 0
                            ? SpawnNemesisPhantomHero(heroRef, 60, "PowerAuditBot", rank, out error)
                            : SpawnEnemyPhantomHero(heroRef, 60, out error));
                }
                catch (Exception ex)
                {
                    logger.Warn($"[PowerDamageAudit] spawn threw for {heroName} rank={rank}: {ex.Message}");
                    spawnFailures++;
                    continue;
                }

                if (id == 0)
                {
                    spawnFailures++;
                    continue;
                }

                try
                {
                    Agent phantom = Game.EntityManager.GetEntity<Agent>(id);
                    if (phantom == null) { spawnFailures++; continue; }

                    float dmgMult = phantom.Properties[PropertyEnum.DamageMult];
                    float dmgPctBonus = phantom.Properties[PropertyEnum.DamagePctBonus];
                    float dmgRating = phantom.Properties[PropertyEnum.DamageRating];
                    int combatLevel = phantom.CombatLevel;

                    var pc = phantom.PowerCollection;
                    if (pc == null) continue;

                    foreach (var kvp in pc)
                    {
                        Power power = kvp.Value?.Power;
                        PowerPrototype pp = power?.Prototype;
                        if (pp == null) continue;

                        // Same eligibility filter TryPhantomAttack's candidate
                        // loop uses — only powers an enemy phantom could
                        // actually pick in combat.
                        if (pp is MovementPowerPrototype) continue;
                        if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                        if (pp.Activation == PowerActivationType.Passive) continue;
                        if (pp.IsToggled) continue;
                        if (pp.IsTravelPower) continue;

                        var reach = pp.GetTargetingReach();
                        if (reach == null || reach.TargetsEnemy == false) continue;

                        float estBase = EstimatePhantomPowerDamage(power, combatLevel);
                        // dmgRating's real conversion to a damage multiplier
                        // isn't reverse-engineered here — reported separately
                        // rather than folded into effectiveEstimate, so this
                        // number is a verified-correct partial estimate, not
                        // a guessed-complete one.
                        float effective = estBase * dmgMult * (1f + dmgPctBonus);

                        allEntries.Add(new DamageAuditEntry
                        {
                            HeroName = heroName,
                            IsTeamUp = isTeamUp,
                            Rank = rank,
                            PowerName = kvp.Key.GetName() ?? kvp.Key.ToString(),
                            IsUltimate = pp.IsUltimate,
                            IsAoE = Power.TargetsAOE(pp),
                            EstBaseDamage = estBase,
                            DmgMult = dmgMult,
                            DmgPctBonus = dmgPctBonus,
                            DmgRating = dmgRating,
                            EffectiveEstimate = effective,
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn($"[PowerDamageAudit] measurement threw for {heroName} rank={rank}: {ex.Message}");
                }
                finally
                {
                    try { DespawnOneEnemyPhantom(id); }
                    catch (Exception ex) { logger.Warn($"[PowerDamageAudit] despawn failed for {heroName} rank={rank}: {ex.Message}"); }
                }
            }
        }

        private static void AuditOnePower(string heroName, bool isTeamUp, PrototypeId powerRef, ref int powerCount,
            List<PowerAuditEntry> dangerous, Logger logger)
        {
            PowerPrototype pp = powerRef.As<PowerPrototype>();
            if (pp == null) return;

            // Same eligibility filter TryPhantomAttack's candidate loop uses —
            // only powers an enemy phantom could actually pick in combat.
            if (pp is MovementPowerPrototype) return;
            if (pp.PowerCategory != PowerCategoryType.NormalPower) return;
            if (pp.Activation == PowerActivationType.Passive) return;
            if (pp.IsToggled) return;
            if (pp.IsTravelPower) return;

            var reach = pp.GetTargetingReach();
            if (reach == null || reach.TargetsEnemy == false) return;

            powerCount++;

            bool isUltimate = pp.IsUltimate;
            float pctCur = pp.Properties?[PropertyEnum.DamageBasePctTargetHealthCur] ?? 0f;
            float pctMax = pp.Properties?[PropertyEnum.DamageBasePctTargetHealthMax] ?? 0f;
            bool isAoE = Power.TargetsAOE(pp);
            float range = 0f;
            try { range = pp.Radius > 0f ? pp.Radius : 0f; } catch { /* not every power exposes a meaningful radius */ }

            string powerName = powerRef.GetName() ?? powerRef.ToString();

            logger.Info($"[PowerAudit] {heroName}{(isTeamUp ? " (team-up)" : "")} :: {powerName} isUltimate={isUltimate} isAoE={isAoE} pctCurHealthDmg={pctCur:F2} pctMaxHealthDmg={pctMax:F2}");

            if (isUltimate || pctCur > 0f || pctMax > 0f)
            {
                dangerous.Add(new PowerAuditEntry
                {
                    HeroName = heroName,
                    IsTeamUp = isTeamUp,
                    PowerName = powerName,
                    IsUltimate = isUltimate,
                    IsAoE = isAoE,
                    PctCurHealthDmg = pctCur,
                    PctMaxHealthDmg = pctMax,
                    Range = range,
                });
            }
        }
    }
}
