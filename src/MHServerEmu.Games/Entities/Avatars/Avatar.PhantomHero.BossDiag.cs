using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Diagnostic instrumentation for the "boss phantom sometimes spawns as /
    /// reverts to a normal boss" bug (reported 2026-08-09, reproduces on all
    /// three game versions, non-deterministic — sometimes an already-spawned
    /// boss regresses when another spawns, sometimes the newly-spawned one
    /// comes in already wrong).
    ///
    /// Three complementary probes, so we find the cause by evidence instead of
    /// by theory. Each is gated to boss phantoms (Agent.IsBossPhantom) and so
    /// can never fire for real story/endgame bosses:
    ///
    ///   1. WorldEntity.OnPropertyChange  — catches Rank / AllianceOverride
    ///      regressions WITH the stack that caused them.
    ///   2. AIController.SetIsEnabled     — catches the native brain being
    ///      switched back on, WITH the stack.
    ///   3. This file                     — a per-tick full-state sampler that
    ///      catches everything the other two structurally can't see (plain C#
    ///      flags, inventory moves, simulation/dormancy, owner rebinding),
    ///      by diffing a snapshot each tick.
    ///
    /// Probe 3 exists because probes 1 and 2 only cover the mechanisms I
    /// currently suspect. The sampler covers the ones I don't — if the real
    /// cause is something not theorized here, the delta line still shows it.
    /// </summary>
    public partial class Avatar
    {
        private static readonly Logger BossDiagLogger = LogManager.CreateLogger();

        /// <summary>Last-seen state per boss-phantom entity id.</summary>
        private static readonly Dictionary<ulong, string> s_bossDiagLastState = new();

        /// <summary>
        /// Compact, single-line, all-the-state-that-matters fingerprint of a
        /// boss phantom. Anything that distinguishes "friendly phantom
        /// teammate" from "normal hostile boss" belongs in here.
        /// </summary>
        private static string CaptureBossPhantomState(Agent boss, Avatar caller)
        {
            if (boss == null) return "<null>";

            string RefName(PrototypeId r) => r != PrototypeId.Invalid ? r.GetName() : "<unset>";

            PrototypeId rank = boss.Properties[PropertyEnum.Rank];
            PrototypeId allianceOverride = boss.Properties[PropertyEnum.AllianceOverride];
            PrototypeId allianceResolved = boss.Alliance != null ? boss.Alliance.DataRef : PrototypeId.Invalid;

            var ai = boss.AIController;
            string aiState = ai == null
                ? "none"
                : $"enabled={ai.IsEnabled},startsEnabled={ai.Blackboard?.PropertyCollection[PropertyEnum.AIStartsEnabled]}";

            var invLoc = boss.InventoryLocation;
            string invLabel = invLoc.InventoryConvenienceLabel.ToString();

            string hostile = caller != null ? boss.IsHostileTo(caller).ToString() : "<no-caller>";

            return $"rank={RefName(rank)} isPhantomHero={boss.IsPhantomHero} " +
                   $"allianceOverride={RefName(allianceOverride)} allianceResolved={RefName(allianceResolved)} " +
                   $"hostileToCaller={hostile} ai=[{aiState}] " +
                   $"powerUserOverride=0x{(ulong)boss.Properties[PropertyEnum.PowerUserOverrideID]:X} " +
                   $"invLoc={invLabel}(container=0x{invLoc.ContainerId:X}) " +
                   $"sim={boss.IsSimulated} dormant={boss.IsDormant} dead={boss.IsDead} " +
                   $"inWorld={boss.IsInWorld} lvl={boss.CharacterLevel}/{boss.CombatLevel}";
        }

        /// <summary>
        /// Records the baseline immediately after spawn finishes, so a boss
        /// that comes in ALREADY wrong is distinguishable from one that
        /// regresses later. Called at the tail of SpawnBossPhantomHero.
        /// </summary>
        internal static void BossDiagBaseline(Agent boss, Avatar caller, string stage)
        {
            if (boss == null || boss.IsBossPhantom == false) return;

            string state = CaptureBossPhantomState(boss, caller);
            s_bossDiagLastState[boss.Id] = state;
            BossDiagLogger.Info($"[BossDiag] BASELINE ({stage}) {boss} (id=0x{boss.Id:X}) {state}");
        }

        /// <summary>
        /// Per-tick sampler. Logs only on change, so a stable squad produces
        /// no output at all and a regression produces exactly one delta line
        /// naming the field that moved.
        /// </summary>
        internal static void BossDiagSample(Agent boss, Avatar caller)
        {
            if (boss == null || boss.IsBossPhantom == false) return;

            string now = CaptureBossPhantomState(boss, caller);
            if (s_bossDiagLastState.TryGetValue(boss.Id, out string prev) == false)
            {
                s_bossDiagLastState[boss.Id] = now;
                BossDiagLogger.Info($"[BossDiag] FIRST-SEEN {boss} (id=0x{boss.Id:X}) {now}");
                return;
            }

            if (prev == now) return;

            s_bossDiagLastState[boss.Id] = now;
            BossDiagLogger.Warn($"[BossDiag] STATE CHANGED {boss} (id=0x{boss.Id:X})\n" +
                                $"    before: {prev}\n" +
                                $"    after : {now}\n" +
                                $"    diff  : {DescribeBossDiagDelta(prev, now)}");
        }

        /// <summary>Names just the space-separated key=value pairs that differ.</summary>
        private static string DescribeBossDiagDelta(string before, string after)
        {
            var beforeParts = before.Split(' ');
            var afterParts = after.Split(' ');
            var changed = new List<string>();

            int n = Math.Min(beforeParts.Length, afterParts.Length);
            for (int i = 0; i < n; i++)
            {
                if (beforeParts[i] != afterParts[i])
                    changed.Add($"{beforeParts[i]} => {afterParts[i]}");
            }

            return changed.Count > 0 ? string.Join(" | ", changed) : "<shape changed>";
        }

        /// <summary>
        /// Region-wide scan for boss-prototype entities, reporting whether each
        /// is still TRACKED in the host's phantom lists or is an ORPHAN.
        ///
        /// Every other probe iterates the phantom id lists, so an entity that
        /// has been dropped from those lists is invisible to all of them — by
        /// definition exactly what an orphan is. This walks Region.Entities
        /// instead, so a leftover entity cannot hide from it.
        ///
        /// Nothing here is inferred: it prints the live entity id, prototype,
        /// tracked/orphan status, and in-world/simulated/dormant state, so the
        /// orphan theory can be confirmed or killed outright rather than
        /// assumed.
        /// </summary>
        internal static void BossDiagOrphanScan(Avatar caller, Player host, string stage)
        {
            if (caller == null || host == null) return;
            var region = caller.Region;
            if (region == null) return;

            var tracked = new HashSet<ulong>();
            foreach (ulong id in host.PhantomAvatarIds) tracked.Add(id);
            foreach (ulong id in host.EnemyPhantomAvatarIds) tracked.Add(id);

            var pool = Player.GetRawBossCandidatePool();
            int total = 0, orphans = 0;
            var sb = new System.Text.StringBuilder();

            foreach (Entity e in region.Entities)
            {
                if (e is not Agent agent) continue;
                if (pool.Contains(agent.PrototypeDataRef) == false) continue;

                total++;
                bool isTracked = tracked.Contains(agent.Id);
                if (isTracked == false) orphans++;

                sb.Append($"\n    {(isTracked ? "TRACKED" : "ORPHAN ")} id=0x{agent.Id:X} " +
                          $"{agent.PrototypeDataRef.GetName()} " +
                          $"isPhantomHero={agent.IsPhantomHero} inWorld={agent.IsInWorld} " +
                          $"sim={agent.IsSimulated} dormant={agent.IsDormant} dead={agent.IsDead} " +
                          $"destroyed={agent.IsDestroyed} " +
                          $"owner=0x{(agent.GetOwnerOfType<Player>()?.Id ?? 0):X}");
            }

            if (total == 0) return;

            BossDiagLogger.Warn($"[BossDiag:Orphan] ({stage}) region boss-prototype entities: " +
                                $"total={total} orphans={orphans}{sb}");
        }

        /// <summary>
        /// Logs the client-facing teardown of a boss phantom during purge, and
        /// — the point of this probe — whether any REAL player's AOI still
        /// holds interest in the entity AFTER it was destroyed.
        ///
        /// Motivated by a user-reported fact that no current probe explains:
        /// a boss spawned as the first spawn after server start is fine, but a
        /// boss created in any spawn that FOLLOWS a previous spawn/purge
        /// T-poses. Since every spawn purges and re-creates the whole squad,
        /// that points at the previous entity's teardown not reaching the
        /// client — leaving a stale client-side actor that the next entity of
        /// the same prototype collides with.
        ///
        /// If interestedAfter=True appears here, the client was never told the
        /// old entity went away, and that is the bug. If it is False for every
        /// player, this theory is dead and I will say so.
        /// </summary>
        internal static void BossDiagTeardown(Agent phantom, Game game, string phase)
        {
            if (phantom == null || phantom.IsBossPhantom == false) return;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[BossDiag:Teardown] ({phase}) id=0x{phantom.Id:X} {phantom.PrototypeDataRef.GetName()} " +
                      $"inWorld={phantom.IsInWorld} destroyed={phantom.IsDestroyed} sim={phantom.IsSimulated}");

            try
            {
                foreach (Player realPlayer in new PlayerIterator(game))
                {
                    if (realPlayer.PlayerConnection == null) continue;
                    var aoi = realPlayer.AOI;
                    if (aoi == null) continue;
                    bool interested = aoi.InterestedInEntity(phantom.Id);
                    sb.Append($" | client={realPlayer.GetName()} stillInterested={interested}");
                }
            }
            catch (Exception ex) { sb.Append($" | AOI check threw: {ex.Message}"); }

            BossDiagLogger.Warn(sb.ToString());
        }

        /// <summary>Drops tracking for a despawned boss phantom.</summary>
        internal static void BossDiagForget(ulong bossId)
        {
            s_bossDiagLastState.Remove(bossId);
            s_bossDiagLastPos.Remove(bossId);
        }

        // ---------------------------------------------------------------
        // Movement probe
        //
        // The state probes above proved server-side state is CORRECT and
        // stays correct (verified 2026-08-09: every boss baselines with
        // rank=<unset>, alliance=Players, hostileToCaller=False, native AI
        // off, and never regresses). So the reported "glitchy & stutter"
        // with 2+ bosses is a movement/simulation problem, not a state one.
        //
        // This traces per-tick motion for boss phantoms, but ONLY once two
        // or more are active — the exact reported repro condition — so a
        // single-boss session stays silent. Oscillation (position bouncing
        // between two points), repeated path failures, or Dormant flapping
        // will all be visible directly in the trace.
        // ---------------------------------------------------------------
        private static readonly Dictionary<ulong, Vector3> s_bossDiagLastPos = new();

        internal static void BossDiagMovementSample(Agent boss, Avatar caller, int bossPhantomCount)
        {
            if (boss == null) return;
            // Gate was 2+ while the report was "breaks when a 2nd spawns".
            // Confirmed 2026-08-09 that a SINGLE boss reproduces it, so the
            // 2-boss interaction is ruled out and the trace must cover one.
            if (bossPhantomCount < 1) return;

            // Trace AVATAR phantoms too, not just bosses. The user reports
            // avatar phantoms move fine while boss phantoms stutter — but both
            // run the identical friendly idle-follow code (PathTo(slot) then
            // Stop() on arrival). Logging both under the same conditions is
            // the controlled comparison that decides whether the stutter is
            // boss-specific or is the shared follow design being more visible
            // on bosses. Without this we'd be guessing which.
            string kind = boss.IsBossPhantom ? "BOSS" : (boss.IsTeamUpAgent ? "TEAMUP" : "AVATAR");

            Vector3 pos = boss.RegionLocation.Position;
            float moved = s_bossDiagLastPos.TryGetValue(boss.Id, out Vector3 prev)
                ? Vector3.Distance2D(prev, pos)
                : -1f;
            s_bossDiagLastPos[boss.Id] = pos;

            var loco = boss.Locomotor;
            string locoState = loco == null
                ? "none"
                : $"locomoting={loco.IsLocomoting},following={loco.IsFollowingEntity},followId=0x{loco.FollowEntityId:X},hasPath={loco.HasPath}";

            float distToCaller = caller != null
                ? Vector3.Distance2D(caller.RegionLocation.Position, pos)
                : -1f;

            BossDiagLogger.Info($"[BossDiag:Move] [{kind}] {boss} (id=0x{boss.Id:X}) pos={pos.ToStringNames()} " +
                                $"movedSinceLastTick={moved:F1} distToCaller={distToCaller:F0} " +
                                $"loco=[{locoState}] dormant={boss.IsDormant} sim={boss.IsSimulated} " +
                                $"bounds={boss.Bounds.Radius:F0}");
        }
    }
}
