using System;
using System.Collections.Generic;
using System.Linq;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Powers.Conditions;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Phantom-hero spawning: creates a Player entity in this Game (no
    /// PlayerConnection, no DB persistence) that owns a real AvatarPrototype
    /// entity from the actual playable roster. Bypasses the login pipeline �
    /// the Player exists only inside this Game's EntityManager.
    ///
    /// Why: the engine's Avatar.ApplyInitialReplicationState hard-requires the
    /// EntitySettings.InventoryLocation.ContainerId to resolve to a Player
    /// entity (Verify.IsNotNull at Avatar.cs:150). Without a Player owner the
    /// Avatar refuses to spawn. Hero-shaped Agent variants (CivilWar bosses,
    /// Skrull-hero variants, etc.) skip that check because they're Agents, not
    /// Avatars � which is why the old bot pool used them and why the names
    /// came out as "Skrull Luke Cage" etc. This gives us the real 64 heroes.
    /// </summary>
    public partial class Avatar
    {
        private static readonly Logger PhantomLogger = LogManager.CreateLogger();

        // No hardcoded roster � the pool is built at first spawn by iterating the
        // client's actual AvatarPrototype hierarchy (NoAbstractApprovedOnly, i.e.
        // concrete + shipping-approved entries). This makes the mod version-
        // agnostic: whatever heroes the currently-loaded client data ships with
        // become spawn candidates automatically. See EnsureResolvedPool().

        private static readonly object s_phantomDeckLock = new();
        private static readonly List<int> s_phantomDeck = new();
        private static int s_phantomDeckIdx;
        private static ulong s_phantomDbIdSeed = 0xB07_FADED_0000_0001UL;

        // Ownership lists moved to Player (see Player.PhantomHero.cs). The
        // Avatar shell no longer owns anything � every operation delegates to
        // GetOwnerOfType<Player>() so avatar swaps and region hops don't
        // orphan phantoms. Left in place for source-compat: PhantomHeroCount
        // + the Ids reader now pull straight from the Player's list.
        private Player PhantomHost => GetOwnerOfType<Player>();
        public int PhantomHeroCount => PhantomHost?.PhantomHeroCount ?? 0;
        private IReadOnlyList<ulong> PhantomIds => PhantomHost?.PhantomAvatarIds ?? (IReadOnlyList<ulong>)System.Array.Empty<ulong>();

        // Comic-book flavored random usernames. Kept short so nameplates fit.
        private static readonly string[] s_phantomAdjectives =
            { "Crimson", "Cosmic", "Silent", "Void", "Neon", "Feral", "Prime", "Shadow", "Solar", "Astral", "Rogue", "Onyx", "Phoenix", "Nova", "Iron", "Storm", "Cyber", "Ghost", "Wraith", "Phantom" };
        private static readonly string[] s_phantomNouns =
            { "Falcon", "Warden", "Reaper", "Nomad", "Sentinel", "Vector", "Specter", "Vanguard", "Sable", "Pulse", "Envoy", "Titan", "Arbiter", "Herald", "Blade", "Fury", "Strike", "Guard", "Reign", "Shade" };
        private static int s_phantomNameCounter;

        private static string NewPhantomUsername(MHServerEmu.Core.System.Random.GRandom rng)
        {
            string a = s_phantomAdjectives[rng.Next(0, s_phantomAdjectives.Length)];
            string n = s_phantomNouns[rng.Next(0, s_phantomNouns.Length)];
            int suffix = System.Threading.Interlocked.Increment(ref s_phantomNameCounter) % 1000;
            return $"{a}{n}{suffix:D3}";
        }

        // Follow-tick constants + scheduler state. Phantoms have no locomotion AI,
        // so they'd stay glued to their spawn spot. Every ~1s, if a phantom is more
        // than 1500u from the caller, we teleport it back with a random offset so
        // multiple phantoms spread out. Also fires a random offensive power at any
        // nearby hostile so they don't just stand around.
        // Tighter than the old 2500u � phantoms should read as "with you"
        // not "vaguely nearby." 1500u � two-thirds of a screen at default
        // zoom; if they wander beyond that the leash snaps them back.
        private const float PhantomFollowMaxDistSq = 1500f * 1500f;
        // Enemy phantoms use a tighter leash � 900u instead of 1500u � so a
        // rogue that got left behind (player died + revived at waypoint, or
        // player just walked away from the fight) teleports onto the player
        // faster and NEVER breaks pursuit. Combined with the "no candidates ->
        // chase caller" fallback in UpdatePhantomHunt, rogues never idle.
        private const float EnemyPhantomFollowMaxDistSq = 900f * 900f;
        // Friendly team-up respawn cooldown after death (45s). Team-ups die
        // permanently rather than entering the downed/revive flow avatar
        // phantoms use, so instead the tick loop re-spawns them from the
        // stored descriptor 45s later. Long enough that the current fight
        // has resolved; short enough that the squad doesn't feel gutted.
        private const long TeamUpRespawnDelayMs = 45_000;
        // Idle-formation ring radius around the caller. When friendly
        // phantoms have no hostile in range, they PathTo an evenly-spaced
        // slot at this distance so they walk with you through hubs / between
        // fights instead of teleport-leashing. Adapted from lordunborn's fork.
        private const float PhantomIdleFollowStopDist = 200f;
        // Bigger arrival zone than lord's 60u so phantoms don't twitch
        // constantly correcting position � once they're within this radius
        // of their slot they Stop and stay stopped until the caller moves
        // meaningfully.
        private const float PhantomFormationArriveDist = 120f;
        // Stuck detection: if the phantom's position barely changes across
        // this many ticks (500ms each) they're either wall-clipped or
        // pathed into an out-of-bounds corner � force a teleport back to
        // caller.
        private const int PhantomStuckTickThreshold = 4;      // 2 seconds
        private const float PhantomStuckMoveEpsilonSq = 40f * 40f;
        private static readonly Dictionary<ulong, (Vector3 lastPos, int stuckTicks)> s_phantomStuckTrack = new();
        private const float PhantomAttackRange = 1200f;
        private const float PhantomAttackRangeSq = PhantomAttackRange * PhantomAttackRange;
        // True melee reach � used to gate melee powers regardless of what
        // Power.GetRange() reports for them (some melee prototypes report a
        // very small or zero numeric range, which the general per-power
        // range filter in TryPhantomAttack would otherwise treat as
        // "unlimited" and fire from all the way out at PhantomAttackRange).
        private const float PhantomMeleeRange = 400f;
        private const float PhantomMeleeRangeSq = PhantomMeleeRange * PhantomMeleeRange;
        // Wider search � phantom will walk to any hostile in this radius.
        private const float PhantomSearchRange = 3500f;
        private const float PhantomSearchRangeSq = PhantomSearchRange * PhantomSearchRange;
        // Cap on how far a friendly phantom will engage from the CALLER
        // (not the phantom's own position). Without this, a phantom that
        // starts 200u from the caller can spot an enemy 3500u further out
        // and chase across the map � the "phantom sprints off after
        // something you can't even see on screen" bug. ~1000u � half a
        // screen at default zoom, so this keeps the fight visible.
        private const float PhantomFriendlyEngageMaxCallerDist = 1000f;
        private const float PhantomFriendlyEngageMaxCallerDistSq = PhantomFriendlyEngageMaxCallerDist * PhantomFriendlyEngageMaxCallerDist;

        // Ambush phantoms (Rogue Encounter spawns and tracked nemeses � NOT
        // the manual Enemy Phantoms farm-tool spawns, which stay instantly
        // aggressive by design) get a stealthier profile: spawn well clear of
        // the player instead of on top of them, then actively search for one
        // (see UpdateNemesisPatrol) until a player actually comes within
        // detection range of THEM � see the ambush branch in
        // SpawnPhantomHeroCore's spawn-position block and UpdatePhantomHunt's
        // enemyMode candidate filter / no-candidates fallback.
        private const float NemesisSpawnMinRadius = 1200f;
        private const float NemesisSpawnMaxRadius = 2000f;
        private const float NemesisDetectRange = 1800f;
        private const float NemesisDetectRangeSq = NemesisDetectRange * NemesisDetectRange;
        // Search-point wander radius around the phantom's current search
        // anchor � keeps each leg of the search looking organic (not a
        // beeline) instead of walking a laser-straight line toward the caller.
        private const float NemesisPatrolRadius = 600f;
        private const long NemesisPatrolRepickMs = 10_000; // re-roll a random patrol point at least this often
        // "Hunting" instead of static patrolling: each repick, the search
        // anchor itself advances up to this far toward the caller's current
        // position (never past it) before a random point is picked around
        // it. Confirmed live: pure random wander near the fixed spawn point
        // read as inert/stuck, especially when spawn landed somewhere
        // secluded � this makes them close in over time like they're
        // actually searching, while UpdatePhantomHunt's detect-range gate
        // still governs when a real chase/fight actually starts.
        private const float NemesisHuntAdvanceDist = 500f;
        private static readonly HashSet<ulong> s_enemyPhantomAmbush = new();
        private static readonly Dictionary<ulong, Vector3> s_nemesisSpawnAnchor = new();
        private static readonly Dictionary<ulong, (Vector3 target, long nextPickMs)> s_nemesisPatrol = new();

        // Tick twice as often so movement + attack feel snappy.
        private static readonly TimeSpan PhantomTickInterval = TimeSpan.FromMilliseconds(500);

        private readonly EventGroup _phantomPendingEvents = new();
        private readonly EventPointer<PhantomTickEvent> _phantomTick = new();

        private void SchedulePhantomTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_phantomTick.IsValid) return;
            scheduler.ScheduleEvent(_phantomTick, PhantomTickInterval, _phantomPendingEvents);
            _phantomTick.Get().Initialize(this);
        }

        private void OnPhantomTick()
        {
            Player host = PhantomHost;
            if (host == null) return;
            if (host.PhantomHeroCount == 0 && host.EnemyPhantomCount == 0) return;

            // ALWAYS re-arm before doing anything else so a transient
            // IsInWorld=false (auto-revive proc, downed-then-released state,
            // brief teleport window) doesn't permanently kill the tick loop.
            // Previously one flicker ? tick returned without rescheduling
            // ? enemy phantoms stood frozen for the rest of the session.
            // The bottom-of-function reschedule is now belt-and-suspenders.
            SchedulePhantomTick();

            // If the caller's Avatar is briefly not in world (mid-teleport,
            // mid-auto-revive) we can't do a proximity sweep � skip this
            // tick's work, but the tick loop keeps running for next time.
            if (IsInWorld == false) return;

            // Event hook � OnPlayerLowHP, once per dip below the same
            // threshold TryPhantomSurvivalRetreat uses for phantoms, so a
            // future support-behavior subscriber (or the debug logger) has
            // a consistent definition of "low" across the whole squad.
            float callerHealthMax = Properties[PropertyEnum.HealthMax];
            if (callerHealthMax > 0f)
            {
                float callerHpPct = (float)Properties[PropertyEnum.Health] / callerHealthMax;
                if (callerHpPct <= PhantomSoftRetreatHpPct)
                {
                    if (s_avatarLowHpNotified.Add(Id))
                        PhantomAIEvents.RaisePlayerLowHP(this);
                }
                else
                {
                    s_avatarLowHpNotified.Remove(Id);
                }
            }

            Vector3 callerPos = RegionLocation.Position;
            var rng = Game.Random;
            List<ulong> stale = null;
            var ids = host.PhantomAvatarIds; // snapshot count for stable iteration

            int callerLevel = CharacterLevel;

            // [BossDiag] Count live boss phantoms up front so the movement
            // probe can gate itself to the reported 2+ repro condition.
            int bossPhantomCount = 0;
            for (int bi = 0; bi < ids.Count; bi++)
            {
                var bp = Game.EntityManager.GetEntity<Agent>(ids[bi]);
                if (bp != null && bp.IsDestroyed == false && bp.IsBossPhantom) bossPhantomCount++;
            }

            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];
                // Widened from Avatar to Agent so team-up phantoms (which
                // are Agent, not Avatar) survive the tick and share the
                // same maintenance path (level sync, downed detection,
                // leash, party sync).
                Agent phantom = Game.EntityManager.GetEntity<Agent>(id);
                if (phantom == null || phantom.IsDestroyed)
                {
                    // Team-up/boss respawn hook: friendly team-up and boss
                    // phantoms that died get re-queued for spawn 90s later so
                    // the squad heals itself instead of shrinking permanently.
                    // Neither has an avatar-style downed/revive state.
                    var goneDescriptor = host.GetPhantomDescriptor(id);
                    if (goneDescriptor.AvatarRef != 0
                        && (((PrototypeId)goneDescriptor.AvatarRef).As<AgentTeamUpPrototype>() != null
                         || IsBossPhantomRef((PrototypeId)goneDescriptor.AvatarRef)))
                    {
                        long dueAt = (Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond) + TeamUpRespawnDelayMs;
                        host.EnqueueTeamUpRespawn(goneDescriptor, dueAt);
                        PhantomLogger.Info($"[PhantomHero:TeamUp:Respawn] queued team-up '{((PrototypeId)goneDescriptor.AvatarRef).GetName()}' respawn in {TeamUpRespawnDelayMs / 1000}s");
                    }
                    (stale ??= new List<ulong>()).Add(id);
                    s_phantomReattachGraceSinceMs.Remove(id);
                    BossDiagForget(id);
                    continue;
                }

                // [BossDiag] Sample boss-phantom state every tick so any
                // regression to "normal boss" is captured with a before/after
                // diff. No-ops for non-boss phantoms and logs nothing while
                // state is stable.
                BossDiagSample(phantom, this);
                BossDiagMovementSample(phantom, this, bossPhantomCount);

                if (phantom.IsInWorld == false)
                {
                    var descriptor = host.GetPhantomDescriptor(id);
                    bool isTeamUp = descriptor.AvatarRef != 0
                        && (((PrototypeId)descriptor.AvatarRef).As<AgentTeamUpPrototype>() != null
                         || IsBossPhantomRef((PrototypeId)descriptor.AvatarRef));

                    // Team-ups and boss phantoms have no persistent revive
                    // flow — keep their existing immediate-requeue behavior.
                    if (isTeamUp)
                    {
                        long dueAt = (Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond) + TeamUpRespawnDelayMs;
                        host.EnqueueTeamUpRespawn(descriptor, dueAt);
                        PhantomLogger.Info($"[PhantomHero:TeamUp:Respawn] queued team-up '{((PrototypeId)descriptor.AvatarRef).GetName()}' respawn in {TeamUpRespawnDelayMs / 1000}s");
                        (stale ??= new List<ulong>()).Add(id);
                        s_phantomReattachGraceSinceMs.Remove(id);
                        continue;
                    }

                    // Avatar/nemesis phantoms: this tick runs every 500ms, far
                    // more often than ReattachPhantomTick's once-per-world-
                    // enter check, and used to instant-unregister (silently
                    // dropping the phantom from the squad, no revive UI) on
                    // any transient not-in-world tick � confirmed live: a
                    // player lost 2 of 4 friendly phantoms mid-fight with no
                    // death shown. Give it the same grace window instead.
                    long nowMsGrace = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                    if (s_phantomReattachGraceSinceMs.TryGetValue(id, out long graceSince) == false)
                    {
                        s_phantomReattachGraceSinceMs[id] = nowMsGrace;
                        continue; // skip this tick's Hunt/leash work, but don't prune yet
                    }
                    if (nowMsGrace - graceSince < PhantomReattachGraceMs)
                        continue;

                    // Genuinely stuck out of world past the grace window.
                    (stale ??= new List<ulong>()).Add(id);
                    s_phantomReattachGraceSinceMs.Remove(id);
                    continue;
                }

                s_phantomReattachGraceSinceMs.Remove(id);

                // Level sync: keep phantoms at the caller's level in BOTH
                // directions, so a lvl-15 hero doesn't drag lvl-15 phantoms
                // into a lvl-60 mission and, equally, swapping down to a
                // low-level hero doesn't leave a squad of lvl-60 phantoms
                // trivialising its content. Runs every 500ms, but the whole
                // block is skipped unless the level actually differs.
                //
                // Phantoms spawned with an explicit level lock
                // (`!phantom spawn N L`) are skipped � the user asked for
                // a specific level and we honour it forever.
                if (callerLevel > 0 && phantom.CharacterLevel != callerLevel && host.IsPhantomLevelLocked(phantom.Id) == false)
                {
                    try
                    {
                        phantom.InitializeLevel(callerLevel);
                        phantom.CombatLevel = callerLevel;
                        // Rescale damage buffs so a level-1 phantom that
                        // just autolevelled to lvl-15 stops hitting like
                        // lvl-1 (or overshoots � the anchor curve tracks
                        // level, not spawn-time snapshot).
                        ApplyPhantomDamageScaling(phantom, callerLevel);
                        // Refresh the stored descriptor so cross-region
                        // migration re-spawns at the new level, not the
                        // stale spawn-time value.
                        host.UpdatePhantomLevel(phantom.Id, callerLevel);
                        // Gear has to be re-rolled for the new level. Levelling
                        // DOWN runs Avatar.OnLevelUp -> CheckEquipmentRestrictions(),
                        // which unequips everything the phantom no longer meets
                        // the level requirement for and would otherwise leave it
                        // stripped; levelling back UP then has to re-roll or the
                        // phantom would be stuck in whatever low-level gear the
                        // downlevel gave it.
                        RegearPhantomForLevel(host, phantom, callerLevel);
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] level sync {phantom.Id:X} ? {callerLevel} failed: {ex.Message}"); }
                }

                // Downed handling. A killable phantom that hit 0 HP stays
                // IsInWorld true but IsDead � same "downed" state real
                // players enter, so friendly phantoms and the human caller
                // can revive via ResurrectOtherAvatar. Phantoms stay down
                // until someone actually revives them � no auto-revive.
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (phantom.IsDead)
                {
                    // Mark the down-start so the next-tick alive branch can
                    // detect the IsDead ? alive transition and refresh the
                    // client-side pose.
                    if (s_phantomDownedSinceMs.ContainsKey(phantom.Id) == false)
                        s_phantomDownedSinceMs[phantom.Id] = nowMs;

                    // While downed, skip movement + hunt � a corpse doesn't
                    // walk. The revive priority in the hunt on OTHER phantoms
                    // will still find and raise this one.
                    continue;
                }

                // Alive path: if we were tracking this phantom as downed,
                // they got revived (by the caller or a friendly phantom via
                // ResurrectOtherAvatar). Force a locomotor refresh so the
                // client stops rendering the downed pose � without this the
                // phantom stays visually flat on the ground until the leash
                // eventually teleports them, which is what the user was
                // seeing on-screen. Same primitives the leash uses:
                // Locomotor.Stop() + ChangeRegionPosition to the current
                // spot triggers a NetMessage the client can process.
                if (s_phantomDownedSinceMs.Remove(phantom.Id))
                {
                    try
                    {
                        // Force-clear any power that was still active the
                        // moment this phantom went down. OnKilled/
                        // OnRemoveFromWorld never end active powers for
                        // Avatar-type entities (WorldEntity.OnRemoveFromWorld
                        // returns immediately for "this is Avatar" � that's
                        // what keeps phantoms revivable in place instead of
                        // being destroyed), so a phantom that died mid-cast
                        // keeps ActivePowerRef set through the entire downed
                        // period. The stuck-power watchdog below can't catch
                        // it either, since PhantomSharedMaintenance is never
                        // called while IsDead (see the continue above). Left
                        // alone, IsExecutingPower stays true after revival and
                        // UpdatePhantomHunt's very first check silently
                        // no-ops every tick � the phantom just stands there
                        // forever instead of resuming hunt/revive logic.
                        if (phantom.ActivePowerRef != PrototypeId.Invalid)
                        {
                            Power stuckPower = phantom.PowerCollection?.GetPower(phantom.ActivePowerRef);
                            stuckPower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force);
                        }
                        s_phantomActivePowerTrack.Remove(phantom.Id);

                        Vector3 herePos = phantom.RegionLocation.Position;
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(herePos, null);
                        PhantomLogger.Info($"[PhantomHero:Down] {phantom} revived � pose refreshed");
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Down] revive refresh failed: {ex.Message}"); }
                }

                // Watchdog + stuck detection + leash � shared with enemy phantoms.
                PhantomSharedMaintenance(phantom, callerPos, rng);

                // Team-up phantoms now run the SAME hunt logic avatar
                // phantoms use (threat scoring, real damage/AoE scoring,
                // kiting, hazard avoidance, support) instead of the engine's
                // native AIController � see SpawnTeamUpPhantomHero, which
                // disables the team-up's native brain once at spawn
                // specifically so this doesn't fight it. Verified safe: the
                // native brain is never silently re-enabled afterward
                // (Agent.Resurrect only re-enables it when
                // CanBePlayerOwned() is false, which is never true for
                // AgentTeamUpPrototype � see Entity.CanBePlayerOwned).
                //
                // Team-ups keep their own separate revive-of-others check
                // first (TryTeamUpReviveDowned uses a power resolved off the
                // CALLER's kit, not the team-up's � team-ups have no
                // AvatarPrototype of their own for UpdatePhantomHunt's
                // avatar-only revive path to use).
                if (phantom.IsTeamUpAgent && TryTeamUpReviveDowned(phantom))
                    continue;

                // A self-revive/"cheat death" proc's brief legitimate
                // Invulnerable/Untargetable/Unaffectable window should freeze
                // the phantom in place, not let it keep fighting � a real
                // player can't act during that animation either. Skip
                // movement/attack for this tick only; PhantomSharedMaintenance
                // above already ran, so the stuck-watchdog still forces this
                // clear if the flags never lift on their own.
                if (IsPhantomInReviveFreeze(phantom))
                {
                    phantom.Locomotor?.Stop();
                    continue;
                }

                // Hunt: locomotor-walk toward the nearest hostile in a wider sweep,
                // then attack once in range. Locomotor.FollowEntity refreshes each
                // tick (250ms repath delay) so the phantom will keep advancing.
                try { UpdatePhantomHunt(phantom, rng); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Hunt] {phantom.Id:X} threw: {ex.Message}"); }
            }

            if (stale != null)
                foreach (ulong id in stale) host.UnregisterPhantom(id);

            // ---- Enemy phantoms: hostile hunt + corpse cleanup ----
            if (host.EnemyPhantomCount > 0)
            {
                List<ulong> enemyGone = null;
                var enemyIds = host.EnemyPhantomAvatarIds;
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                for (int i = 0; i < enemyIds.Count; i++)
                {
                    ulong id = enemyIds[i];
                    // Widened for team-up phantoms.
                    Agent foe = Game.EntityManager.GetEntity<Agent>(id);
                    if (foe == null || foe.IsDestroyed || foe.IsInWorld == false)
                    {
                        (enemyGone ??= new List<ulong>()).Add(id);
                        continue;
                    }

                    // Corpse cleanup: enemy phantoms are killable but have no
                    // client-driven revive � despawn a few seconds after death
                    // so the win feels earned and the body doesn't linger.
                    if (foe.IsDead)
                    {
                        if (s_enemyDeadSinceMs.TryGetValue(id, out long deadSince) == false)
                        {
                            s_enemyDeadSinceMs[id] = nowMs;
                            // First tick that sees the corpse � close the
                            // revenge loop if this foe is on the host's
                            // nemesis roster. Guarded by the "not already
                            // tracked" check so we only retire once.
                            try
                            {
                                host.RetireNemesis((ulong)foe.PrototypeDataRef);
                                host.TryClaimBountyReward((ulong)foe.PrototypeDataRef);
                            }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] RetireNemesis failed on {foe.Id:X}: {ex.Message}"); }
                            // Drop the phantom's equipped gear as ground loot
                            // for the killer. Uses the phantom's exact rolled
                            // ItemSpec so what drops matches what was rolled at
                            // spawn (level-band appropriate � cosmic at 60,
                            // rare at 30, etc). Same one-shot guard so we don't
                            // duplicate on subsequent corpse ticks.
                            //
                            // Skipped entirely during an Endless Challenge run
                            // � that mode's only reward is the chest spawned
                            // every few waves, not per-kill gear drops. Also
                            // skipped for every phantom spawned by the Trial
                            // of the Impossible gauntlet (stages, finale, and
                            // finale hazards) � confirmed live 2026-07-23:
                            // regular stages were dropping gear when they
                            // shouldn't, and the finale would otherwise
                            // double-drop (this automatic roll PLUS the
                            // explicit DropTrialFinaleLoot lootsplosion).
                            //
                            // Checked by per-phantom id (Player.IsTrialSuppressedPhantom),
                            // NOT the live IsTrialGauntletActive flag �
                            // confirmed live 2026-07-31 that checking the live
                            // flag here raced against this same corpse-cleanup
                            // tick: the finale kill's own handler
                            // (OnTrialStageEntityDead) calls EndTrialRun
                            // synchronously right after spawning the reward
                            // chest, clearing IsTrialGauntletActive BEFORE
                            // this tick ever sees the fresh corpse, so the old
                            // flag-based check let the finale phantom's own
                            // gear drop through anyway, burying the real
                            // reward chest under an unwanted extra loot pile.
                            //
                            // IsEndlessChallengeActive below is STILL a live
                            // flag, unlike the per-id check next to it -- audited
                            // 2026-08-01 and confirmed this doesn't currently
                            // race, because nothing in Player.WaveDirector.cs
                            // clears it synchronously off an EntityDeadGameEvent
                            // (its wipe-check runs on an independent polling
                            // tick, not a kill handler). If a future change
                            // adds a synchronous "last kill ends the Endless
                            // run" path here (mirroring Trial's
                            // OnTrialStageEntityDead -> EndTrialRun chain),
                            // this line reintroduces the EXACT bug just fixed
                            // for Trial -- give it the same per-id-snapshot
                            // treatment (an IsEndlessSuppressedPhantom-style
                            // set populated at spawn time) instead of trusting
                            // the live flag here, same as IsTrialSuppressedPhantom.
                            if (host.IsEndlessChallengeActive == false
                                && host.IsTrialSuppressedPhantom(foe.Id) == false
                                && host.IsDeathmatchSuppressedPhantom(foe.Id) == false)
                            {
                                try { DropPhantomGear(foe, host); } catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] drop failed on {foe.Id:X}: {ex.Message}"); }
                            }
                            // Loot Goblin Hunt payout � a flagged fleeing
                            // phantom pays its bonus loot table on death
                            // instead of/on top of the normal gear drop.
                            if (s_fleeingBonusLootTableRef.TryGetValue(foe.Id, out ulong goblinLootRef) && goblinLootRef != 0)
                            {
                                try
                                {
                                    using var goblinLootHandle = Loot.LootInputSettingsPool.Get(out Loot.LootInputSettings goblinLoot);
                                    goblinLoot.Initialize(Loot.LootContext.Drop, host, foe);
                                    for (int gi = 0; gi < 5; gi++)
                                        Game.LootManager.SpawnLootFromTable((PrototypeId)goblinLootRef, goblinLoot, 1);
                                }
                                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] goblin payout failed: {ex.Message}"); }
                            }
                        }
                        else if (nowMs - deadSince >= EnemyPhantomCorpseMs)
                        {
                            try { if (foe.IsInWorld) foe.ExitWorld(); foe.Destroy(); }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Corpse] cleanup failed on {foe.Id:X}: {ex.Message}"); }
                            (enemyGone ??= new List<ulong>()).Add(id);
                            s_enemyDeadSinceMs.Remove(id);
                        }
                        continue;
                    }
                    s_enemyDeadSinceMs.Remove(id);

                    PhantomSharedMaintenance(foe, callerPos, rng);

                    // Same self-revive freeze as friendly phantoms below �
                    // don't let an enemy/nemesis phantom keep attacking
                    // during its own "cheat death" invulnerability window.
                    if (IsPhantomInReviveFreeze(foe))
                    {
                        foe.Locomotor?.Stop();
                        continue;
                    }

                    // Enemy team-up phantoms now run the same hunt logic as
                    // enemy avatar phantoms � native AI disabled once at
                    // spawn, same as friendly team-ups (see
                    // SpawnTeamUpPhantomHero).
                    //
                    // Hunt in enemy mode: no reviving, and the caller is a
                    // valid (primary!) target.
                    try { UpdatePhantomHunt(foe, rng, enemyMode: true); }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Hunt] enemy {foe.Id:X} threw: {ex.Message}"); }
                }

                if (enemyGone != null)
                    foreach (ulong id in enemyGone)
                    {
                        host.UnregisterEnemyPhantom(id);
                        s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); PrunePhantomAiStateFor(id);
                        s_phantomNextUltimateMs.Remove(id); s_phantomActivePowerTrack.Remove(id);
                        s_enemyDeadSinceMs.Remove(id); s_enemyPhantomRankLevel.Remove(id);
                        s_enemyPhantomAmbush.Remove(id); s_nemesisSpawnAnchor.Remove(id); s_nemesisPatrol.Remove(id);
                        s_fleeingPhantoms.Remove(id); s_fleeingBonusLootTableRef.Remove(id);
                        PruneBlacklistFor(id); PrunePowerBlacklistFor(id);
                        s_phantomInvulnerableTrack.Remove(id); s_phantomDeliberatelyInvincible.Remove(id);
                    }
            }

            // Drain any team-up phantom respawns whose 90s cooldown is up.
            // Same code path Rogue Encounter / manual spawn use, so all the
            // downstream tracking (party HUD, level sync, formation) works.
            long nowMsDrain = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            var respawnsDue = host.DrainTeamUpRespawnsDue(nowMsDrain);
            if (respawnsDue != null)
            {
                foreach (var entry in respawnsDue)
                {
                    try
                    {
                        var refId = (PrototypeId)entry.Descriptor.AvatarRef;
                        // Same queue, same 90s cooldown — a boss phantom uses
                        // it identically to a team-up, just dispatched to its
                        // own spawn method instead (see IsBossPhantomRef).
                        ulong id;
                        string err;
                        if (IsBossPhantomRef(refId))
                            id = SpawnBossPhantomHero(refId, entry.Descriptor.Level, out err, usernameOverride: entry.Descriptor.Username);
                        else
                            id = SpawnTeamUpPhantomHero(refId, entry.Descriptor.Level, out err, enemy: false, nemesisRank: 0, usernameOverride: entry.Descriptor.Username, gearOverride: entry.Descriptor.GearRefs);
                        if (id == 0)
                            PhantomLogger.Warn($"[PhantomHero:TeamUp:Respawn] respawn failed for {refId.GetName()}: {err} � re-queueing 30s");
                        // On failure, re-queue in 30s so a transient issue
                        // (region not loaded, entity budget) doesn't perma-lose
                        // the team-up.
                        if (id == 0)
                            host.EnqueueTeamUpRespawn(entry.Descriptor, nowMsDrain + 30_000);
                        else
                            PhantomLogger.Info($"[PhantomHero:TeamUp:Respawn] respawned team-up '{refId.GetName()}' (id=0x{id:X})");
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp:Respawn] threw: {ex.Message}"); }
                }
            }

            // Keep the tick alive if we have live phantoms OR pending
            // (future) respawns so the queue actually drains.
            if (host.PhantomHeroCount > 0 || host.EnemyPhantomCount > 0 || host.TeamUpRespawnQueueCount > 0)
                SchedulePhantomTick();
        }

        // Enemy-phantom corpse timers (avatar id -> death timestamp ms).
        private const long EnemyPhantomCorpseMs = 4000;
        private static readonly Dictionary<ulong, long> s_enemyDeadSinceMs = new();

        // Nemesis rank + level per enemy phantom (avatar id -> (rank, level)),
        // recorded at spawn so the death-drop path can apply the rank/level
        // loot tiering (BiS jackpot, down-tier drops, loot-splosion). Plain
        // rogues (rank 0) simply aren't in this dict.
        private static readonly Dictionary<ulong, (int rank, int level)> s_enemyPhantomRankLevel = new();

        // Loot Goblin Hunt (Player.FeatureLab.cs) � phantoms flagged here
        // flee instead of fighting; the bonus loot table dict pays out on
        // death instead of the normal phantom-gear drop.
        internal static readonly HashSet<ulong> s_fleeingPhantoms = new();
        internal static readonly Dictionary<ulong, ulong> s_fleeingBonusLootTableRef = new();
        internal const float LootGoblinFleeStandoff = 2000f;

        /// <summary>
        /// Public lookup for the balance/damage diagnostic in WorldEntity.cs �
        /// lets it tag a hit/heal with the attacker's real numeric nemesis
        /// rank (1-5) without needing this dictionary itself to be public.
        /// Returns false (rank/level both 0) for plain rogues, which
        /// intentionally aren't tracked here.
        /// </summary>
        internal static bool TryGetEnemyPhantomRank(ulong avatarId, out int rank, out int level)
        {
            if (s_enemyPhantomRankLevel.TryGetValue(avatarId, out var entry))
            {
                rank = entry.rank;
                level = entry.level;
                return true;
            }
            rank = 0;
            level = 0;
            return false;
        }

        /// <summary>
        /// Rogue/nemesis ambush phantoms actively hunt instead of standing
        /// around or leashing to the caller: each repick, the phantom's
        /// search anchor advances up to NemesisHuntAdvanceDist toward the
        /// caller's current position (never past it), then a random point
        /// within NemesisPatrolRadius of that (now-closer) anchor is picked
        /// and validated against the navmesh before committing to it � a
        /// live nemesis got stuck spawned onto an unreachable ledge before
        /// this validation existed. Repicks whenever the phantom arrives at
        /// its current target or the repick timer elapses.
        /// UpdatePhantomHunt's detect-range gate is what breaks them out of
        /// the hunt and into a real chase/fight once someone gets close.
        /// </summary>
        private void UpdateNemesisPatrol(Agent phantom, Region region, MHServerEmu.Core.System.Random.GRandom rng)
        {
            var loco = phantom.Locomotor;
            if (loco == null) return;

            if (!s_nemesisSpawnAnchor.TryGetValue(phantom.Id, out Vector3 anchor))
                anchor = phantom.RegionLocation.Position; // no anchor recorded (e.g. migrated) � hunt from here instead

            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            Vector3 phantomPos = phantom.RegionLocation.Position;

            bool needNewTarget = true;
            if (s_nemesisPatrol.TryGetValue(phantom.Id, out var patrol))
            {
                float arriveDistSq = PhantomFormationArriveDist * PhantomFormationArriveDist;
                bool arrived = Vector3.DistanceSquared2D(phantomPos, patrol.target) <= arriveDistSq;
                needNewTarget = arrived || nowMs >= patrol.nextPickMs;
            }
            if (needNewTarget == false) return;

            // Advance the search anchor toward the caller so the hunt actually
            // closes distance over time instead of looping the same small
            // area forever. Clamped so a single repick never overshoots past
            // the caller's own position (that would be indistinguishable
            // from the old leash behavior this replaced).
            Vector3 callerPos = RegionLocation.Position;
            float distToCallerSq = Vector3.DistanceSquared2D(anchor, callerPos);
            if (distToCallerSq > NemesisHuntAdvanceDist * NemesisHuntAdvanceDist)
            {
                Vector3 toCaller = callerPos - anchor;
                float distToCaller = MathF.Sqrt(distToCallerSq);
                anchor += toCaller * (NemesisHuntAdvanceDist / distToCaller);
            }
            else
            {
                anchor = callerPos;
            }
            s_nemesisSpawnAnchor[phantom.Id] = anchor;

            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            float navRadius = MathF.Max(20f, phantom.Bounds.Radius);
            Vector3 patrolTarget = anchor;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = (float)(rng.NextDouble() * NemesisPatrolRadius);
                Vector3 candidate = anchor + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0f);
                Vector3 floored = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(floored, navRadius, walkCheck))
                {
                    patrolTarget = floored;
                    break;
                }
                // Last attempt: fall back to the anchor itself rather than an
                // unvalidated point � the anchor was reachable when it was
                // last used as a target, or is the caller's own position.
                if (attempt == 5)
                    patrolTarget = RegionLocation.ProjectToFloor(region, anchor);
            }
            s_nemesisPatrol[phantom.Id] = (patrolTarget, nowMs + NemesisPatrolRepickMs);

            var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(400) };
            loco.PathTo(patrolTarget, ref opts);
        }

        /// <summary>
        /// True while a self-revive/"cheat death" proc's legitimate
        /// Invulnerable/Untargetable/Unaffectable window is active � the
        /// phantom should stand still and not attack during it, the same as
        /// a real player can't act during that animation. Deliberately-
        /// invincible phantoms (opt-in god mode, permanent Invulnerable
        /// only) are exempt � they're meant to keep fighting.
        /// </summary>
        private static bool IsPhantomInReviveFreeze(Agent phantom)
        {
            bool untargetableOrUnaffectable = phantom.Properties[PropertyEnum.Untargetable] || phantom.Properties[PropertyEnum.Unaffectable];
            if (untargetableOrUnaffectable) return true;
            return phantom.Properties[PropertyEnum.Invulnerable] && s_phantomDeliberatelyInvincible.Contains(phantom.Id) == false;
        }

        /// <summary>
        /// Per-phantom upkeep shared by friendly and enemy phantoms: the
        /// stuck-power watchdog, wall-stuck detection, and the leash that
        /// teleports strays back to the caller (which for enemies keeps the
        /// fight ON the caller).
        /// </summary>
        private void PhantomSharedMaintenance(Agent phantom, Vector3 callerPos, MHServerEmu.Core.System.Random.GRandom rng)
        {
            // Stuck-power watchdog: root-caused 2026-07-26 via code tracing
            // (not guessed) � a power with ActiveUntilCancelled == true (or
            // the hold/release SecondaryActivateOnReleasePrototype pattern)
            // only ever ends via an explicit release, which for a real
            // player is the NetMessageTryCancelPower packet
            // (PlayerConnection.cs's OnTryCancelPower) sent when the client
            // releases the button. The AI stack's only equivalent
            // (Behavior/StaticAI/UsePower.cs's End()) calls EndPower solely
            // on a behavior-tree Interrupted transition � which never fires
            // if the AI profile just keeps re-selecting the same UsePower
            // action because, from its perspective, the power is still
            // validly "Running". So for THIS class of power, a phantom can
            // NEVER end it on its own � waiting several seconds hoping it
            // resolves is pointless. Detect ActiveUntilCancelled up front and
            // end it almost immediately (1 tick) instead of waiting the full
            // reactive PhantomStuckPowerTicks window used for genuinely
            // transient stalls.
            PrototypeId activePowerRef = phantom.ActivePowerRef;
            if (activePowerRef != PrototypeId.Invalid)
            {
                bool cannotSelfEnd = activePowerRef.As<PowerPrototype>()?.ActiveUntilCancelled == true;
                int stuckThreshold = cannotSelfEnd ? 1 : PhantomStuckPowerTicks;

                if (s_phantomActivePowerTrack.TryGetValue(phantom.Id, out var powerTrack) && powerTrack.powerRef == activePowerRef)
                {
                    int ticks = powerTrack.ticks + 1;
                    if (ticks >= stuckThreshold)
                    {
                        try
                        {
                            Power stuckPower = phantom.PowerCollection?.GetPower(activePowerRef);
                            stuckPower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force);
                            PhantomLogger.Info($"[PhantomHero:Watchdog] force-ended stuck power {activePowerRef.GetName()} on {phantom.Id:X} after {ticks * 500}ms" +
                                (cannotSelfEnd ? " (ActiveUntilCancelled � can never self-end via AI)" : ""));

                            // For the ActiveUntilCancelled case specifically,
                            // don't wait on the separate multi-second
                            // stuck-invulnerable watchdog below � this power
                            // IS the confirmed source of the self-buff, so
                            // clear it in the same tick EndPower runs instead
                            // of leaving the phantom untargetable for several
                            // more seconds. Deliberately-invincible phantoms
                            // are still exempt (same rule as the watchdog
                            // below).
                            if (cannotSelfEnd && s_phantomDeliberatelyInvincible.Contains(phantom.Id) == false)
                            {
                                phantom.Properties[PropertyEnum.Invulnerable] = false;
                                phantom.Properties[PropertyEnum.Untargetable] = false;
                                phantom.Properties[PropertyEnum.Unaffectable] = false;
                                phantom.Properties[PropertyEnum.TutorialInvulnerable] = false;
                                s_phantomInvulnerableTrack.Remove(phantom.Id);
                            }
                        }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Watchdog] EndPower failed on {phantom.Id:X}: {ex.Message}"); }
                        s_phantomActivePowerTrack.Remove(phantom.Id);
                    }
                    else s_phantomActivePowerTrack[phantom.Id] = (activePowerRef, ticks);
                }
                else s_phantomActivePowerTrack[phantom.Id] = (activePowerRef, 1);
            }
            else s_phantomActivePowerTrack.Remove(phantom.Id);

            // Stuck-invulnerable watchdog: a self-revive/"cheat death" gear
            // proc applies a Condition expecting a real client to eventually
            // release it (animation completion, etc.) � a phantom never
            // does. Confirmed live: clearing Invulnerable alone wasn't
            // enough � the phantom stayed untargetable too, so the proc
            // evidently also sets Untargetable (and/or Unaffectable, the
            // broadest "can't be affected by anything" flag) alongside it.
            // Watch all three together. Deliberately-invincible phantoms
            // (opt-in god mode) only ever get Invulnerable set on purpose,
            // never the other two, so gating on "any of the three" plus the
            // exemption still leaves their permanent Invulnerable alone.
            //
            // TutorialInvulnerable added (2026-07-25): a real player-facing
            // player-untargetable bug was reported for a phantom that
            // wouldn't clear via this watchdog. Entity.IsUnaffectable is
            // actually `Unaffectable || TutorialInvulnerable` (Entity.cs) �
            // this watchdog was only checking the former, so a phantom that
            // somehow picked up TutorialInvulnerable (the flag the game's
            // native HUD-tutorial mission actions use) would stay
            // permanently untargetable/unaffectable with no way to recover.
            // Not confirmed to be exactly how it got set, but closing the
            // gap is safe regardless � a phantom should never stay stuck.
            bool invulnStuck = phantom.Properties[PropertyEnum.Invulnerable]
                || phantom.Properties[PropertyEnum.Untargetable]
                || phantom.Properties[PropertyEnum.Unaffectable]
                || phantom.Properties[PropertyEnum.TutorialInvulnerable];
            // Self-heal timed protection first. If the window has passed, lift it
            // here rather than trusting the scheduled event that was supposed to �
            // a missed lift used to be permanent (see MarkPhantomTimedInvincible).
            long nowMsProtect = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            bool timedProtectionActive = false;
            if (s_phantomTimedInvincibleUntilMs.TryGetValue(phantom.Id, out long protectUntilMs))
            {
                if (nowMsProtect >= protectUntilMs)
                {
                    phantom.Properties[PropertyEnum.Invulnerable] = false;
                    phantom.Properties[PropertyEnum.PowerLock] = false;
                    s_phantomTimedInvincibleUntilMs.Remove(phantom.Id);
                    PhantomLogger.Info($"[PhantomHero] timed spawn protection on {phantom.Id} expired � lifted by watchdog");
                }
                else
                {
                    timedProtectionActive = true;
                }
            }

            bool exemptDeliberate = (s_phantomDeliberatelyInvincible.Contains(phantom.Id) || timedProtectionActive)
                && phantom.Properties[PropertyEnum.Untargetable] == false
                && phantom.Properties[PropertyEnum.Unaffectable] == false
                && phantom.Properties[PropertyEnum.TutorialInvulnerable] == false;

            if (invulnStuck && exemptDeliberate == false)
            {
                long nowMsInvuln = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;

                // 2026-07-26 redesign � root-caused (Silver Surfer, then
                // Punisher's Cosmic item proc) to a Condition granting these
                // properties whose own completion event never fires for a
                // phantom. Rather than always waiting a fixed number of
                // ticks regardless of the source, use the Condition's OWN
                // declared Duration/TimeRemaining when it has one � clear it
                // the INSTANT it's already outstayed its real, intended
                // duration (so it never lasts LONGER than it's supposed to),
                // and fall back to the tick-counter only for the case where
                // the condition has no finite duration to check against at
                // all. On top of that, once a specific condition has been
                // force-cleared, refuse to let that same condition type grant
                // this phantom another invulnerability window again for a
                // cooldown period � otherwise a proc that keeps re-firing
                // reads as one continuous "can't be hurt" state even though
                // each individual instance is technically brief.
                bool anyOverstayedOrNoDuration = false;
                var toRemoveNow = new List<ulong>();
                var clearedConditionRefs = new List<PrototypeId>();

                if (phantom.ConditionCollection != null)
                {
                    foreach (Condition condition in phantom.ConditionCollection)
                    {
                        if (condition.Properties[PropertyEnum.Invulnerable] == false
                            && condition.Properties[PropertyEnum.Untargetable] == false
                            && condition.Properties[PropertyEnum.Unaffectable] == false
                            && condition.Properties[PropertyEnum.TutorialInvulnerable] == false)
                        {
                            continue;
                        }

                        PrototypeId condKeyRef = condition.CreatorPowerPrototypeRef != PrototypeId.Invalid
                            ? condition.CreatorPowerPrototypeRef
                            : condition.ConditionPrototypeRef;
                        var cooldownKey = (phantom.Id, condKeyRef);

                        bool inNoRepeatCooldown = s_phantomInvulnConditionCooldownUntilMs.TryGetValue(cooldownKey, out long cooldownUntil)
                            && nowMsInvuln < cooldownUntil;
                        bool overstayedOwnDuration = condition.IsFinite && condition.TimeRemaining <= TimeSpan.Zero;

                        if (inNoRepeatCooldown || overstayedOwnDuration)
                        {
                            toRemoveNow.Add(condition.Id);
                            clearedConditionRefs.Add(condKeyRef);

                            // Back-to-back guard: this exact condition can't
                            // grant another invulnerability window for at
                            // least as long as this one was legitimately
                            // supposed to last (its own Duration if finite),
                            // or a flat 10s floor for a permanent/no-duration
                            // condition � long enough that a proc firing
                            // again immediately doesn't just chain into a
                            // second window back to back.
                            long cooldownSpanMs = condition.IsFinite && condition.Duration > TimeSpan.Zero
                                ? (long)condition.Duration.TotalMilliseconds
                                : 10_000;
                            s_phantomInvulnConditionCooldownUntilMs[cooldownKey] = nowMsInvuln + cooldownSpanMs;
                        }
                        else if (condition.IsFinite == false)
                        {
                            // No natural duration to check against at all �
                            // this is the "permanent until explicitly ended"
                            // case (e.g. a toggle). Fall back to the
                            // reactive tick-counter safety net.
                            anyOverstayedOrNoDuration = true;
                        }
                    }
                }

                int invulnTicks = s_phantomInvulnerableTrack.TryGetValue(phantom.Id, out int prevTicks) ? prevTicks + 1 : 1;
                bool tickCounterExpired = anyOverstayedOrNoDuration && invulnTicks >= PhantomStuckInvulnerableTicks;

                if (toRemoveNow.Count > 0 || tickCounterExpired)
                {
                    try
                    {
                        if (phantom.ConditionCollection != null)
                        {
                            // Tick-counter fallback path needs its own scan
                            // since toRemoveNow only collected the
                            // already-overstayed/cooldown-blocked ones above.
                            if (tickCounterExpired)
                            {
                                foreach (Condition condition in phantom.ConditionCollection)
                                {
                                    if (toRemoveNow.Contains(condition.Id)) continue;
                                    if (condition.Properties[PropertyEnum.Invulnerable]
                                        || condition.Properties[PropertyEnum.Untargetable]
                                        || condition.Properties[PropertyEnum.Unaffectable]
                                        || condition.Properties[PropertyEnum.TutorialInvulnerable])
                                    {
                                        toRemoveNow.Add(condition.Id);
                                        clearedConditionRefs.Add(condition.CreatorPowerPrototypeRef != PrototypeId.Invalid
                                            ? condition.CreatorPowerPrototypeRef
                                            : condition.ConditionPrototypeRef);
                                    }
                                }
                            }
                            foreach (ulong conditionId in toRemoveNow)
                                phantom.ConditionCollection.RemoveCondition(conditionId);
                        }

                        phantom.Properties[PropertyEnum.Invulnerable] = false;
                        phantom.Properties[PropertyEnum.Untargetable] = false;
                        phantom.Properties[PropertyEnum.Unaffectable] = false;
                        phantom.Properties[PropertyEnum.TutorialInvulnerable] = false;
                        if (phantom.ActivePowerRef != PrototypeId.Invalid)
                        {
                            Power stuckInvulnPower = phantom.PowerCollection?.GetPower(phantom.ActivePowerRef);
                            stuckInvulnPower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force);
                        }

                        string sourcesStr = clearedConditionRefs.Count > 0
                            ? $" (source condition(s): {string.Join(", ", clearedConditionRefs.Select(r => r.GetName()))})"
                            : "";
                        PhantomLogger.Info($"[PhantomHero:Watchdog] force-cleared Invulnerable/Untargetable/Unaffectable/TutorialInvulnerable on {phantom.Id:X}{sourcesStr}");
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Watchdog] Invulnerable/Untargetable clear failed on {phantom.Id:X}: {ex.Message}"); }
                    s_phantomInvulnerableTrack.Remove(phantom.Id);
                }
                else s_phantomInvulnerableTrack[phantom.Id] = invulnTicks;
            }
            else s_phantomInvulnerableTrack.Remove(phantom.Id);

            // Summon stat leak: enemy/nemesis phantoms carry deliberately
            // inflated combat-scaling properties (DamageMult/DamagePctBonus/
            // DamageRating/HealthMaxMult � see ApplyPhantomDamageScaling and
            // the nemesis-rank HP mult) as flat base Properties. A real
            // player's own version of these is always level/gear-bounded;
            // some pet-summon powers (CopyOwnerProperties=true) copy the
            // owner's Properties straight onto the summoned entity, so a
            // phantom's inflation leaks onto anything it summons � confirmed
            // live: a nemesis's summoned pet hit way too hard and had way
            // too much HP. Strip it back off every tick (cheap, idempotent,
            // and only ever touches phantom-owned summons � a real player's
            // pets never pass through PhantomSharedMaintenance).
            foreach (WorldEntity summoned in new SummonedEntityIterator(phantom))
            {
                // Explicit safe values, not RemoveProperty � a generic
                // summoned pet prototype may have no curve of its own for
                // these, and removing a multiplicative property risks it
                // reading back as 0 (zero damage / zero max HP) rather than
                // a sane baseline.
                if (summoned.Properties[PropertyEnum.DamageMult] != 1.0f)
                    summoned.Properties[PropertyEnum.DamageMult] = 1.0f;
                if (summoned.Properties[PropertyEnum.DamagePctBonus] != 0f)
                    summoned.Properties[PropertyEnum.DamagePctBonus] = 0f;
                if (summoned.Properties[PropertyEnum.DamageRating] != 0f)
                    summoned.Properties[PropertyEnum.DamageRating] = 0f;
                if (summoned.Properties[PropertyEnum.HealthMaxMult] != 1.0f)
                    summoned.Properties[PropertyEnum.HealthMaxMult] = 1.0f;

                // The actual root cause of "still absurd HP/damage" even with
                // the four properties above reset: the phantom's own Boss/
                // MiniBoss Rank tag (set to drive the boss-bar UI, see the
                // rank-tag block above) ALSO copies onto the summon via the
                // same owner-property-copy path. WorldEntity.cs's Rank
                // property-change handler runs a real Mod bundle
                // (ModChangeModEffects) the INSTANT the copied Rank is first
                // read as already-present at creation � by the time this
                // tick-based scrub runs, that bundle's HP/damage bonuses are
                // already attached as their own Condition-backed mods,
                // completely independent of DamageMult/HealthMaxMult (same
                // failure shape previously found on the phantoms themselves,
                // see ApplyPhantomDamageScaling's clamp comment). Confirmed
                // fix, not guessed: WorldEntity.cs's own Rank case (~line
                // 3812) unwinds the old rank's mods via
                // ClearAttachedPropertiesOfType + ModChangeModEffects
                // automatically whenever Rank actually CHANGES on an
                // IsSimulated entity � so simply setting it back to the
                // summon's own natural default triggers the same real
                // unwind the engine already uses for legitimate rank swaps.
                PrototypeId naturalRank = summoned.WorldEntityPrototype?.Rank?.DataRef ?? PrototypeId.Invalid;
                if (summoned.Properties[PropertyEnum.Rank] != naturalRank)
                    summoned.Properties[PropertyEnum.Rank] = naturalRank;
            }

            // Stuck detection: if the phantom's position barely moved
            // this tick despite the Locomotor being set to move, count
            // it. After N consecutive stuck ticks assume they're
            // wall-clipped or pathed out of bounds and force-leash.
            Vector3 curPos = phantom.RegionLocation.Position;
            bool forceLeash = false;
            if (s_phantomStuckTrack.TryGetValue(phantom.Id, out var stuckState))
            {
                float movedSq = Vector3.DistanceSquared2D(curPos, stuckState.lastPos);
                bool wantsToMove = phantom.Locomotor != null && phantom.Locomotor.IsMoving;
                int newStuck = (wantsToMove && movedSq < PhantomStuckMoveEpsilonSq) ? stuckState.stuckTicks + 1 : 0;
                if (newStuck >= PhantomStuckTickThreshold) forceLeash = true;
                s_phantomStuckTrack[phantom.Id] = (curPos, forceLeash ? 0 : newStuck);
            }
            else s_phantomStuckTrack[phantom.Id] = (curPos, 0);

            // Leash: teleport back if stranded far or wall-stuck. Enemy
            // phantoms use a tighter leash so they aggressively re-close on
            // the caller after death/revive or a run-away attempt.
            //
            // Ambush phantoms (Rogue Encounter / nemeses) are exempt from the
            // caller-distance leash entirely � that's the whole point of
            // patrol, spawning away from the player and roaming their own
            // territory instead of being yanked back onto them. They still
            // get rescued if genuinely wall-stuck, but recover to their own
            // spawn anchor instead of the player's position.
            if (s_enemyPhantomAmbush.Contains(phantom.Id))
            {
                if (forceLeash)
                {
                    Region ar = phantom.Region;
                    Vector3 anchor = s_nemesisSpawnAnchor.TryGetValue(phantom.Id, out var storedAnchor) ? storedAnchor : curPos;
                    Vector3 rescuePos = ChoosePhantomLeashPos(ar, anchor, rng, phantom.Bounds.Radius);
                    try
                    {
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(rescuePos, null);
                        s_phantomStuckTrack[phantom.Id] = (rescuePos, 0);
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Leash] ambush rescue teleport failed on {phantom.Id:X}: {ex.Message}"); }
                }
                return;
            }

            bool isEnemyPhantom = phantom.IsPhantomHero
                && phantom.GetOwnerOfType<Player>()?.PhantomCreatorId == 0;

            // Team Deathmatch: nobody leashes to the player. Every combatant is
            // fighting two rival duos across the whole arena, so yanking them back
            // to the human would collapse a three-way match into a permanent scrum
            // around one person. Only the explicit forceLeash (stuck rescue) still
            // applies.
            if (IsDeathmatchTeamCombatant(phantom.Id) && forceLeash == false)
                return;

            float leashMaxDistSq = isEnemyPhantom ? EnemyPhantomFollowMaxDistSq : PhantomFollowMaxDistSq;
            float distSq = Vector3.DistanceSquared2D(curPos, callerPos);
            if (distSq > leashMaxDistSq || forceLeash)
            {
                Region r = phantom.Region;
                Vector3 leashPos = ChoosePhantomLeashPos(r, callerPos, rng, phantom.Bounds.Radius);
                try
                {
                    phantom.Locomotor?.Stop();
                    phantom.ChangeRegionPosition(leashPos, null);
                    s_phantomStuckTrack[phantom.Id] = (leashPos, 0);
                }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Leash] teleport failed on {phantom.Id:X}: {ex.Message}"); }
            }
        }

        /// <summary>
        /// True when this entity is registered as a combatant in the caller's
        /// active Team Deathmatch. Used to switch the phantom AI out of its
        /// PvE-shaped "hunt the player" behaviour and into free-for-all-between-
        /// teams behaviour for the duration of a match.
        /// </summary>
        /// <summary>
        /// How far a Team Deathmatch combatant can see. Effectively region-wide.
        ///
        /// This was briefly capped short (1800u) to make teams find each other by
        /// roaming instead of beelining from spawn � but on the arena pool's larger
        /// maps that produced the opposite problem: combatants standing still or
        /// wandering to random navmesh points for long stretches without ever
        /// getting close enough to see anyone (reported live 2026-08-08 � teams
        /// "stuck just standing still" and "getting lost trying to navigate the
        /// region"). Reverted to region-wide sight per that request: everyone can
        /// always see the other team and commits to closing the distance instead
        /// of roaming blind. Spawn anchors are still dispersed to opposite ends of
        /// the map (ChooseDeathmatchTeamAnchorsDefault), so this does not reproduce
        /// the original spawn-pileup problem � it only removes the blind-roam phase.
        /// </summary>
        private const float DeathmatchSightRange = 1_000_000f;

        private bool IsDeathmatchTeamCombatant(ulong entityId)
        {
            Player host = PhantomHost ?? GetOwnerOfType<Player>();
            return host != null && host.IsDeathmatchTeamCombatant(entityId);
        }

        // One-time-per-phantom diagnostic set. Removed once attack is verified.
        private static readonly HashSet<ulong> s_phantomAttackLogged = new();
        // One-time-per-(phantom,target) diagnostic set for the Attack log so a
        // new boss gets its state dumped even after this phantom has already
        // logged an attack on a mob.
        private static readonly HashSet<ulong> s_phantomAttackTargetLogged = new();

        // Revive-priority range � search a bit wider than combat range so
        // phantoms notice downed players from across a room.
        private const float PhantomReviveSearchRange = 4000f;
        private const float PhantomReviveSearchRangeSq = PhantomReviveSearchRange * PhantomReviveSearchRange;

        // Real usable range (squared) for a phantom's resurrect-other power.
        // Confirmed via live server logs (2026-07-15): a hardcoded 500u
        // "cast range" guess let phantoms stop and attempt the cast well
        // outside the power's actual range, so ActivatePower rejected almost
        // every attempt with OutOfPosition (203 rejections logged in one
        // session, only 3 successful revives). Ask the power itself instead
        // of guessing � same fix already applied to combat-power range
        // gating. Falls back to true melee reach if the power reports no
        // positive range at all (same convention as the combat-power gate).
        private static float GetReviveCastRangeSq(Agent phantom, PrototypeId resurrectPowerRef)
        {
            if (resurrectPowerRef != PrototypeId.Invalid)
            {
                Power resurrectPower = phantom.GetPower(resurrectPowerRef);
                float r = resurrectPower?.GetRange() ?? 0f;
                if (r > 0f)
                {
                    float withMargin = r + 50f;
                    return withMargin * withMargin;
                }
            }
            return PhantomMeleeRangeSq;
        }

        private void UpdatePhantomHunt(Agent phantom, MHServerEmu.Core.System.Random.GRandom rng, bool enemyMode = false)
        {
            Region region = phantom.Region;
            if (region == null || phantom.PowerCollection == null) return;

            // Mid-cast: stand still, like a real player. Without this the
            // tick kept re-issuing FollowEntity every 500ms while a power
            // was executing, so phantoms slid across the ground through
            // their cast animations. Any new attack would return
            // PowerInProgress anyway, and the stuck-power watchdog (in
            // OnPhantomTick, which runs before this) still force-ends
            // channels that never finish � so skipping the whole hunt for
            // the duration of a cast is safe.
            if (phantom.IsExecutingPower)
            {
                var castLoco = phantom.Locomotor;
                if (castLoco != null && castLoco.IsMoving)
                    castLoco.Stop();
                return;
            }

            Vector3 phantomPos = phantom.RegionLocation.Position;
            Vector3 callerPos = RegionLocation.Position;
            bool isAmbushPhantom = enemyMode && s_enemyPhantomAmbush.Contains(phantom.Id);

            // Loot Goblin Hunt (Player.FeatureLab.cs) � a flagged phantom
            // never fights, it just runs from the nearest real avatar,
            // reusing the same kite-away Locomotor primitive ranged phantoms
            // already use for standoff, just with a much larger standoff so
            // it reads as fleeing rather than kiting.
            if (s_fleeingPhantoms.Contains(phantom.Id))
            {
                float distSq = Vector3.DistanceSquared2D(phantomPos, callerPos);
                TryPhantomKite(phantom, region, callerPos, MathF.Sqrt(distSq), LootGoblinFleeStandoff, Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond, rng);
                return;
            }

            // Priority 0: self-heal at low HP, before anything else �
            // friendly and enemy phantoms alike. This is the same medkit
            // power real players use (bound to the M / DedicatedHealSlot
            // hotkey): GlobalsPrototype.AvatarHealPower is granted to every
            // avatar (real or phantom) through the ordinary InitializePowers
            // pipeline at OnEnteredWorld, so no special grant is needed here
            // � it's just been invisible to the AI because nothing ever
            // looked for it. Its own cooldown (whatever the real power data
            // defines) gates reuse via the normal IsOnCooldown() check, same
            // as every other power this AI fires. Directly targets the
            // "half the squad rushes in to revive and dies to the next AoE"
            // problem � a critically hurt phantom now tries to save itself
            // before it dies, instead of only ever being reactively revived.
            if (TryPhantomSelfHeal(phantom, enemyMode))
                return;

            // Get out of damaging ground effects before doing anything else
            // positional. Ranked just below self-heal (a phantom about to die
            // should still drink first) but above hunting/following, because
            // continuing to walk a path that keeps it parked in fire defeats
            // every other survivability behavior. Applies to enemy phantoms
            // too � a nemesis standing in its own ally's hazard looks broken
            // in exactly the same way.
            if (TryPhantomAvoidHazard(phantom, region,
                    Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond))
                return;

            // Enemy phantoms don't do triage � straight to the hunt.
            if (enemyMode)
                goto Hunt;

            // Support/buff pass � friendly phantoms only, and deliberately
            // placed AFTER self-heal but BEFORE the revive/hunt logic so it
            // can't preempt either emergency response. Its own long cooldown
            // (~12-16s) keeps it from displacing meaningful combat time.
            //
            // Resolves its own ally list internally; it contributes nothing
            // to the hunt's candidate list. See the header comment on
            // TryPhantomSupport for why that separation is mandatory.
            if (TryPhantomSupport(phantom, region, Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond, rng))
                return;

            // Priority 1: revive any downed real player OR friendly phantom
            // (avatar-type or team-up) within revive range. Real avatars and
            // phantoms are both still IsInWorld while downed (dead-but-
            // revivable); filtered below to Agent entities that are IsDead
            // and either belong to a real player or the same phantom squad.
            // Nearest wins.
            Agent downed = null;
            float downedDistSq = float.MaxValue;
            var reviveSphere = new Sphere(phantomPos, PhantomReviveSearchRange);
            var reviveCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            // Direct check on the caller first � this covers the case where the
            // human died far from the phantom (out of the 4000u sphere) or
            // during a scripted death animation where the AOI doesn't return
            // them from IterateEntitiesInVolume. The caller is the phantom's
            // owner, so we always know exactly who to look for.
            if (this.IsDead && this.IsInWorld && this.Region == region)
            {
                downed = this;
                downedDistSq = Vector3.DistanceSquared(this.RegionLocation.Position, phantomPos);
            }
            else
            {
                // Direct roster scan � this phantom's own squad, checked by
                // ID with NO distance ceiling, same as the caller check
                // above. Confirmed live (2026-07-15 session): a squad spread
                // out fighting separate targets can end up more than the
                // 4000u sweep radius apart, and a downed teammate outside
                // that radius was completely invisible to every other
                // phantom � "none of them would revive the last one" � until
                // the player manually leashed everyone back close enough.
                // The human caller never had this problem because it's
                // always looked up directly instead of via a bounded sweep;
                // squadmates now get the same treatment.
                Player rosterHost = this.PhantomHost;
                if (rosterHost != null)
                {
                    var rosterIds = rosterHost.PhantomAvatarIds;
                    for (int ri = 0; ri < rosterIds.Count; ri++)
                    {
                        ulong avId = rosterIds[ri];
                        if (avId == phantom.Id) continue;
                        Agent candidate = Game.EntityManager.GetEntity<Agent>(avId);
                        if (candidate == null || candidate.IsDead == false || candidate.IsInWorld == false || candidate.Region != region) continue;
                        // Team Deathmatch combatants are replaced on death, not
                        // revived. Reviving one would resurrect a body that has
                        // already been counted and replaced, inflating the roster
                        // and dragging the revived phantom into the player's party.
                        if (IsDeathmatchTeamCombatant(candidate.Id)) continue;
                        // Only real avatars can be resurrected. PhantomAvatarIds
                        // also holds TEAM-UP agents (RegisterPhantom stores both),
                        // and team-ups have no downed/revive flow at all - a dead
                        // one just despawns. Without this the squad would walk to
                        // a dead team-up (or one of its temporary away-team
                        // summons) and play the resurrect cast on nothing, which
                        // is exactly the "random revive animation on nothing"
                        // players reported. Confirmed live 2026-08-03 via
                        // PhantomHero:ReviveClaim entries naming
                        // NewCoulsonAwayShotgunAgent / NewCoulsonAwayMinigunAgent.
                        if (candidate is not Avatar) continue;

                        float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, phantomPos);
                        if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                    }
                }

                // Spatial sweep for OTHER real players (not this phantom's own
                // roster, e.g. a party member) � kept bounded to
                // PhantomReviveSearchRange since these are strangers to the
                // squad, not something we track directly by ID.
                foreach (WorldEntity we in region.IterateEntitiesInVolume(reviveSphere, reviveCtx))
                {
                    if (we is not Agent candidate) continue;
                    if (candidate.Id == phantom.Id) continue;
                    if (candidate.IsDead == false) continue;
                    if (candidate is not Avatar) continue;   // see the roster scan above

                    Player candOwner = candidate.GetOwnerOfType<Player>();
                    if (candOwner == null || candOwner.PlayerConnection == null) continue;

                    float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, phantomPos);
                    if (d > PhantomReviveSearchRangeSq) continue;
                    if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                }
            }
            // Claim the downed target so the rest of the squad doesn't also
            // converge on the exact same revive � every phantom runs this
            // same search independently every tick with no coordination, so
            // without a claim, 3 nearby phantoms all pick the same nearest
            // downed ally and all 3 abandon the fight to pile on one revive
            // (confirmed live 2026-07-19). Whoever gets here first on a
            // given target keeps it; everyone else treats it as "nothing to
            // revive" and falls through to normal combat instead. The claim
            // expires on its own after PhantomReviveClaimTimeoutMs (covers
            // the claimant dying, getting stuck, or the target being
            // revived by something else entirely) so a downed ally is never
            // permanently orphaned by a stale claim.
            if (downed != null)
            {
                long nowMsRevive = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (s_phantomReviveClaim.TryGetValue(downed.Id, out var claim)
                    && claim.claimantId != phantom.Id
                    && nowMsRevive - claim.claimedAtMs < PhantomReviveClaimTimeoutMs
                    && nowMsRevive - claim.firstClaimedAtMs < PhantomReviveClaimMaxHoldMs)
                {
                    // DIAGNOSTIC (2026-07-21) � user reported multiple phantoms
                    // reviving the same downed target simultaneously. Proves
                    // whether the claim system is actually rejecting the
                    // duplicate attempt (if this line fires, it is).
                    PhantomLogger.Info($"[PhantomHero:ReviveClaim] {phantom} backed off downed {downed} � already claimed by {claim.claimantId:X}");
                    downed = null;

                    // Gap fix (2026-07-21): rejection alone only stops this phantom
                    // from casting the revive THIS tick � if it was already
                    // mid-path toward the downed target from an earlier tick
                    // (e.g. it held the claim briefly before losing it, or found
                    // the same target before another phantom's claim registered),
                    // that FollowEntity command stays active in the Locomotor and
                    // it keeps visibly walking toward/clustering on the target
                    // even though it will never actually cast. Explicitly stop
                    // here so Hunt (below) picks its own real destination instead
                    // of coasting on stale movement. The team-up revive path
                    // already does the equivalent via RestoreTeamUpAssistedEntity.
                    phantom.Locomotor?.Stop();
                }
                else
                {
                    // Bug fixed (2026-07-20 audit): the claimant refreshed
                    // BOTH timestamps every tick it held the claim, so the
                    // 6s "timeout" was actually only an INACTIVITY timeout �
                    // a phantom that kept trying (even if it was the
                    // farthest one, or its revives kept getting rejected)
                    // could hold the claim indefinitely, permanently
                    // locking out closer phantoms. firstClaimedAtMs now only
                    // gets set once, when a NEW claimant takes over, so a
                    // hard cap (PhantomReviveClaimMaxHoldMs) applies
                    // regardless of how active the claimant stays.
                    bool isNewClaimant = claim.claimantId != phantom.Id;
                    long firstClaimedAtMs = isNewClaimant ? nowMsRevive : claim.firstClaimedAtMs;
                    s_phantomReviveClaim[downed.Id] = (phantom.Id, nowMsRevive, firstClaimedAtMs);

                    // A fresh claimant gets a clean slate � the previous
                    // holder's failure count says nothing about whether THIS
                    // phantom (likely standing somewhere different) will
                    // also get rejected.
                    if (isNewClaimant)
                        s_phantomReviveOutOfPositionCount.Remove(downed.Id);
                }
            }

            // Team-ups don't have AvatarPrototype/ResurrectOtherAvatar (they're
            // Agent, not Avatar) and already got their own revive-of-others
            // priority via TryTeamUpReviveDowned before UpdatePhantomHunt was
            // even called (see the tick loop). So if a team-up somehow still
            // has a downed target here, just skip this avatar-specific cast
            // path and fall through to Hunt rather than trying to cast a
            // power that doesn't exist on this entity type.
            if (downed != null && phantom is Avatar avatarPhantom)
            {
                PrototypeId reviveCastPowerRef = avatarPhantom.AvatarPrototype?.ResurrectOtherEntityPower ?? PrototypeId.Invalid;
                // Walk to them if we're not in cast range yet.
                if (downedDistSq > GetReviveCastRangeSq(phantom, reviveCastPowerRef))
                {
                    var reviveLoco = phantom.Locomotor;
                    if (reviveLoco != null)
                    {
                        var reviveOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                        bool reviveOk = reviveLoco.FollowEntity(downed.Id, 50f, 50f, ref reviveOpts, false);

                        // The roster scan above has no distance ceiling, so a
                        // downed teammate can now be found on the far side of
                        // a gap the navmesh can't actually path across. Same
                        // rescue the Hunt branch already uses: force-leash
                        // next to the target instead of standing there
                        // failing to path forever.
                        if (reviveOk == false
                            && (reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.Failed
                             || reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.FailedNaviMesh
                             || reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.FailedNoPathFound))
                        {
                            Vector3 targetPos = downed.RegionLocation.Position;
                                    Vector3 rescuePos = ChoosePhantomLeashPos(region, targetPos, rng, phantom.Bounds.Radius);
                            try
                            {
                                reviveLoco.Stop();
                                phantom.ChangeRegionPosition(rescuePos, null);
                                s_phantomStuckTrack[phantom.Id] = (rescuePos, 0);
                                PhantomLogger.Info($"[PhantomHero:Revive] {phantom} path to downed {downed} failed, force-leashed to {rescuePos.ToStringNames()}");
                            }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Revive] rescue leash threw: {ex.Message}"); }
                        }
                    }
                }
                else
                {
                    // In cast range � fire the built-in resurrect-other power.
                    // bypassCooldown: true so this phantom can keep chain-
                    // reviving the rest of a wiped squad instead of sitting
                    // on cooldown after its first successful revive.
                    try
                    {
                        var reviveResult = avatarPhantom.ResurrectOtherAvatar(downed, bypassCooldown: true);
                        if (reviveResult != null && reviveResult != PowerUseResult.Success)
                        {
                            PhantomLogger.Info($"[PhantomHero:Revive] {phantom} -> {downed} rejected: {reviveResult}");

                            // BREAKDOWN DIAGNOSTIC (2026-07-21) � user directly
                            // observed the claimed reviver never actually
                            // finishing the revive, which orphans the claim
                            // (only clears on Success) and looks like
                            // "everyone's stuck trying." RestrictiveCondition is
                            // a generic bucket over ~8 different caster-side
                            // conditions (Agent.CanTriggerPower) � log every one
                            // of them explicitly instead of guessing which.
                            if (reviveResult == PowerUseResult.RestrictiveCondition)
                            {
                                PhantomLogger.Info($"[PhantomHero:ReviveBlocked] {phantom} knockback={avatarPhantom.IsInKnockback} " +
                                    $"knockdown={avatarPhantom.IsInKnockdown} knockup={avatarPhantom.IsInKnockup} stunned={avatarPhantom.IsStunned} " +
                                    $"mesmerized={avatarPhantom.IsMesmerized} npcAmbientLock={avatarPhantom.NPCAmbientLock} " +
                                    $"powerLock={avatarPhantom.IsInPowerLock} aiControlPowerLock={avatarPhantom.HasAIControlPowerLock} " +
                                    $"tutorialPowerLock={avatarPhantom.IsInTutorialPowerLock}");

                                // Reverted (2026-07-21): releasing the claim on every
                                // RestrictiveCondition failure was wrong � live data (the
                                // breakdown above) showed it's almost always stunned=True,
                                // i.e. the reviver got hit by ongoing enemy fire, a normal
                                // and expected mid-combat interruption, not a stuck/broken
                                // claimant. Releasing on that let a DIFFERENT phantom
                                // immediately grab the claim, which likely gets stunned by
                                // the same ongoing fight moments later too � the claim
                                // churned between phantoms every ~0.5-1s, which is exactly
                                // what looked like "everyone's trying to revive the same
                                // target." Explicit requirement: exactly ONE phantom should
                                // ever be "the reviver" for a given target at a time, stable,
                                // while everyone else keeps fighting. So: do NOT release
                                // here � this same claimant keeps re-claiming every tick
                                // (it's still the closest) and will succeed once the stun
                                // passes. The existing 6s-inactivity / 20s-hard-cap timeout
                                // remains the only escape hatch for a claimant that's truly
                                // stuck (e.g. dead itself).
                            }
                            else if (reviveResult == PowerUseResult.OutOfPosition)
                            {
                                // GetReviveCastRangeSq's gate uses a squared
                                // 3D distance check, but this rejection means
                                // the power itself disagrees every time
                                // despite that check passing � almost always
                                // a navmesh obstruction or line-of-sight
                                // blocker a flat distance comparison can't
                                // see (same class of issue as the path-failed
                                // rescue above, just discovered by the power
                                // instead of the pathfinder). Unlike
                                // RestrictiveCondition, this has no natural
                                // reason to resolve itself by waiting.
                                int failCount = s_phantomReviveOutOfPositionCount.TryGetValue(downed.Id, out int prevFail) ? prevFail + 1 : 1;
                                s_phantomReviveOutOfPositionCount[downed.Id] = failCount;

                                if (failCount == PhantomReviveRepositionAfterFailures)
                                {
                                    try
                                    {
                                        Vector3 targetPos = downed.RegionLocation.Position;
                                        Vector3 rescuePos = ChoosePhantomLeashPos(region, targetPos, rng, phantom.Bounds.Radius);
                                        phantom.Locomotor?.Stop();
                                        phantom.ChangeRegionPosition(rescuePos, null);
                                        PhantomLogger.Info($"[PhantomHero:Revive] {phantom} stuck OutOfPosition reviving {downed} ({failCount}x) � force-repositioned to {rescuePos.ToStringNames()}");
                                    }
                                    catch (Exception rescueEx) { PhantomLogger.Warn($"[PhantomHero:Revive] reposition-on-stuck threw: {rescueEx.Message}"); }
                                }
                                else if (failCount >= PhantomReviveGiveUpAfterFailures)
                                {
                                    // Repositioning didn't fix it either.
                                    // Releasing the claim here (rather than
                                    // waiting on PhantomReviveClaimMaxHoldMs,
                                    // which never actually applies to the
                                    // CURRENT holder � see the field comment
                                    // above) lets this phantom or a
                                    // squadmate re-claim fresh next tick, and
                                    // lets THIS phantom fall through to Hunt
                                    // in the meantime instead of idling
                                    // forever on an unreachable target.
                                    s_phantomReviveClaim.Remove(downed.Id);
                                    s_phantomReviveOutOfPositionCount.Remove(downed.Id);
                                    PhantomLogger.Info($"[PhantomHero:Revive] {phantom} giving up reviving {downed} after {failCount} OutOfPosition rejections � claim released");
                                }
                            }
                        }
                        else
                        {
                            s_phantomReviveOutOfPositionCount.Remove(downed.Id);
                        }
                        // Race fix (lordunborn's fork independently hit the same
                        // bug): releasing the claim the instant Success comes
                        // back is premature � downed.IsDead doesn't flip to
                        // false until the NEXT tick, so a second phantom's
                        // roster scan this same tick (or the next one, before
                        // the flip lands) could still see "still downed, claim
                        // is free" and cast its own redundant revive on the
                        // same target. Deliberately do NOT remove the claim
                        // here � once IsDead actually flips false, no phantom's
                        // roster scan will consider this entity "downed"
                        // anymore regardless of claim state, so the now-stale
                        // claim is harmless and simply expires on its own via
                        // the existing 6s-inactivity timeout (this claimant
                        // stops refreshing it the moment the target's no
                        // longer found as downed).
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Revive] {phantom.Id:X} -> {downed.Id:X} failed: {ex.Message}"); }
                }
                return; // don't hunt while triaging a downed teammate
            }

            Hunt:
            // Widest sweep so we start advancing on enemies before they're in
            // attack range. IterateEntitiesInVolume walks the region spatial
            // partition, cheap.
            // Team Deathmatch: unlimited sight. Teams start deliberately far
            // apart (anchors are spread across the whole region), so the normal
            // 3500u sweep would leave every team standing still, unable to see
            // anyone to walk toward. Hunting the entire region is what makes them
            // actively seek each other out. This branch is inert outside a match �
            // IsDeathmatchTeamCombatant requires an active TDM roster � so no
            // other mode's search behaviour changes.
            float sweepRange = IsDeathmatchTeamCombatant(phantom.Id)
                ? DeathmatchSightRange
                : PhantomSearchRange;

            var sweepSphere = new Sphere(phantomPos, sweepRange);
            var ctx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            // Build a full sorted candidate list of hostile Agents instead of just
            // "the nearest one." Some encounters (dramatic-entrance bosses, mission
            // untargetable phases, out-of-line-of-sight bosses on elevated
            // platforms) leave the nearest hostile in a state where
            // Power.IsValidTarget silently rejects � and if that's the only entity
            // we track, the phantom locks onto it, ActivatePower burns the
            // cooldown returning BadTarget, and the phantom stands still for the
            // whole fight. With a list we fall through to the next-nearest until
            // one accepts the attack.
            // (entity, distSq, threat) � threat drives ordering, distSq still
            // drives every range gate downstream.
            var candidates = new List<(WorldEntity we, float distSq, float threat)>();
            List<(WorldEntity we, float distSq, string reason)> diagRejected = null;
            ulong squadHostId = PhantomHost?.Id ?? 0;
            ulong squadFocusId = GetPhantomSquadFocusTarget(squadHostId, enemyMode,
                Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond);
            ulong committedTargetId = s_phantomCommittedTargetId.TryGetValue(phantom.Id, out ulong committedId) ? committedId : 0;
            bool diagWant = ShouldEmitPhantomDiag(phantom.Id);
            long nowMsSweep = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            PhantomPersonality personality = GetPhantomPersonality(phantom.Id, phantom.PrototypeDataRef, rng);
            GetPersonalityThreatMult(personality, out float persDistanceMult, out float persPeelMult, out float persFinishMult, out float persFocusMult);
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id) continue;
                // Friendly phantoms never target their caller in THIS list �
                // it drives movement/attack-target priority (candidates[0]
                // becomes "nearest" for both the Locomotor and the attack
                // try-loop), and the caller is almost always the closest
                // thing to a friendly phantom. Adding them here made
                // candidates[0] the caller instead of the nearest hostile,
                // which broke friendly-phantom combat AI outright: they
                // stopped chasing distant enemies (Locomotor followed the
                // already-adjacent caller instead) and lost their smooth
                // idle-follow-slot behavior (the "candidates.Count == 0"
                // branch below never triggered anymore since the caller was
                // always a valid candidate) � confirmed via a live user
                // report (2026-07-19) of friendly bots going sluggish and no
                // longer approaching enemies in combat. Buffing the caller
                // would need a genuinely separate, lower-priority mechanism
                // that can't preempt this list; reverted rather than risk
                // shipping the regression again.
                if (enemyMode == false && we.Id == Id) continue;
                if (we.IsDead || we.IsInWorld == false) continue;

                if (enemyMode)
                {
                    // Enemy phantoms only hunt player-side avatars � the
                    // caller (this human), other real players in the region,
                    // or the caller's friendly phantoms. The hostile-alliance
                    // trick that lets players damage them also makes mob
                    // factions look like valid targets in the sweep, and
                    // whichever mob is closest steals every hunt tick � so
                    // enemy phantoms end up wandering to a Hydra grunt while
                    // the player they were meant to hunt stands 40m behind
                    // them. Restrict to Avatars only and the fantasy holds.
                    if (we is not Avatar avCand) continue;

                    // Team Deathmatch: three mutually hostile duos, so "who is a
                    // valid target" is answered by alliance alone. Without this the
                    // filter below skips every other enemy phantom
                    // (PhantomCreatorId == 0), which is exactly why rival duos
                    // ignored each other and everyone piled onto the human.
                    if (IsDeathmatchTeamCombatant(phantom.Id))
                    {
                        if (phantom.IsHostileTo(avCand) == false) continue;
                    }
                    else if (avCand.Id == Id)
                    {
                        // Caller � always a valid target.
                    }
                    else
                    {
                        Player avOwner = avCand.GetOwnerOfType<Player>();
                        if (avOwner == null) continue;
                        bool isRealPlayer = avOwner.PlayerConnection != null;
                        bool isFriendlyPhantom = avOwner.PhantomCreatorId != 0
                            && avOwner.PhantomCreatorId == this.PhantomHost?.Id;
                        // Skip other enemy phantoms (PhantomCreatorId == 0)
                        // and unrelated foreign phantoms.
                        if (isRealPlayer == false && isFriendlyPhantom == false) continue;
                    }
                }
                else
                {
                    // Friendly phantoms: any hostile Agent...
                    if (we is not Agent) continue;
                    if (phantom.IsHostileTo(we) == false) continue;

                    // ...but only if the enemy is close to the CALLER, not
                    // just close to the phantom. Otherwise a phantom sitting
                    // in idle formation next to you spots something 3500u
                    // out and sprints off after it, then trips the leash and
                    // teleports back � the "phantom keeps running away and
                    // snapping back" behavior.
                    //
                    // Team Deathmatch is the deliberate exception: your ally is a
                    // combatant in its own right and must chase rivals across the
                    // whole arena, not orbit you. Gated on the match roster, so
                    // ordinary squad phantoms keep the stay-near-me rule.
                    if (IsDeathmatchTeamCombatant(phantom.Id) == false)
                    {
                        float callerDistSq = Vector3.DistanceSquared2D(we.RegionLocation.Position, callerPos);
                        if (callerDistSq > PhantomFriendlyEngageMaxCallerDistSq) continue;
                    }
                }
                // True 3D distance, not 2D � this value feeds both the
                // outer PhantomAttackRange gate and every per-power range
                // check in TryPhantomAttack. On maps with real verticality
                // (gantries/platforms, e.g. Taskmaster Institute) a target
                // directly above/below reads as "close" under a 2D-only
                // distance even when it's actually far away in 3D space,
                // which is what let ranged (and melee) powers fire at
                // targets that were visibly nowhere near in-range.
                //
                // Edge-to-edge, not center-to-center: the real in-game range
                // check (Power.Validation.cs's IsInRangeInternal) subtracts
                // the target's Bounds.Radius before comparing against a
                // power's range � this AI didn't, so for a target with any
                // real collision size (bosses especially) the phantom had to
                // walk nearly into the target's CENTER before the melee gate
                // passed, producing a multi-second "walks into the enemy"
                // visual before the first attack could fire (confirmed live
                // 2026-07-19). Subtracting the target's radius here matches
                // the real validation and makes every downstream range check
                // (PhantomMeleeRangeSq/PhantomAttackRangeSq, and each
                // per-power range filter in TryPhantomAttack) agree with what
                // the client actually shows as "in range."
                float rawDist = Vector3.Distance(we.RegionLocation.Position, phantomPos);
                float edgeDist = MathF.Max(0f, rawDist - we.Bounds.Radius);
                float d = edgeDist * edgeDist;
                // Ambush phantoms only notice a target once it's actually
                // within THEIR detection range � the caller-is-always-valid
                // rule above still applies, but doesn't mean "always in
                // range." Without this gate every nemesis/rogue would
                // aggro the instant it spawned, patrol notwithstanding.
                if (isAmbushPhantom && d > NemesisDetectRangeSq)
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d, "out-of-detect-range"));
                    continue;
                }
                // Skip anything the engine won't accept as a valid target yet.
                // Dramatic-entrance bosses (Doom, Loki, terminal bosses...) spawn
                // with IsDormant=true until their intro cutscene wakes them
                // (Agent.cs:97 + WakeEndCallback line 3119). While dormant,
                // IsAffectedByPowersInternal returns false so
                // Power.IsValidTarget rejects the attack (Power.Validation.cs
                // line 313).
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable)
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d,
                        we.IsDormant ? "dormant" : we.IsUntargetable ? "untargetable" : "unaffectable"));
                    continue;
                }
                // Per-phantom blacklist: if we tried this target recently and
                // ActivatePower returned non-Success, skip for the blacklist window.
                // Lets phantoms rotate through other hostiles while a cutscene
                // boss finishes waking up, and lets the boss get picked up again
                // on the next tick after the blacklist expires.
                if (IsTargetBlacklisted(phantom.Id, we.Id, nowMsSweep))
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d, "blacklist"));
                    continue;
                }
                // ---- Threat score (ordering only; eligibility unchanged) ----
                //
                // Distance term dominates: a target at the phantom's feet
                // scores the full weight, one at the edge of the search
                // sweep scores ~0, so the bonuses below re-rank things that
                // are all roughly nearby rather than dragging a phantom
                // across the map.
                float threat = 0f;
                float edgeDistForScore = MathF.Sqrt(d);
                float closeness = 1f - Math.Clamp(edgeDistForScore / PhantomSearchRange, 0f, 1f);
                threat += closeness * PhantomThreatDistanceWeight * persDistanceMult;

                // Peel � is this hostile currently attacking the person we're
                // protecting? Only meaningful for friendly phantoms; enemy
                // phantoms are the aggressors, not bodyguards.
                // AIController is null for player-controlled avatars, so this
                // naturally only evaluates real AI mobs.
                if (enemyMode == false && we is Agent threatAgent)
                {
                    WorldEntity itsTarget = threatAgent.AIController?.TargetEntity;
                    if (itsTarget != null && itsTarget.Id == Id)
                        threat += PhantomThreatPeelWeight * persPeelMult;
                }

                // Finish � bias toward targets close to death so damage isn't
                // spread thin across a pack that all stays alive.
                float hpMax = we.Properties[PropertyEnum.HealthMax];
                if (hpMax > 0f)
                {
                    float hpPct = (float)we.Properties[PropertyEnum.Health] / hpMax;
                    if (hpPct > 0f && hpPct < PhantomThreatFinishHpPct)
                        threat += PhantomThreatFinishWeight * persFinishMult * (1f - (hpPct / PhantomThreatFinishHpPct));
                }

                // Focus fire � converge on what a squadmate already committed
                // to, so a group actually kills things instead of chipping.
                if (squadFocusId != 0 && we.Id == squadFocusId)
                    threat += PhantomThreatFocusWeight * persFocusMult;

                // Sticky � my own committed target from last tick keeps a lead
                // so score jitter can't flip-flop me between two similar
                // targets every tick. See PhantomThreatStickyWeight.
                if (committedTargetId != 0 && we.Id == committedTargetId)
                    threat += PhantomThreatStickyWeight;

                // Event hooks � fire once per unique boss/elite this phantom
                // encounters (not every tick it's still in range). Rank is
                // the same real classification the rest of this AI already
                // reads (ComputePhantomFollowStopDist's Boss/MiniBoss checks
                // elsewhere use the identical Rank enum).
                Rank weRank = we.GetRankPrototype()?.Rank ?? Rank.Popcorn;
                if (weRank == Rank.Boss || weRank == Rank.MiniBoss || weRank == Rank.GroupBoss)
                {
                    if (s_phantomSeenBossIds.Add((phantom.Id, we.Id)))
                        PhantomAIEvents.RaiseBossSpawn(phantom, we);
                }
                else if (weRank == Rank.Elite)
                {
                    if (s_phantomSeenEliteIds.Add((phantom.Id, we.Id)))
                        PhantomAIEvents.RaiseEliteSpawn(phantom, we);
                }

                candidates.Add((we, d, threat));
            }
            // Highest threat first. NOTE: the list is no longer distance-
            // ordered, so any downstream loop must not assume "once one is
            // out of range, the rest are too" � see the attack loop below.
            // Team Deathmatch sorts by DISTANCE, not threat. Combatants can see
            // the whole arena there, so threat ordering would send them past an
            // adjacent rival to chase a wounded one on the far side of the map.
            // Nearest-first keeps fights local and stops everyone converging into
            // one scrum. Gated on the match roster, so every other mode keeps the
            // threat ordering it was tuned with.
            if (IsDeathmatchTeamCombatant(phantom.Id))
            {
                // Distance sort with a sticky discount � the committed target
                // sorts as if it were at half its squared distance, so only a
                // meaningfully closer rival displaces it. Not a static lambda:
                // it has to capture the committed id.
                ulong dmSticky = committedTargetId;
                candidates.Sort((a, b) =>
                {
                    float da = a.we.Id == dmSticky ? a.distSq * DeathmatchStickyDistSqFactor : a.distSq;
                    float db = b.we.Id == dmSticky ? b.distSq * DeathmatchStickyDistSqFactor : b.distSq;
                    return da.CompareTo(db);
                });
            }
            else
                candidates.Sort(static (a, b) => b.threat.CompareTo(a.threat));

            if (diagWant && (candidates.Count == 0 || diagRejected != null))
                DumpPhantomHuntDiag(phantom, phantomPos,
                    candidates.Count > 0 ? candidates[0].we : null,
                    candidates.Count > 0 ? candidates[0].distSq : 0f,
                    diagRejected, region, sweepSphere, ctx);

            if (candidates.Count == 0)
            {
                // Enemy mode: don't stop if the player just died � the
                // caller's Avatar is IsDead briefly and gets filtered out
                // of the sweep, and if we stop here the phantom sits idle
                // waiting for a target that IS alive to appear. Instead,
                // keep advancing on the caller's position so we're on top
                // of them the moment they revive. Also flush the target /
                // power blacklists that may have accumulated during the
                // death sequence so the wake-up is clean.
                //
                // Ambush phantoms are the exception: no candidate in range
                // means nobody's found them yet, so they patrol their own
                // territory instead of homing in on the caller � that's the
                // whole point of the ambush behavior (see UpdateNemesisPatrol).
                if (enemyMode)
                {
                    PruneBlacklistFor(phantom.Id);
                    PrunePowerBlacklistFor(phantom.Id);

                    if (isAmbushPhantom)
                    {
                        UpdateNemesisPatrol(phantom, region, rng);
                        return;
                    }

                    Avatar callerAv = this;
                    var loco2 = phantom.Locomotor;
                    if (callerAv != null && loco2 != null)
                    {
                        var opts2 = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                        // Follow the caller Avatar entity � even while dead
                        // the entity id is stable and the locomotor can path
                        // to the corpse's position.
                        loco2.FollowEntity(callerAv.Id, PhantomAttackRange, PhantomAttackRange, ref opts2, false);
                    }
                    return;
                }

                // Team Deathmatch: nothing in sight, so ROAM. With sight cut to
                // DeathmatchSightRange, teams start out unable to see each other
                // � without roaming they would simply stand on their spawn points
                // forever and no fight would ever happen. Walking to random
                // navi-mesh points is what makes them find each other.
                if (IsDeathmatchTeamCombatant(phantom.Id))
                {
                    TryDeathmatchRoam(phantom, region, rng);
                    return;
                }

                // Friendly mode idle � trail the caller organically.
                //
                // Each phantom picks a personal slot around the caller
                // derived from a hash of its runtime id: a preferred angle
                // (not the evenly-spaced-ring "marching" pattern) and a
                // preferred distance (natural spread across the squad).
                // Because every phantom's angle is unique, they don't
                // converge on the same follow spot � no stacking.
                //
                // Arrival tolerance is generous (~120u) so once a phantom
                // gets "close enough" to its slot they Stop, instead of
                // micro-correcting every tick. Reads as a group of friends
                // walking with you, not a locked drill formation.
                var idleLoco = phantom.Locomotor;
                if (idleLoco != null)
                {
                    Vector3 slotPos = ComputePhantomIdleSlot(phantom, region);
                    float slotDistSq = Vector3.DistanceSquared2D(phantomPos, slotPos);
                    if (slotDistSq > PhantomFormationArriveDist * PhantomFormationArriveDist)
                    {
                        var idleOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(400) };
                        idleLoco.PathTo(slotPos, ref idleOpts);
                    }
                    else
                    {
                        idleLoco.Stop();
                    }
                }
                return;
            }

            // Advance toward the closest survivor for movement, but for the
            // attack try each in order � the closest might be a Living Laser
            // waiting on his cutscene entry that rejects power activation for a
            // few seconds, while the actual boss is right behind him and
            // attackable now. Without the fallback the phantom stood on the
            // first target and never fired.
            // Per-hero combat range preference, resolved once per tick. Keyed
            // by the phantom's AVATAR prototype (which hero it is), not the
            // phantom entity, so the setting applies to that hero wherever it
            // is spawned. Auto (the default) reproduces the original behavior.
            PhantomCombatRangePref phantomRangePref =
                PhantomHost?.GetCombatRangePref(phantom.PrototypeDataRef) ?? PhantomCombatRangePref.Auto;

            // Commit to the best target WE CAN SEE, falling back to the best
            // overall only when nothing in the top of the list is visible.
            //
            // Committing purely to candidates[0] pinned phantoms to targets
            // around corners: the sticky bonus then held that commitment tick
            // after tick, the attack loop (correctly) refused to swing at a
            // no-LoS target, and if the approach path also failed the phantom
            // froze � observed live 2026-08-07, a Captain America standing at
            // a corner doing nothing while the rest of the squad fought mobs
            // it could plainly see. Scanning the top few candidates for one
            // with line of sight costs at most a handful of raycasts and makes
            // the phantom fight what is actually in front of it.
            //
            // (The LoS concept itself: the engine's own mob AI checks it in
            // Combat.cs:166, UsePower.cs:360 and MoveTo.cs:192 � this AI
            // historically checked none of them.)
            //
            // Mistake rate � imperfect-decision-modeling: swap in the
            // second-best candidate instead of the objectively best one, at
            // a small personality-tuned probability, so target selection
            // doesn't read as a perfect optimizer every single tick. Never
            // applied to Team Deathmatch's distance-sorted list � that sort
            // exists for fairness/pacing, not a "best pick," and a mistake
            // there would just look like random target flailing rather than
            // a believable misjudgment.
            if (candidates.Count >= 2 && IsDeathmatchTeamCombatant(phantom.Id) == false
                && rng.NextFloat() < GetPersonalityMistakeRate(personality))
            {
                (candidates[0], candidates[1]) = (candidates[1], candidates[0]);
            }

            WorldEntity primaryTarget = candidates[0].we;
            float primaryDistSq = candidates[0].distSq;
            bool primaryLoS = phantom.LineOfSightTo(primaryTarget);

            if (primaryLoS == false)
            {
                int scan = Math.Min(4, candidates.Count);
                for (int i = 1; i < scan; i++)
                {
                    if (phantom.LineOfSightTo(candidates[i].we) == false) continue;
                    primaryTarget = candidates[i].we;
                    primaryDistSq = candidates[i].distSq;
                    primaryLoS = true;
                    break;
                }
            }

            // Record the commitment for next tick's sticky bonus � AFTER the
            // visibility scan, so stickiness reinforces a target the phantom
            // can actually fight, never one it is blind to.
            s_phantomCommittedTargetId[phantom.Id] = primaryTarget.Id;

            // Survival Logic � checked AFTER self-heal already had its shot
            // (at the top of this method) and failed/wasn't available, so
            // retreat is the FALLBACK survivability response, not competing
            // with the primary one. See TryPhantomSurvivalRetreat's header.
            if (TryPhantomSurvivalRetreat(phantom, region, primaryTarget, primaryDistSq, enemyMode, nowMsSweep, rng))
                return;

            // Team Deathmatch, long-range target: go straight to the short-hop
            // roam instead of ever calling FollowEntity on a target this far away.
            //
            // Region-wide sight (added 2026-08-08, DeathmatchSightRange) means a
            // combatant can see a target 9000-10000+ units off from the moment the
            // match starts. FollowEntity on a target that far tries to generate one
            // single path across the whole map � the previous "roam on path
            // failure" fallback assumed that call would visibly fail
            // (NaviPathResult.Failed) and catch it, but that stopped producing any
            // movement at all on a live retest (reported 2026-08-08: combatants
            // spawn in and never move, worse than before region-wide sight, when
            // short-range roam at least wandered visibly). Rather than keep
            // trusting a specific pathfinder result code to decide when to fall
            // back, skip the long-range FollowEntity attempt entirely and always
            // take the short reliable hops toward the target � the same roam this
            // AI already uses successfully once no direct-range candidate exists.
            const float DeathmatchDirectPathMaxDist = 3500f;
            const float DeathmatchDirectPathMaxDistSq = DeathmatchDirectPathMaxDist * DeathmatchDirectPathMaxDist;
            if (IsDeathmatchTeamCombatant(phantom.Id) && primaryDistSq > DeathmatchDirectPathMaxDistSq)
            {
                TryDeathmatchRoam(phantom, region, rng, primaryTarget.RegionLocation.Position);
                return;
            }

            // Always keep the Locomotor advancing toward the target � even when
            // we're inside attack range. Stopping while attacking was the reason
            // phantoms visually stood still: my previous tick called Stop() every
            // time primaryDistSq was in range, so they only ever ticked "stop,
            // cast, stop, cast" with no walking between. Now we walk in, stop only
            // if Locomotor reaches the target's radius, and fire the power
            // regardless � the engine cancels movement automatically while a
            // cast animation runs (Locomotor.Locomote respects ActivePower flags).
            var loco = phantom.Locomotor;
            if (loco != null)
            {
                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                // Follow only as close as the phantom's widest usable power's
                // range. Ranged heroes (Storm, Iron Man, Rocket) stop at
                // projectile range and start casting; melee heroes (Thing,
                // Colossus) keep walking in to 50u. Without this every
                // phantom sprinted into point-blank on every target � visually
                // wrong for ranged kits, and left the phantom stuck at 50u
                // firing projectiles the client had to render at melee.
                float followStopDist = ComputePhantomFollowStopDist(phantom, primaryTarget, phantomRangePref);

                // No line of sight: standing at standoff range is useless, the
                // wall between us doesn't care about projectile range. Tighten
                // the stop distance toward melee so the phantom keeps walking �
                // FollowEntity's navmesh path naturally goes AROUND the
                // obstruction, and LoS opens somewhere along that path, at which
                // point the normal standoff resumes next tick.
                if (primaryLoS == false)
                    followStopDist = MathF.Min(followStopDist, 100f);

                bool ok = loco.FollowEntity(primaryTarget.Id, followStopDist, followStopDist, ref opts, false);
                if (s_phantomLocoLogged.Add(phantom.Id))
                {
                    PhantomLogger.Info($"[PhantomHero:Loco] {phantom} authoritative={phantom.IsMovementAuthoritative} simulated={phantom.IsSimulated} inWorld={phantom.IsInWorld} target={primaryTarget.Id:X} dist={MathF.Sqrt(primaryDistSq):F0} FollowEntity returned={ok} locoEnabled={loco.IsEnabled} isMoving={loco.IsMoving} method={loco.Method} baseSpeed={loco.DefaultRunSpeed} hasPath={loco.HasPath} pathResult={loco.LastGeneratedPathResult} canMove={phantom.CanMove()}");
                }

                // Throttled (not one-shot) version for Team Deathmatch specifically �
                // added 2026-08-08 alongside the TDM:Roam log, same reason: the
                // one-shot log above fired during the pre-match lock on the report
                // we're chasing and told us nothing about behavior after unlock.
                // Remove once confirmed working.
                if (IsDeathmatchTeamCombatant(phantom.Id)
                    && (s_deathmatchRoamNextDiagMs.TryGetValue(phantom.Id, out long nextFollowDiagMs) == false || nowMsSweep >= nextFollowDiagMs))
                {
                    s_deathmatchRoamNextDiagMs[phantom.Id] = nowMsSweep + 3000;
                    PhantomLogger.Info($"[TDM:Follow] {phantom} target={primaryTarget.Id:X} dist={MathF.Sqrt(primaryDistSq):F0} FollowEntity={ok} isMoving={loco.IsMoving} pathResult={loco.LastGeneratedPathResult} canMove={phantom.CanMove()}");
                }

                // Pathfinding failure � enemy phantoms in Manhattan / verticality
                // regions can end up on an elevated platform (Z=49) while the
                // player is at ground level (Z=1), and FollowEntity's navmesh
                // path resolution returns Failed. Without a fix the phantom just
                // stands there for the whole fight. Detect and force-leash to a
                // valid navmesh spot near the target so the fight resumes at
                // ground level. Only fires when target is out of attack range
                // � a small path glitch inside attack range is fine, the
                // phantom will just cast from where they stand.
                // Team Deathmatch combatants are never teleported out of a
                // pathing failure � a body blinking across the arena reads as a
                // bug to anyone watching, and this mode has no leash tying them
                // to the player anyway. They re-path next tick or roam elsewhere.
                // Deathmatch combatants get no teleport rescue (a body blinking
                // across the arena reads as a bug) � but they used to get NOTHING
                // on a failed path, standing still until the roam logic happened
                // to fire. The failure signal is instant (LastGeneratedPathResult
                // is already read on this very line for the diag), so use it:
                // roam to a fresh navmesh point on foot and approach from there.
                // BOTH rescue gates below also fire when there is no line of
                // sight, not only when out of attack range. "In range" through
                // a wall is not in range in any way that matters � the attack
                // loop refuses no-LoS targets, so a failed path to one used to
                // fall through both rescues and freeze the phantom at the
                // corner (the Captain America stall, 2026-08-07).
                bool pathFailed = ok == false
                    && (loco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.Failed
                     || loco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.FailedNaviMesh);
                bool stranded = primaryDistSq > PhantomAttackRangeSq || primaryLoS == false;

                if (pathFailed && stranded && IsDeathmatchTeamCombatant(phantom.Id))
                {
                    // Bias the roam toward the target we actually know about,
                    // not a random direction � see TryDeathmatchRoam's header.
                    TryDeathmatchRoam(phantom, region, rng, primaryTarget.RegionLocation.Position);
                    return;
                }

                if (pathFailed && stranded && IsDeathmatchTeamCombatant(phantom.Id) == false)
                {
                    Vector3 targetPos = primaryTarget.RegionLocation.Position;
                    Vector3 rescuePos = ChoosePhantomLeashPos(region, targetPos, rng, phantom.Bounds.Radius);
                    try
                    {
                        loco.Stop();
                        phantom.ChangeRegionPosition(rescuePos, null);
                        s_phantomStuckTrack[phantom.Id] = (rescuePos, 0);
                        PhantomLogger.Info($"[PhantomHero:Loco] {phantom} path failed, force-leashed to {rescuePos.ToStringNames()} near target");
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loco] path-fail leash threw: {ex.Message}"); }
                }

                // Thin-wall stall: no line of sight, but FollowEntity considers
                // us ARRIVED (inside the tightened 100u stop, path not failed) �
                // the corner itself is between us. FollowEntity won't move a
                // phantom that's already within stop distance, so path straight
                // to the target's own position instead; the navmesh route bends
                // around the geometry and LoS opens along the way.
                if (primaryLoS == false && ok && loco.IsMoving == false)
                {
                    var stallOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                    loco.PathTo(primaryTarget.RegionLocation.Position, ref stallOpts);
                }
            }

            // Only fire an attack when the phantom is settled � either the
            // Locomotor has arrived (or is close enough that the last step
            // is trivial), or the target is inside melee range. Firing while
            // FollowEntity is mid-path produces the "skating" look: the
            // cast animation cancels walking mid-stride but position keeps
            // advancing, so the character glides without a walk cycle.
            bool arrived = loco == null || loco.IsMoving == false;

            // Range gating must use the CLOSEST candidate, not candidates[0].
            // The list is threat-sorted now, so candidates[0] is the target we
            // want to commit to � which can legitimately be one we're still
            // walking toward (e.g. peeling something off the player) while a
            // different enemy is already at arm's length. Gating on
            // candidates[0]'s distance would make the phantom walk right past
            // an adjacent enemy without ever swinging at it. The attack loop
            // below re-checks each candidate's own range anyway, so this is
            // purely "is there anything at all worth swinging at from here".
            float closestDistSq = float.MaxValue;
            WorldEntity closestHostile = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].distSq >= closestDistSq) continue;
                closestDistSq = candidates[i].distSq;
                closestHostile = candidates[i].we;
            }

            // Ranged kiting � back off when something has closed the gap.
            //
            // UNITS: candidate distances are EDGE-to-edge (the sweep subtracts
            // the target's Bounds.Radius). ComputePhantomFollowStopDist adds
            // the target radius back for the Locomotor, which is
            // centre-to-centre � passing null here yields the phantom's pure
            // standoff range so both sides of the comparison are edge-based.
            // Mixing those two would make the trigger distance wrong by a
            // boss-sized margin.
            if (closestHostile != null)
            {
                float pureStandoff = ComputePhantomFollowStopDist(phantom, null, phantomRangePref);
                long nowKiteMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (TryPhantomKite(phantom, region, closestHostile.RegionLocation.Position,
                        MathF.Sqrt(closestDistSq), pureStandoff, nowKiteMs, rng))
                    return;
            }

            bool inMelee = closestDistSq <= PhantomMeleeRangeSq;
            if ((arrived || inMelee) && closestDistSq <= PhantomAttackRangeSq)
            {
                // Anti-clustering spacing dash � checked first so it can
                // preempt the attack this tick when it fires (own internal
                // cooldown means this is rare, ~every 10-16s per phantom).
                // See PhantomSpacingDashCooldownMs.
                //
                // Skipped when already in melee range or when there's only one
                // hostile in the candidate list � the whole point of this dash
                // is anti-CLUSTERING for a squad; a melee phantom that just spent
                // seconds closing the gap immediately dashing away created an
                // approach-dash-approach loop, and anything effectively 1v1 has
                // no squad to space out from, so it just randomly disengaged
                // mid-fight (confirmed live 2026-07-20 audit for enemy/nemesis
                // phantoms). Originally gated to enemyMode only, which left
                // friendly phantoms doing the same walk-in-then-dash-away dance
                // whenever they were effectively 1v1 in Deathmatch (reported
                // live 2026-08-08: ally phantoms looked far less committed to
                // closing the distance than enemy phantoms did).
                bool skipDashHere = inMelee || candidates.Count <= 1;
                if (skipDashHere == false && TryPhantomSpacingDash(phantom, region, rng))
                    return;

                // Per-phantom attack cooldown � prevents the 2 Hz tick from
                // burst-firing 2 attacks per second. Real players average
                // closer to 1 attack per 800-1200 ms after animation locks.
                long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                bool attackReady = s_phantomNextAttackMs.TryGetValue(phantom.Id, out long nextAt) == false || now >= nextAt;

                // Reaction delay � see IsPhantomReactionReady's header. Gates
                // only the FIRST attack against a brand-new primary target;
                // has no effect on an already-engaged one, since the target
                // id passed in hasn't changed tick to tick.
                attackReady = attackReady && IsPhantomReactionReady(phantom.Id, primaryTarget.Id, personality, now, rng);

                // The statue window: arrived, in range, but the attack timer
                // has not elapsed. This used to be a hard stand-still � the
                // single most bot-like tell the AI had. A short cooldown-gated
                // lateral step keeps them in motion between casts; the engine
                // cancels the move automatically if a cast starts
                // (Locomotor.Locomote respects ActivePower), so this can never
                // delay an attack.
                if (attackReady == false && loco != null && loco.IsMoving == false && closestHostile != null)
                    TryPhantomCombatStrafe(phantom, region, closestHostile.RegionLocation.Position, now, rng);

                if (attackReady)
                {
                    // Try candidates in THREAT order. First one that
                    // ActivatePower accepts wins. Others get blacklisted only
                    // when they actually get an activate attempt � we don't
                    // pre-check IsValidTarget because that would double the
                    // per-tick work for the common case where the top pick is
                    // fine.
                    bool fired = false;
                    int maxTries = Math.Min(5, candidates.Count);
                    for (int i = 0; i < maxTries; i++)
                    {
                        WorldEntity tryTarget = candidates[i].we;
                        float tryDistSq = candidates[i].distSq;
                        // `continue`, NOT `break`. This loop used to break here
                        // because the list was sorted by distance, so the first
                        // out-of-range entry guaranteed the rest were further
                        // still. The list is threat-sorted now, so an
                        // out-of-range high-threat target can sit above a
                        // perfectly attackable closer one � breaking here would
                        // silently skip it and the phantom would stand idle.
                        if (tryDistSq > PhantomAttackRangeSq) continue;

                        // Line of sight � never swing at something behind a wall.
                        // Without this, a LoS-requiring power fails on activation
                        // and gets transient-blacklisted, which reads as the
                        // phantom cycling its kit while hitting nothing. The
                        // committed target's result is reused from the once-per-
                        // tick check above; other candidates pay one raycast
                        // each, and only when actually reached in this loop.
                        // The target is NOT blacklisted for it � walls stop
                        // blocking as soon as either side moves.
                        bool hasLoS = tryTarget.Id == primaryTarget.Id
                            ? primaryLoS
                            : phantom.LineOfSightTo(tryTarget);
                        if (hasLoS == false) continue;

                        PowerUseResult r = TryPhantomAttack(phantom, tryTarget, tryDistSq, rng);
                        if (r == PowerUseResult.Success)
                        {
                            fired = true;
                            // Successful hit � make sure this target isn't
                            // blacklisted from a stale prior tick.
                            ClearTargetBlacklist(phantom.Id, tryTarget.Id);
                            // Publish as the squad's focus target so squadmates
                            // converge on it instead of each chipping something
                            // different. Refreshed on every landed hit, so the
                            // focus follows whatever the squad is actually
                            // fighting and expires on its own once they stop.
                            SetPhantomSquadFocusTarget(squadHostId, enemyMode, tryTarget.Id, now);
                            break;
                        }
                        // Only blacklist the TARGET for target-specific failures.
                        // Everything else (RestrictiveCondition, WeaponMissing,
                        // OutOfPosition, InsufficientEndurance, FullscreenMovie,
                        // Cooldown, ...) is about the PHANTOM or the POWER,
                        // not the target � the failing power gets blacklisted
                        // internally in TryPhantomAttack, and the picker will
                        // choose a different one next tick. Nuking the target
                        // for 3 seconds was what made enemy phantoms silently
                        // give up on the player while cycling through every
                        // restrictive-condition power one at a time.
                        bool targetIsTheProblem =
                            r == PowerUseResult.BadTarget
                            || r == PowerUseResult.TargetIsMissing;
                        if (targetIsTheProblem)
                            BlacklistTarget(phantom.Id, tryTarget.Id, nowMsSweep);
                    }
                    if (!fired && diagWant)
                        PhantomLogger.Info($"[PhantomHero:Attack] {phantom} all {maxTries} candidates rejected the attack � sweep found {candidates.Count} hostile(s), first={candidates[0].we} dist={MathF.Sqrt(candidates[0].distSq):F0}");
                    // Enemy phantoms attack ~2x faster than friendlies �
                    // 400ms + 300ms jitter vs 800ms + 400ms � because the
                    // player is one target being ganged up on, not a squad
                    // sharing pressure. Enough to feel dangerous without
                    // being animation-jamming.
                    long baseCd = enemyMode ? 400 : 800;
                    long jitter = enemyMode ? 300 : 400;
                    s_phantomNextAttackMs[phantom.Id] = now + baseCd + (long)(rng.NextDouble() * jitter);
                }
            }
        }

        // Killable-phantom balance knobs.
        //
        // Friendly phantoms (Squad Builder / !phantom spawn) intentionally
        // stay closer to a real avatar's stat block � they're squadmates,
        // not the main event, and boosting them further trivialises the
        // fights they're helping with.
        //
        // Enemy phantoms (Rogue Encounter, Wave Director, Enemy Phantoms
        // tool) are the CONTENT � they exist to challenge the player, so
        // they hit harder and take more punishment. Nemesis rank layers on
        // top of the enemy values so returning nemeses feel meaningfully
        // more dangerous than a fresh rogue spawn.
        //
        // Doubled 2.0 -> 4.0 (2026-07-21), grounded in a real live-logged
        // "[NemesisDamage]" balance investigation, not a guess: enemy
        // phantom ULTIMATE/finisher powers (SolarOvercharge, Tantrum,
        // GammaPunch, LeapImplodeEnd...) were landing for ~100% of a
        // friendly phantom's max HP in a single hit � confirmed across
        // ranks -1 (plain rogue), 1, and 2, so nemesis rank scaling was
        // NOT the actual driver despite that being the initial suspicion.
        // The real cause: this session's AI rework made the "a ready
        // ultimate wins outright" power-pick rule reliably fire (the old
        // broken cooldown-based picker rarely reached it), and those
        // ultimates are balanced against a real geared level-60 player's
        // HP/defense, not a phantom's 2x-baseline pool. Doubling the pool
        // roughly halves the fraction of max HP one of these hits removes
        // (~100% -> ~50%), turning a guaranteed kill into a big, survivable
        // hit a phantom can self-heal back from (self-heal already
        // triggers at <=35% HP) instead of dying outright.
        // Bumped 4.0 -> 45.0 (2026-07-21), grounded in real logged HealthMax
        // values from [NemesisDamage] (base HealthMax before any mult, i.e.
        // observed/4.0): Wolverine ~22263, Storm ~20393, Cyclops ~18953,
        // JeanGrey ~13757 � the weakest of these needed. User's explicit ask:
        // a max-level (60) friendly phantom should have AT LEAST 500k HP.
        // 45x on the weakest base (JeanGrey) lands at ~619k, comfortably
        // over the floor with margin for other heroes' bases running lower
        // still. Also switched both friendly spawn paths below from a flat
        // assignment to ScaleHealthMultForLevel(PhantomHealthMult, level) �
        // previously this mult was NOT level-scaled at all (unlike the enemy
        // pool), so a level-1 friendly phantom would already reach full 45x;
        // now it ramps the same quadratic 35%->100% curve enemy phantoms use,
        // reaching the full 45x (and the 500k+ floor) only at level 60.
        private const float PhantomHealthMult      = 45.0f;  // friendly: +4400% HealthMax at level 60 (was flat 4.0/+400%)
        // Enemy pool got a big HP bump after the PvP-damage-scaling bug fix.
        // Previously they took ~1000� reduced damage on the player's attacks,
        // so 3.0� base HP felt tanky. With full damage now landing, they melt
        // in ~2 shots unless we compensate � 8.0� keeps rogue encounters
        // dangerous without going overboard.
        private const float EnemyPhantomHealthMult = 8.0f;

        // Deathmatch HP � same value for both teams (unlike the 45.0/8.0
        // friendly/enemy split above, which was tuned for solo PvE and would
        // otherwise leave deathmatch opponents absurdly squishy relative to
        // the player's own real, unscaled gear damage). Set a bit above the
        // existing friendly baseline per live feedback ("could use a little
        // more HP" on both sides after the deathmatch damage-profile fix) �
        // tune this one constant if matches still end too fast either way.
        private const float DeathmatchPhantomHealthMult = 55.0f;

        // Per-phantom "downed since" timestamp � 0 when alive.
        // Populated on the first tick that observes IsDead; removed on the
        // first alive tick after a revive so the alive branch can detect
        // the transition and force a client-side pose refresh.
        private static readonly Dictionary<ulong, long> s_phantomDownedSinceMs = new();

        // Grace window (ms) for ReattachPhantomTick's same-region
        // IsInWorld==false check � a phantom whose leash/teleport catch-up
        // hasn't landed yet at the exact instant the caller re-enters world
        // (scripted boss-arena entrance, mission-portal cutscene, any local
        // same-region transition) used to get force-destroyed immediately,
        // with no revive and no requeue � confirmed live: a player lost 2 of
        // 4 friendly phantoms during a subway boss fight with no death UI.
        // Only destroy if the phantom has been out-of-world in the SAME
        // region for longer than this; a real cross-region divergence
        // (phantom.Region != myRegion) is unambiguous and still destroys
        // immediately, same as before.
        private const long PhantomReattachGraceMs = 8_000;
        private static readonly Dictionary<ulong, long> s_phantomReattachGraceSinceMs = new();

        // Per-phantom next-attack timestamp (ms). Enforces at least ~800ms
        // between casts so the tick doesn't spam-fire.
        private static readonly Dictionary<ulong, long> s_phantomNextAttackMs = new();

        // Per-phantom next-spacing-dash timestamp (ms). Movement/dash
        // powers are otherwise never used by phantom AI at all (excluded
        // from the normal attack candidate pool) � this fires one purely
        // for anti-clustering spacing every ~10-16s per phantom (jittered
        // so a squad doesn't dash in lockstep), not as a tactical dodge
        // (no hazard/AoE detection exists to dodge with). Requested live
        // (2026-07-19): a 9-phantom Ultron raid saw the whole squad
        // converge into one blob that a single boss AoE could half-wipe.
        private const long PhantomSpacingDashCooldownMs = 10_000;
        private const long PhantomSpacingDashJitterMs = 6_000;
        private static readonly Dictionary<ulong, long> s_phantomNextDashMs = new();

        // Per-downed-target revive claim (downedId -> (claimantPhantomId,
        // claimedAtMs, firstClaimedAtMs)) � stops the whole squad from
        // independently deciding to revive the same ally at once. See the
        // claim check in UpdatePhantomHunt for the full rationale.
        // PhantomReviveClaimTimeoutMs is an INACTIVITY timeout (claim goes
        // stale if the claimant stops re-asserting it); PhantomReviveClaimMaxHoldMs
        // is a hard cap on total hold time regardless of activity � added
        // 2026-07-20 after an audit found the claimant refreshing its claim
        // every tick meant the "timeout" never actually fired for an active
        // (even if farthest-away or repeatedly-rejected) claimant, letting
        // it lock out closer phantoms indefinitely.
        private const long PhantomReviveClaimTimeoutMs = 6_000;
        private const long PhantomReviveClaimMaxHoldMs = 20_000;
        private static readonly Dictionary<ulong, (ulong claimantId, long claimedAtMs, long firstClaimedAtMs)> s_phantomReviveClaim = new();

        // 2026-07-28 � real bug found live: unlike RestrictiveCondition (an
        // expected, self-resolving mid-combat interruption with explicit
        // reasoning below), an OutOfPosition rejection had ZERO handling �
        // the claimant just re-logged and retried the exact same cast
        // forever. Confirmed live: a phantom held a revive claim for over a
        // minute straight, rejected OutOfPosition roughly every 0.5s, never
        // attacking or moving. Made worse by a second bug: the 20s
        // PhantomReviveClaimMaxHoldMs cap above is only ever evaluated from
        // ANOTHER phantom's perspective when it's deciding whether to back
        // off � the claimant's own tick never checks its own hold duration
        // (claim.claimantId != phantom.Id short-circuits false for the
        // current holder), so a claimant with nothing nearby to contest it
        // literally never hits that cap. Tracked per downed-entity-ID
        // (reset whenever the claim changes hands or the revive succeeds)
        // since only one phantom holds a given claim at a time anyway.
        private const int PhantomReviveRepositionAfterFailures = 3;
        private const int PhantomReviveGiveUpAfterFailures = 8;
        private static readonly Dictionary<ulong, int> s_phantomReviveOutOfPositionCount = new();

        /// <summary>Phantoms already logged once for "no usable power" � keeps the diagnostic from spamming every tick.</summary>
        private static readonly HashSet<ulong> s_phantomNoPowerLogged = new();

        // Per-phantom next-ultimate timestamp (ms). Ultimates fire on any
        // target once available, then rest for 20 minutes regardless of
        // what the power data's own cooldown says.
        private const long PhantomUltimateCooldownMs = 20 * 60 * 1000;
        // Wait 15 seconds after spawn before an enemy phantom can throw
        // their first Ultimate � prevents the "spawn ? nuke ? dead player"
        // one-shot experience while still letting the ultimate happen
        // later in the fight.
        private const long EnemyPhantomUltimateOpenerBlockMs = 15 * 1000;
        private static readonly Dictionary<ulong, long> s_phantomNextUltimateMs = new();

        // Per-(phantom, power) blacklist. Some powers fail for reasons that
        // won't clear on their own � WeaponMissing (needs an equipped item
        // the phantom doesn't have) and NotAllowedByTransformMode (needs a
        // transform state the phantom AI never enters, e.g. Rogue's
        // GlovesOff) are genuinely structural: nothing changes for the rest
        // of the fight, so the long window is correct there. Without
        // blacklisting these, a broken power with a big cooldown weight
        // gets picked every tick against every target (per-TARGET blacklist
        // doesn't help) and the phantom never lands a hit.
        //
        // RestrictiveCondition is NOT the same kind of failure � confirmed
        // (2026-07-20) that for a phantom this result is almost always
        // caused by the phantom's OWN transient status (stunned/held/
        // immobilized/keyword-locked for a few seconds � Agent.cs's
        // RestrictiveCondition sites), not a structurally broken power.
        // Blacklisting it for the same 10 minutes as a genuinely broken
        // power was the actual cause of a real live bug: over a long fight
        // more and more powers eventually get unlucky enough to trip this
        // mid-status-effect at some point, and since nothing prunes the
        // blacklist while a fight is continuously ongoing (only phantom
        // destruction / zero-candidates-in-range does), the working
        // candidate pool measurably shrinks the longer the fight runs �
        // "aggressive early, passive later," confirmed live. Given its
        // short-lived real cause, RestrictiveCondition gets its own much
        // shorter window instead.
        private const long PhantomPowerBlacklistMs = 10 * 60 * 1000;
        private const long PhantomTransientPowerBlacklistMs = 15 * 1000;
        private static readonly Dictionary<(ulong phantomId, PrototypeId powerRef), long> s_phantomPowerBlacklist = new();
        private static bool IsPhantomPowerBlacklisted(ulong phantomId, PrototypeId powerRef, long nowMs)
            => s_phantomPowerBlacklist.TryGetValue((phantomId, powerRef), out long expiresAt) && nowMs < expiresAt;
        private static void PrunePowerBlacklistFor(ulong phantomId)
        {
            List<(ulong, PrototypeId)> toRemove = null;
            foreach (var key in s_phantomPowerBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomPowerBlacklist.Remove(k);
        }

        // Stuck-power watchdog. Channeled / recurring powers (beam channels
        // and some ultimates) never end on their own for phantoms � a real
        // player ends them by releasing the button, which the phantom can't
        // do. A stuck ActivePowerRef rejects every subsequent attack AND
        // revive with PowerInProgress, soft-locking the phantom forever.
        // If the same power stays active for this many consecutive ticks
        // (500ms each), force-end it.
        private const int PhantomStuckPowerTicks = 10; // 5 seconds
        private static readonly Dictionary<ulong, (PrototypeId powerRef, int ticks)> s_phantomActivePowerTrack = new();

        // Stuck-invulnerable watchdog � a "cheat death"/self-revive gear
        // proc sets PropertyEnum.Invulnerable via a Condition/toggle that a
        // real client would eventually release; a phantom never does.
        // Confirmed live: a phantom with a self-revive item keeps moving and
        // attacking normally but can't be damaged, indefinitely. Force-clear
        // it (and end whatever power is still active, in case it's a
        // toggle) after this many consecutive ticks. Deliberately-invincible
        // phantoms (Squad Builder's opt-in god mode, see the "invincible"
        // spawn param) are tracked separately and exempt from this.
        // 2026-07-26: was 6 ticks (3s) � confirmed live this same mechanism
        // also fires from GEAR item procs (Powers/ItemPowers/ItemConditions/
        // CosmicItemInvulnerableBuff.prototype on a Punisher phantom), not
        // just hero self-revive powers, and can recur repeatedly through a
        // single fight. Whatever the proc's own legitimate active duration
        // is happens BEFORE it's even detected as "stuck", so the full
        // window a player sees no damage land is that duration PLUS however
        // long this watchdog waits on top � tightened to minimize the added
        // wait, not the root cause (the Condition itself never completing
        // for an AI-driven phantom, which needs the source data to fix
        // properly and isn't fixable in general here).
        private const int PhantomStuckInvulnerableTicks = 2; // 1 second
        private const float PhantomStandaloneAggroRange = 3000f;
        private static readonly Dictionary<ulong, int> s_phantomInvulnerableTrack = new();
        private static readonly HashSet<ulong> s_phantomDeliberatelyInvincible = new();

        /// <summary>
        /// TIMED deliberate invulnerability � Deathmatch spawn protection.
        ///
        /// Deliberately NOT s_phantomDeliberatelyInvincible. That set is for
        /// permanent god-mode phantoms, and membership disables the stuck-
        /// invulnerability watchdog outright (see exemptDeliberate below). Using it
        /// for a temporary window means that if the scheduled lift ever fails to
        /// fire, the phantom is left Invulnerable AND PowerLocked with the safety
        /// net switched off � it can never be killed and never attacks. That is
        /// exactly what happened live 2026-08-05: a Colossus stopped attacking,
        /// could not be killed, and its team never respawned because it never died.
        ///
        /// An expiry makes it self-healing: past the deadline the watchdog reclaims
        /// the phantom and clears both properties itself.
        /// </summary>
        private static readonly Dictionary<ulong, long> s_phantomTimedInvincibleUntilMs = new();

        internal static void MarkPhantomTimedInvincible(ulong phantomId, long untilMs) => s_phantomTimedInvincibleUntilMs[phantomId] = untilMs;

        internal static void ClearPhantomTimedInvincible(ulong phantomId) => s_phantomTimedInvincibleUntilMs.Remove(phantomId);
        // No-repeat guard: (phantom, condition/power ref) -> game-time ms until which
        // that specific condition source is barred from granting invulnerability again.
        private static readonly Dictionary<(ulong, PrototypeId), long> s_phantomInvulnConditionCooldownUntilMs = new();

        // Per-(phantom,target) blacklist expiry. Populated when ActivatePower
        // returns non-Success, so the sweep skips that target for
        // PhantomBlacklistDurationMs. Lets phantoms rotate to hittable targets
        // during scripted encounters (cutscene bosses, mid-transition mission
        // NPCs, temporary Invulnerable phases) instead of glueing to the
        // first-picked hostile forever.
        private const long PhantomBlacklistDurationMs = 3000;
        private static readonly Dictionary<(ulong phantomId, ulong targetId), long> s_phantomTargetBlacklist = new();
        private static bool IsTargetBlacklisted(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist.TryGetValue((phantomId, targetId), out long expiresAt) && nowMs < expiresAt;
        private static void BlacklistTarget(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist[(phantomId, targetId)] = nowMs + PhantomBlacklistDurationMs;
        private static void ClearTargetBlacklist(ulong phantomId, ulong targetId)
            => s_phantomTargetBlacklist.Remove((phantomId, targetId));
        private static void PruneBlacklistFor(ulong phantomId)
        {
            List<(ulong, ulong)> toRemove = null;
            foreach (var key in s_phantomTargetBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomTargetBlacklist.Remove(k);
        }

        // Roam targets, per phantom, with the time they were chosen. A phantom
        // keeps walking to the same point until it arrives or the point goes
        // stale, otherwise it would pick a new direction every tick and vibrate
        // on the spot.
        private static readonly Dictionary<ulong, (Vector3 dest, long chosenMs)> s_deathmatchRoam = new();
        private const long DeathmatchRoamRepickMs = 12_000;
        private const float DeathmatchRoamArriveDist = 250f;
        private const float DeathmatchRoamMinDist = 1500f;
        private const float DeathmatchRoamMaxDist = 4500f;

        /// <summary>
        /// Walks a Team Deathmatch combatant toward a roam point, picking a new
        /// one when it arrives, the point goes stale, or it has none. Movement
        /// only � no teleporting.
        ///
        /// <paramref name="towardPos"/>, when given, biases the pick toward that
        /// direction instead of a fully random angle. This is what lets a
        /// combatant that CAN see a distant rival (region-wide sight) but whose
        /// direct FollowEntity path failed still make real progress toward them �
        /// short hops in roughly the right direction, each one well within the
        /// navmesh pathfinder's search budget (NaviPathGenerator caps its search
        /// at 256 steps, which a single-shot path across a 9000+u open map like
        /// Savage Land can exceed outright, returning FailedNoPathFound and
        /// leaving the phantom standing still � confirmed live 2026-08-08 via
        /// FollowEntity's own path-result field: every long-range attempt failed
        /// while nearer ones later succeeded). A null bias keeps the old fully
        /// random wander, used only when no candidate exists to walk toward at all.
        /// </summary>
        private static void TryDeathmatchRoam(Agent phantom, Region region, MHServerEmu.Core.System.Random.GRandom rng, Vector3? towardPos = null)
        {
            if (phantom == null || region == null) return;

            long nowMs = phantom.Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            Vector3 pos = phantom.RegionLocation.Position;

            bool needNew = true;
            if (s_deathmatchRoam.TryGetValue(phantom.Id, out var roam))
            {
                bool arrived = Vector3.DistanceSquared2D(pos, roam.dest) <= DeathmatchRoamArriveDist * DeathmatchRoamArriveDist;
                bool stale = nowMs - roam.chosenMs > DeathmatchRoamRepickMs;
                needNew = arrived || stale;
            }

            if (needNew)
            {
                Vector3 dest = towardPos.HasValue
                    ? ChooseScatteredArenaPosBiased(region, pos, towardPos.Value, pos, rng, phantom.Bounds.Radius,
                        DeathmatchRoamMinDist, DeathmatchRoamMaxDist)
                    : ChooseScatteredArenaPos(region, pos, pos, rng, phantom.Bounds.Radius,
                        DeathmatchRoamMinDist, DeathmatchRoamMaxDist);
                s_deathmatchRoam[phantom.Id] = (dest, nowMs);
                roam = (dest, nowMs);
            }

            var loco = phantom.Locomotor;
            if (loco == null) return;

            var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(500) };
            bool pathOk = loco.PathTo(roam.dest, ref opts);

            // Throttled visibility into whether roam is actually making progress �
            // added 2026-08-08 after a "still standing still" report we could not
            // confirm or rule out from the existing one-shot FollowEntity log (it
            // only fires once ever per phantom, and had already fired during the
            // pre-match lock on the report in question). Remove once confirmed working.
            if (s_deathmatchRoamNextDiagMs.TryGetValue(phantom.Id, out long nextDiagMs) == false || nowMs >= nextDiagMs)
            {
                s_deathmatchRoamNextDiagMs[phantom.Id] = nowMs + 3000;
                PhantomLogger.Info($"[TDM:Roam] {phantom} pos={pos.ToStringNames()} dest={roam.dest.ToStringNames()} pathOk={pathOk} isMoving={loco.IsMoving} canMove={phantom.CanMove()} pathResult={loco.LastGeneratedPathResult}");
            }

            if (pathOk == false)
            {
                // Unreachable � drop it and pick a fresh one next tick rather
                // than teleporting there.
                s_deathmatchRoam.Remove(phantom.Id);
            }
        }

        private static readonly Dictionary<ulong, long> s_deathmatchRoamNextDiagMs = new();

        /// <summary>
        /// Pick a leash-teleport position near the caller that lands on the
        /// walkable navi mesh. Retries up to 6 times with fresh random
        /// angles/radii; falls back to caller position if nothing validates.
        /// Fixes the "phantom leashes into a wall/out-of-bounds corner and
        /// stays there" case that only server-restart used to unstick.
        /// </summary>
        private static Vector3 ChoosePhantomLeashPos(Region region, Vector3 callerPos, MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius)
        {
            if (region == null) return callerPos;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                // Tighter than the old 200-800 range so leashed phantoms
                // land right next to the caller instead of "somewhere on
                // this screen."
                float radius = 150f + (float)(rng.NextDouble() * 250f);
                Vector3 candidate = callerPos + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                // Same InvalidCell check the ambush-spawn candidate loop
                // needed (confirmed live 2026-07-31/08-01) -- NaviMesh.Contains
                // alone can pass a point with no backing Cell, which makes
                // ChangeRegionPosition silently no-op (no exception, so the
                // caller's try/catch never fires) while still recording the
                // never-applied position into s_phantomStuckTrack, delaying
                // the next stuck-rescue attempt by a full detection cycle.
                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck)
                    && region.GetCellAtPosition(candidate) != null)
                    return candidate;
            }
            // Fallback: caller's exact position. Guaranteed walkable since
            // the caller is standing on it.
            return callerPos;
        }

        /// <summary>
        /// Walkable point within a caller-chosen radius band, for spreading Team
        /// Deathmatch duos across the arena instead of stacking them on the
        /// player. Same navi-mesh + backing-cell validation as
        /// ChoosePhantomLeashPos (both checks are required: NaviMesh.Contains can
        /// pass a point with no Cell, and ChangeRegionPosition then silently
        /// no-ops), but with a far wider band and more attempts.
        ///
        /// Falls back to <paramref name="fallback"/> rather than an arbitrary
        /// point, so a failure puts a combatant somewhere known-walkable instead
        /// of out of bounds.
        /// </summary>
        public static Vector3 ChooseScatteredArenaPos(Region region, Vector3 origin, Vector3 fallback,
            MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius, float minRadius, float maxRadius)
        {
            if (region == null) return fallback;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            for (int attempt = 0; attempt < 24; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = minRadius + (float)(rng.NextDouble() * Math.Max(1f, maxRadius - minRadius));
                Vector3 candidate = origin + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);

                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck)
                    && region.GetCellAtPosition(candidate) != null)
                    return candidate;
            }
            return fallback;
        }

        /// <summary>
        /// Same walkable-point search as <see cref="ChooseScatteredArenaPos"/>, but
        /// the angle is drawn from a +/-50 degree cone facing <paramref name="towardPos"/>
        /// instead of the full circle � a short hop that's actually progress toward
        /// a known, far-off target rather than a random wander. See
        /// TryDeathmatchRoam's header for why this exists.
        /// </summary>
        private static Vector3 ChooseScatteredArenaPosBiased(Region region, Vector3 origin, Vector3 towardPos, Vector3 fallback,
            MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius, float minRadius, float maxRadius)
        {
            if (region == null) return fallback;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            Vector3 toTarget = towardPos - origin;
            float baseAngle = MathF.Atan2(toTarget.Y, toTarget.X);
            const float coneHalfWidth = MathF.PI * 50f / 180f;

            for (int attempt = 0; attempt < 24; attempt++)
            {
                float angle = baseAngle + ((float)(rng.NextDouble() * 2.0 - 1.0) * coneHalfWidth);
                float radius = minRadius + (float)(rng.NextDouble() * Math.Max(1f, maxRadius - minRadius));
                Vector3 candidate = origin + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);

                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck)
                    && region.GetCellAtPosition(candidate) != null)
                    return candidate;
            }
            // Cone search failed every attempt (obstruction, edge of mesh) �
            // fall back to the old fully-random search rather than freezing.
            return ChooseScatteredArenaPos(region, origin, fallback, rng, avatarRadius, minRadius, maxRadius);
        }

        /// <summary>
        /// Picks a random walkable point near <paramref name="fromPos"/> for
        /// a pure anti-clustering spacing dash � not danger-aware (no
        /// hazard/AoE detection exists for this AI to dodge with), just a
        /// random nearby spot so a squad breaks up its blob shape over time.
        /// </summary>
        private static Vector3 ChoosePhantomDashDestination(Region region, Vector3 fromPos, MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius)
        {
            if (region == null) return fromPos;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = 150f + (float)(rng.NextDouble() * 200f);
                Vector3 candidate = fromPos + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                // Same InvalidCell check as ChoosePhantomLeashPos above.
                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck)
                    && region.GetCellAtPosition(candidate) != null)
                    return candidate;
            }
            return fromPos;
        }

        /// <summary>
        /// Immediately relocates every tracked friendly phantom/team-up to a
        /// valid spot near <paramref name="newPos"/>. Called by
        /// Teleporter.TeleportToLocalTarget for same-region mission-portal
        /// area transitions � those reposition only the caller via
        /// ChangeRegionPosition and never go through
        /// BeginRegionTransfer/SnapshotPhantomsForTransfer (that's the
        /// cross-region path only). The normal 500ms leash tick would likely
        /// catch a stray phantom eventually since the caller-distance check
        /// re-reads the caller's live position every tick, but that's a
        /// same-region-instance assumption riding on ordinary leash timing �
        /// this makes the catch-up immediate and certain instead of waiting
        /// on the next tick.
        /// </summary>
        internal void BringPhantomsToPosition(Vector3 newPos, Region regionOverride = null)
        {
            Player host = PhantomHost;
            if (host == null) return;
            // this.Region can be transiently null right after a fresh
            // cross-region arrival, even though the caller (Teleporter)
            // already has a valid Region in hand from resolving the
            // teleport destination � use that instead of re-deriving a
            // possibly-stale one when the caller has it.
            Region region = regionOverride ?? Region;
            if (region == null) return;
            var rng = Game?.Random;
            if (rng == null) return;

            var ids = host.PhantomAvatarIds;
            for (int i = 0; i < ids.Count; i++)
            {
                Agent phantom = Game.EntityManager.GetEntity<Agent>(ids[i]);
                if (phantom == null || phantom.IsDestroyed || phantom.IsInWorld == false) continue;
                try
                {
                    Vector3 pos = ChoosePhantomLeashPos(region, newPos, rng, phantom.Bounds.Radius);
                    phantom.Locomotor?.Stop();
                    phantom.ChangeRegionPosition(pos, null);
                }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] BringPhantomsToPosition failed for {phantom.Id:X}: {ex.Message}"); }
            }
        }

        private static readonly HashSet<ulong> s_phantomLocoLogged = new();

        /// <summary>
        /// True if this phantom's kit has a genuine (TargetingReach.Melee-
        /// flagged) power ready � same real, data-driven check
        /// ComputePhantomFollowStopDist already uses to decide combat
        /// standoff, reused here to decide frontline-vs-backline formation
        /// placement while idle.
        /// </summary>
        private static bool IsPhantomMeleeKit(Agent phantom)
        {
            var pc = phantom.PowerCollection;
            if (pc == null) return false;
            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled) continue;
                if (pp.IsTravelPower) continue;
                if (Power.IsMelee(pp)) return true;
            }
            return false;
        }

        /// <summary>
        /// Per-phantom preferred idle slot around the caller. Each phantom
        /// gets a personal (angle, distance) derived from a hash of its
        /// runtime id: because the angle is unique per phantom, they never
        /// converge on the same follow spot � no stacking. Distance is
        /// frontline/backline-biased on top of that per-phantom variance �
        /// a standard MMO/ARPG companion-AI formation pattern: melee-kit
        /// phantoms hold a closer slot (first into a fight), ranged-kit
        /// phantoms hold further back � instead of every phantom sharing
        /// one uniform ring regardless of role. Slot is always computed
        /// against the caller's current position so it tracks as the caller
        /// walks around.
        /// </summary>
        private Vector3 ComputePhantomIdleSlot(Agent phantom, Region region)
        {
            Vector3 callerPos = RegionLocation.Position;
            ulong phantomId = phantom.Id;

            // Hash-mix the id so consecutive phantom ids don't produce
            // near-identical slots. Constants are arbitrary large primes.
            ulong h = phantomId * 2654435761UL ^ (phantomId >> 16);
            float angle = ((h & 0xFFFF) / 65535f) * MathF.PI * 2f;              // 0 .. 2p

            bool frontline = IsPhantomMeleeKit(phantom);
            float baseDist = frontline ? PhantomIdleFollowStopDist * 0.6f : PhantomIdleFollowStopDist * 1.3f;
            float dist = baseDist + (((h >> 16) & 0xFF) / 255f - 0.5f) * 140f;

            Vector3 slot = callerPos + new Vector3(MathF.Cos(angle) * dist,
                                                    MathF.Sin(angle) * dist, 0f);
            return region != null ? RegionLocation.ProjectToFloor(region, slot) : slot;
        }

        /// <summary>
        /// Walk (or Stop) a phantom toward its personal idle-formation slot
        /// around the caller. Shared between avatar phantoms (called from
        /// UpdatePhantomHunt's no-target branch) and team-up phantoms (called
        /// from the tick loop when no hostile is in range).
        /// </summary>
        private void ApplyPhantomIdleFormation(Agent phantom, Vector3 callerPos)
        {
            var loco = phantom.Locomotor;
            if (loco == null) return;
            Region region = phantom.Region ?? Region;
            Vector3 slotPos = ComputePhantomIdleSlot(phantom, region);
            float slotDistSq = Vector3.DistanceSquared2D(phantom.RegionLocation.Position, slotPos);
            if (slotDistSq > PhantomFormationArriveDist * PhantomFormationArriveDist)
            {
                var idleOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(400) };
                loco.PathTo(slotPos, ref idleOpts);
            }
            else
            {
                loco.Stop();
            }
        }

        /// <summary>
        /// Same revive-priority behavior avatar phantoms have, but driven from
        /// the team-up code path (team-ups skip UpdatePhantomHunt). Sweeps for
        /// a downed real player OR friendly phantom nearby; walks toward them
        /// or fires the resurrect power granted at spawn. Returns true if the
        /// team-up is now committed to a revive (caller should skip idle
        /// formation for this tick).
        /// </summary>
        private bool TryTeamUpReviveDowned(Agent teamUp)
        {
            Region region = teamUp.Region;
            if (region == null) return false;
            // Skip if the team-up has no resurrect power (enemy team-ups
            // deliberately never learn it).
            PrototypeId resurrectPowerRef = AvatarPrototype?.ResurrectOtherEntityPower ?? PrototypeId.Invalid;
            if (resurrectPowerRef == PrototypeId.Invalid) return false;
            if (teamUp.GetPower(resurrectPowerRef) == null) return false;

            Vector3 teamUpPos = teamUp.RegionLocation.Position;
            Agent downed = null;
            float downedDistSq = float.MaxValue;

            // Direct-check the caller first (matches avatar phantom revive path).
            if (this.IsDead && this.IsInWorld && this.Region == region)
            {
                downed = this;
                downedDistSq = Vector3.DistanceSquared(this.RegionLocation.Position, teamUpPos);
            }
            else
            {
                // Direct roster scan � unlimited range, same fix as the
                // avatar-phantom revive path (see UpdatePhantomHunt): a
                // downed squadmate outside the 4000u sweep, or separated
                // mainly by elevation under the old 2D distance calc, was
                // otherwise invisible to every team-up too.
                Player rosterHost = this.PhantomHost;
                if (rosterHost != null)
                {
                    var rosterIds = rosterHost.PhantomAvatarIds;
                    for (int ri = 0; ri < rosterIds.Count; ri++)
                    {
                        ulong avId = rosterIds[ri];
                        if (avId == teamUp.Id) continue;
                        Agent candidate = Game.EntityManager.GetEntity<Agent>(avId);
                        if (candidate == null || candidate.IsDead == false || candidate.IsInWorld == false || candidate.Region != region) continue;

                        float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, teamUpPos);
                        if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                    }
                }

                var sphere = new Sphere(teamUpPos, PhantomReviveSearchRange);
                var ctx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);
                foreach (WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
                {
                    if (we is not Agent candidate) continue;
                    if (candidate.Id == teamUp.Id) continue;
                    if (candidate.IsDead == false) continue;
                    Player candOwner = candidate.GetOwnerOfType<Player>();
                    if (candOwner == null || candOwner.PlayerConnection == null) continue;
                    float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, teamUpPos);
                    if (d > PhantomReviveSearchRangeSq) continue;
                    if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                }
            }

            if (downed == null)
            {
                // Nothing to revive right now � make sure the native brain
                // is following the caller again (see below), not still
                // pointed at a target from a previous revive attempt.
                RestoreTeamUpAssistedEntity(teamUp);
                return false;
            }

            // Claim the downed target � root cause of "multiple phantoms/team-ups
            // revive the same downed ally at once" (reported 2026-07-21): this
            // path picks the same nearest-downed-ally logic as the avatar-phantom
            // Hunt path but never consulted s_phantomReviveClaim, so a team-up
            // and an avatar phantom (or two team-ups) could both lock onto and
            // cast on the same target with zero coordination between them.
            {
                long nowMsRevive = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (s_phantomReviveClaim.TryGetValue(downed.Id, out var claim)
                    && claim.claimantId != teamUp.Id
                    && nowMsRevive - claim.claimedAtMs < PhantomReviveClaimTimeoutMs
                    && nowMsRevive - claim.firstClaimedAtMs < PhantomReviveClaimMaxHoldMs)
                {
                    PhantomLogger.Info($"[PhantomHero:ReviveClaim] {teamUp} backed off downed {downed} � already claimed by {claim.claimantId:X}");
                    RestoreTeamUpAssistedEntity(teamUp);
                    return false;
                }

                long firstClaimedAtMs = (claim.claimantId == teamUp.Id) ? claim.firstClaimedAtMs : nowMsRevive;
                s_phantomReviveClaim[downed.Id] = (teamUp.Id, nowMsRevive, firstClaimedAtMs);
            }

            // Guard against re-casting on someone already being resurrected.
            if (teamUp.Properties[PropertyEnum.PendingResurrectEntityId] == downed.Id) return true;

            if (downedDistSq > GetReviveCastRangeSq(teamUp, resurrectPowerRef))
            {
                // The team-up's native AIController re-issues its own
                // MoveToType.AssistedEntity follow (toward the caller) every
                // ~100ms � faster than our 500ms tick � so a plain
                // Locomotor.FollowEntity call here toward the downed target
                // kept losing that race and the team-up never actually
                // reached cast range. Instead of fighting the native brain,
                // redirect what IT thinks its assisted entity is to the
                // downed target, so its own faster follow logic drives it
                // there cooperatively. Restored back to the caller once the
                // revive resolves (or no longer applies).
                var controller = teamUp.AIController;
                if (controller?.Blackboard?.PropertyCollection != null)
                    controller.Blackboard.PropertyCollection[PropertyEnum.AIAssistedEntityID] = downed.Id;

                var loco = teamUp.Locomotor;
                if (loco != null)
                {
                    var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                    loco.FollowEntity(downed.Id, 50f, 50f, ref opts, false);
                }
                return true;
            }

            try
            {
                // Chain-revive fix: without clearing cooldown here, the
                // first successful revive puts the resurrect power on
                // cooldown for this team-up and it can't help with the
                // rest of a wiped squad until it expires.
                teamUp.Properties.RemoveProperty(new(PropertyEnum.PowerCooldownStartTime, resurrectPowerRef));
                teamUp.Properties.RemoveProperty(new(PropertyEnum.PowerCooldownDuration, resurrectPowerRef));

                var settings = new PowerActivationSettings(downed.Id, downed.RegionLocation.Position, teamUpPos);
                settings.Flags |= PowerActivationSettingsFlags.NotifyOwner;
                var reviveResult = teamUp.ActivatePower(resurrectPowerRef, ref settings);
                if (reviveResult == PowerUseResult.Success)
                {
                    teamUp.Properties[PropertyEnum.PendingResurrectEntityId] = downed.Id;
                    // Same race fix as the avatar-phantom revive path above �
                    // don't release the claim until IsDead is confirmed false;
                    // let the 6s-inactivity timeout clean up the now-stale
                    // claim naturally once nothing considers this target
                    // "downed" anymore.
                }
                else
                {
                    PhantomLogger.Info($"[PhantomHero:TeamUp:Revive] {teamUp} -> {downed} rejected: {reviveResult}");
                    if (reviveResult == PowerUseResult.RestrictiveCondition)
                    {
                        PhantomLogger.Info($"[PhantomHero:ReviveBlocked] {teamUp} knockback={teamUp.IsInKnockback} " +
                            $"knockdown={teamUp.IsInKnockdown} knockup={teamUp.IsInKnockup} stunned={teamUp.IsStunned} " +
                            $"mesmerized={teamUp.IsMesmerized} npcAmbientLock={teamUp.NPCAmbientLock} " +
                            $"powerLock={teamUp.IsInPowerLock} aiControlPowerLock={teamUp.HasAIControlPowerLock} " +
                            $"tutorialPowerLock={teamUp.IsInTutorialPowerLock}");

                        // Reverted (2026-07-21) � see the matching avatar-phantom revive
                        // path's comment: live data showed these failures are almost
                        // always stunned=True (ongoing combat interruption, expected),
                        // not a stuck claimant. Releasing here caused the claim to churn
                        // between phantoms every ~0.5-1s, which is what looked like
                        // "everyone's trying to revive the same target." Keep this same
                        // claimant stable � it re-claims every tick on its own since it's
                        // still the closest � and let it succeed once un-stunned, or fall
                        // back to the existing 6s/20s timeout if it's truly stuck.
                    }
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] ActivatePower(ResurrectOther) failed: {ex.Message}"); }
            finally { RestoreTeamUpAssistedEntity(teamUp); }
            return true;
        }

        /// <summary>
        /// Points the team-up's native AIController back at the caller as
        /// its assisted entity � the normal "stick near your owner" state �
        /// after a revive attempt (successful, failed, or no longer needed)
        /// temporarily redirected it at a downed target instead.
        /// </summary>
        private void RestoreTeamUpAssistedEntity(Agent teamUp)
        {
            var controller = teamUp.AIController;
            if (controller?.Blackboard?.PropertyCollection == null) return;
            if (controller.Blackboard.PropertyCollection[PropertyEnum.AIAssistedEntityID] != Id)
                controller.Blackboard.PropertyCollection[PropertyEnum.AIAssistedEntityID] = Id;
        }

        /// <summary>
        /// Drop everything the phantom is currently wearing as ground loot for
        /// the killer. Runs on the first tick after death (before the 4-second
        /// corpse timer + Destroy). Uses the EXACT ItemSpec stored on each
        /// equipped Item entity � the drops preserve every affix that was
        /// rolled at spawn, and follow the same level-band tier the phantom
        /// wears (Cosmic at level 60, Rare at level 30, etc � whatever
        /// ApplyPhantomGear rolled).
        ///
        /// Team-up phantoms are skipped � they don't wear the avatar
        /// equipment inventories, and giving them a drop table is a design
        /// choice we haven't made yet.
        /// </summary>
        // Default number of items in a rank-5 sub-60 loot-splosion.
        private const int LootSplosionCount = 10;

        private static void DropPhantomGear(Agent phantom, Player killer)
        {
            if (phantom == null || killer == null) return;
            if (phantom.Prototype is not AvatarPrototype avatarProto) return;
            if (avatarProto.EquipmentInventories == null) return;

            Game game = phantom.Game;
            var lootMgr = game?.LootManager;
            if (lootMgr == null) return;

            // Nemesis rank/level drives the loot tier. Plain rogues (not in the
            // dict) are rank 0 -> just drop worn gear.
            int rank = 0, level = phantom.CharacterLevel;
            if (s_enemyPhantomRankLevel.TryGetValue(phantom.Id, out var rl)) { rank = rl.rank; level = rl.level; }

            using var inputSettingsHandle = Loot.LootInputSettingsPool.Get(out Loot.LootInputSettings inputSettings);
            inputSettings.Initialize(Loot.LootContext.Drop, killer, phantom);
            using var summaryHandle = Loot.LootResultSummaryPool.Get(out Loot.LootResultSummary summary);

            var rng = game.Random;

            // Rank 5 below level 60: skip the worn-gear drop entirely and
            // explode a pile of random loot instead. 20-59 = terminal-boss
            // tier (top of the level band); below 20 = plain level-appropriate.
            if (rank >= Player.NemesisMaxRank && level < 60)
            {
                int rolled = RollSplosionInto(summary, avatarProto, killer, lootMgr, rng, level, LootSplosionCount);
                if (rolled > 0)
                {
                    lootMgr.SpawnLootFromSummary(summary, inputSettings);
                    PhantomLogger.Info($"[PhantomHero:Loot] rank-5 lvl-{level} loot-splosion: {rolled} item(s) from '{avatarProto.DataRef.GetName()}' (killer={killer.GetName()})");
                }
                return;
            }

            // Costume slot: gated behind a rank-based chance instead of an
            // unconditional drop, independent of whatever the gear roll
            // below does � costumes are collectible/cosmetic so they
            // shouldn't fall off of every single rogue kill.
            int costumeDropped = RollPhantomCostumeDrop(phantom, avatarProto, rank, rng, summary);

            // Rank 5 at level 60: this is the only case where the worn gear
            // IS the full BiS loadout (see ApplyPhantomGear's bisLoadout
            // param at spawn). No rank should have a guaranteed 100% BiS
            // drop, so this is no longer an unconditional dump of every
            // slot � instead:
            //   5%  -> SUPER loot-splosion: every worn piece drops, guaranteed.
            //   25% -> up to 3 random worn BiS pieces drop.
            //   70% (roll misses both) -> no BiS at all; a random
            //         level-appropriate gear splosion drops instead so the
            //         kill still feels worthwhile.
            if (rank >= Player.NemesisMaxRank && level >= 60)
            {
                var wornItems = new List<Items.Item>();
                foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
                {
                    InventoryPrototype invProto = assignment.Inventory;
                    if (invProto != null && invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;
                    Inventory inv = phantom.GetInventoryByRef(assignment.Inventory.DataRef);
                    if (inv == null) continue;
                    foreach (var entry in inv)
                    {
                        Items.Item item = game.EntityManager.GetEntity<Items.Item>(entry.Id);
                        if (item?.ItemSpec != null) wornItems.Add(item);
                    }
                }

                int dropped = 0;
                float roll = rng.NextFloat();
                if (roll < 0.05f)
                {
                    dropped = AddWornItemsInto(summary, wornItems, wornItems.Count, rng);
                    if (dropped > 0)
                        PhantomLogger.Info($"[PhantomHero:Loot] rank-5 SUPER loot-splosion: all {dropped} worn BiS piece(s) from '{avatarProto.DataRef.GetName()}' (killer={killer.GetName()})");
                }
                else if (roll < 0.30f)
                {
                    dropped = AddWornItemsInto(summary, wornItems, Math.Min(3, wornItems.Count), rng);
                    if (dropped > 0)
                        PhantomLogger.Info($"[PhantomHero:Loot] rank-5 partial BiS drop: {dropped} worn piece(s) from '{avatarProto.DataRef.GetName()}' (killer={killer.GetName()})");
                }
                else
                {
                    dropped = RollSplosionInto(summary, avatarProto, killer, lootMgr, rng, level, LootSplosionCount);
                    if (dropped > 0)
                        PhantomLogger.Info($"[PhantomHero:Loot] rank-5 BiS roll missed � random lvl-{level} splosion: {dropped} item(s) from '{avatarProto.DataRef.GetName()}' (killer={killer.GetName()})");
                }

                if (dropped + costumeDropped > 0)
                    lootMgr.SpawnLootFromSummary(summary, inputSettings);
                return;
            }

            // Everyone else (ranks 0-4): drop worn gear (random level-band
            // gear they were rolled with, not BiS) plus the rank 3/4 bonus.
            int wornDropped = 0;
            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                InventoryPrototype invProto = assignment.Inventory;
                if (invProto != null && invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue; // handled above
                Inventory inv = phantom.GetInventoryByRef(assignment.Inventory.DataRef);
                if (inv == null) continue;

                foreach (var entry in inv)
                {
                    Items.Item item = game.EntityManager.GetEntity<Items.Item>(entry.Id);
                    if (item?.ItemSpec == null) continue;
                    // Equipped items bind on equip; clear the binding affix so
                    // the ground drop is pickupable (see OnPickupInteraction).
                    try { item.ItemSpec.SetBindingState(false); }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] unbind failed on {item.Id:X}: {ex.Message}"); }
                    summary.Add(new Loot.LootResult(item.ItemSpec));
                    wornDropped++;
                }
            }

            // Rank 3/4 bonus: add down-tier BiS items on top of the worn drop.
            //   Rank 3 -> ~50% chance of 1 down-tier BiS item.
            //   Rank 4 -> 0, 1, or 2 down-tier BiS items (uniform).
            int bonus = 0;
            if ((rank == 3 || rank == 4) && PhantomBiSData.TryGetLoadout(avatarProto.DataRef, game, out var bisLoadout) && bisLoadout.Count > 0)
            {
                int want = rank == 3 ? (rng.NextFloat() < 0.5f ? 1 : 0) : rng.Next(0, 3);
                bonus = AddDownTierBiSInto(summary, bisLoadout, avatarProto, killer, lootMgr, rng, level, want);
            }

            // Baseline splosion bonus, scaling with rank -- worn gear alone can
            // be as few as 1-2 pieces (low character level = few unlocked
            // equip slots, or a costume-heavy loadout), which felt underwhelming
            // for an ambush kill. Guaranteed random level-band items on top,
            // growing with rank so higher-rank encounters feel meaningfully
            // more rewarding than a plain rank-0 rogue.
            int splosionWant = rank switch
            {
                0 => 2,
                1 => 3,
                2 => 4,
                3 => 5,
                4 => 6,
                _ => 0
            };
            int splosion = splosionWant > 0 ? RollSplosionInto(summary, avatarProto, killer, lootMgr, rng, level, splosionWant) : 0;

            if (wornDropped + bonus + splosion + costumeDropped > 0)
            {
                lootMgr.SpawnLootFromSummary(summary, inputSettings);
                PhantomLogger.Info($"[PhantomHero:Loot] dropped {wornDropped} worn + {bonus} down-tier BiS + {splosion} splosion item(s) (rank {rank}) from '{avatarProto.DataRef.GetName()}' (killer={killer.GetName()})");
            }
        }

        /// <summary>
        /// Rolls the rank-based costume drop chance (6% ranks 0-3, 15% rank
        /// 4, 20% rank 5) and adds the phantom's equipped costume item to
        /// the summary if it hits. Returns 1 if a costume was added, 0
        /// otherwise. Independent of whatever the main gear roll does.
        /// </summary>
        private static int RollPhantomCostumeDrop(Agent phantom, AvatarPrototype avatarProto, int rank,
            MHServerEmu.Core.System.Random.GRandom rng, Loot.LootResultSummary summary)
        {
            float costumeDropChance = rank >= Player.NemesisMaxRank ? 0.20f : rank == 4 ? 0.15f : 0.06f;
            if (rng.NextFloat() >= costumeDropChance) return 0;

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                InventoryPrototype invProto = assignment.Inventory;
                if (invProto == null || invProto.ConvenienceLabel != InventoryConvenienceLabel.Costume) continue;

                Inventory inv = phantom.GetInventoryByRef(assignment.Inventory.DataRef);
                if (inv == null) return 0;
                foreach (var entry in inv)
                {
                    Items.Item item = phantom.Game.EntityManager.GetEntity<Items.Item>(entry.Id);
                    if (item?.ItemSpec == null) continue;
                    try { item.ItemSpec.SetBindingState(false); }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] costume unbind failed on {item.Id:X}: {ex.Message}"); }
                    summary.Add(new Loot.LootResult(item.ItemSpec));
                    return 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// Adds <paramref name="count"/> randomly-selected items from
        /// <paramref name="wornItems"/> into the summary (Fisher-Yates
        /// partial shuffle so the same item can't be picked twice), clearing
        /// each one's binding affix so the ground drop is pickupable.
        /// </summary>
        private static int AddWornItemsInto(Loot.LootResultSummary summary, List<Items.Item> wornItems, int count,
            MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (count <= 0 || wornItems.Count == 0) return 0;
            count = Math.Min(count, wornItems.Count);

            // Fisher-Yates partial shuffle in place � fine here since
            // wornItems is a throwaway list built fresh for this drop.
            for (int i = wornItems.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                (wornItems[i], wornItems[j]) = (wornItems[j], wornItems[i]);
            }

            int added = 0;
            for (int i = 0; i < count; i++)
            {
                Items.Item item = wornItems[i];
                try { item.ItemSpec.SetBindingState(false); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] worn-item unbind failed on {item.Id:X}: {ex.Message}"); }
                summary.Add(new Loot.LootResult(item.ItemSpec));
                added++;
            }
            return added;
        }

        /// <summary>
        /// Roll <paramref name="count"/> random items across the avatar's equip
        /// slots and add them to the drop summary. Used for rank-5 sub-60
        /// loot-splosions. Level =20 pulls the top of the level band
        /// (terminal-boss feel); below 20 uses the plain level band.
        /// </summary>
        private static int RollSplosionInto(Loot.LootResultSummary summary, AvatarPrototype avatarProto,
            Player killer, LootManager lootMgr, MHServerEmu.Core.System.Random.GRandom rng, int level, int count)
        {
            EnsureRarityTiers();
            s_rarityByTier.TryGetValue(5, out PrototypeId bannedUltimateRef);
            List<PrototypeId> band = GetPhantomGearAllowedRarities(level);

            // Collect the equip slots we can roll from.
            var slots = new List<EquipmentInvUISlot>();
            foreach (var assignment in avatarProto.EquipmentInventories)
            {
                if (assignment.UnlocksAtCharacterLevel > level) continue;
                var uiSlot = assignment.UISlot;
                bool core = uiSlot >= EquipmentInvUISlot.Gear01 && uiSlot <= EquipmentInvUISlot.Gear05;
                bool special = uiSlot is EquipmentInvUISlot.Artifact01 or EquipmentInvUISlot.Artifact02
                    or EquipmentInvUISlot.Artifact03 or EquipmentInvUISlot.Artifact04 or EquipmentInvUISlot.Medal
                    or EquipmentInvUISlot.Relic or EquipmentInvUISlot.Insignia or EquipmentInvUISlot.Ring
                    or EquipmentInvUISlot.Legendary or EquipmentInvUISlot.UruForged;
                if (core || special) slots.Add(uiSlot);
            }
            if (slots.Count == 0) return 0;

            int added = 0;
            for (int i = 0; i < count; i++)
            {
                EquipmentInvUISlot uiSlot = slots[rng.Next(0, slots.Count)];
                var picker = new MHServerEmu.Core.Collections.Picker<Prototype>(rng);
                LootUtilities.BuildInventoryLootPicker(picker, avatarProto.DataRef, uiSlot);
                ItemSpec spec = null;
                while (spec == null && picker.Empty() == false)
                {
                    if (picker.PickRemove(out Prototype proto) == false || proto == null) break;
                    // The picker above was built from avatarProto's own equipment inventory
                    // (the phantom's, not the killer's), so some candidates may be exclusive
                    // to that avatar (e.g. a boss's signature gear). Resolve the item spec
                    // against that same avatar or GetInventorySlotForAgent comes back Invalid
                    // for the killer and affix generation fails outright.
                    var s = lootMgr.CreateItemSpec(proto.DataRef, LootContext.Drop, killer, level, rollForAvatarProtoOverride: avatarProto);
                    if (s == null) continue;
                    if (bannedUltimateRef != PrototypeId.Invalid && s.RarityProtoRef == bannedUltimateRef) continue;
                    spec = s;
                }
                if (spec != null)
                {
                    try { spec.SetBindingState(false); }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] splosion unbind failed: {ex.Message}"); }
                    summary.Add(new Loot.LootResult(spec));
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// Pick <paramref name="want"/> random slots from the hero's BiS loadout
        /// and add each item at ONE rarity tier below its level-band top (the
        /// "down-tier" version). Used for the rank 3/4 bonus drop.
        /// </summary>
        private static int AddDownTierBiSInto(Loot.LootResultSummary summary,
            IReadOnlyDictionary<EquipmentInvUISlot, PrototypeId> bisLoadout, AvatarPrototype avatarProto,
            Player killer, LootManager lootMgr, MHServerEmu.Core.System.Random.GRandom rng, int level, int want)
        {
            if (want <= 0) return 0;
            PrototypeId downTierRarity = GetDownTierRarity(level);

            var keys = new List<EquipmentInvUISlot>(bisLoadout.Keys);
            // Fisher-Yates partial shuffle so we pick distinct random slots.
            for (int i = keys.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }

            int added = 0;
            for (int i = 0; i < keys.Count && added < want; i++)
            {
                PrototypeId itemRef = bisLoadout[keys[i]];
                if (itemRef == PrototypeId.Invalid) continue;
                // Build the BiS item at the down-tier rarity; if the item can't
                // exist at that rarity, fall back to its natural roll so the
                // drop still lands.
                // bisLoadout is keyed to avatarProto's own gear (see PhantomBiSData.TryGetLoadout
                // above), so this needs the same rollForAvatarProtoOverride as RollSplosionInto --
                // otherwise avatar-exclusive BiS pieces fail affix generation against the killer.
                ItemSpec spec = lootMgr.CreateItemSpec(itemRef, LootContext.Drop, killer, level, downTierRarity, avatarProto)
                             ?? lootMgr.CreateItemSpec(itemRef, LootContext.Drop, killer, level, rollForAvatarProtoOverride: avatarProto);
                if (spec == null) continue;
                try { spec.SetBindingState(false); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Loot] down-tier unbind failed: {ex.Message}"); }
                summary.Add(new Loot.LootResult(spec));
                added++;
            }
            return added;
        }

        /// <summary>
        /// The rarity one tier below the top of the level band � the "down-tier"
        /// rarity for rank 3/4 bonus drops. Falls back to the band top if there
        /// is no lower tier.
        /// </summary>
        private static PrototypeId GetDownTierRarity(int level)
        {
            List<PrototypeId> band = GetPhantomGearAllowedRarities(level);
            if (band.Count == 0) return PrototypeId.Invalid;
            EnsureRarityTiers();
            // Find the highest tier present in the band, then step one down.
            int topTier = 0;
            foreach (var kvp in s_rarityByTier)
                if (band.Contains(kvp.Value) && kvp.Key > topTier) topTier = kvp.Key;
            for (int t = topTier - 1; t >= 1; t--)
                if (s_rarityByTier.TryGetValue(t, out PrototypeId r) && r != PrototypeId.Invalid)
                    return r;
            return band[0];
        }

        /// <summary>
        /// Cheap check: does THIS team-up have a valid hostile to engage?
        /// Used to gate team-up idle formation so we don't fight the
        /// team-up's native AI when it's actively pursuing or attacking a
        /// target.
        ///
        /// Mirrors UpdatePhantomHunt's own friendly-mode candidate filter
        /// exactly, since team-ups are otherwise "the same" as avatar
        /// phantoms (party-member fake players): a wide PhantomSearchRange
        /// sweep centered on the phantom itself, narrowed to only hostiles
        /// within PhantomFriendlyEngageMaxCallerDist of the CALLER. Previously
        /// this was centered on the caller's position for the whole check,
        /// which meant one hostile anywhere within engage range of the HUMAN
        /// disabled idle formation for the ENTIRE squad at once � every idle
        /// team-up fell back to the same native-AI follow point and stacked
        /// on top of each other while just walking around.
        /// </summary>
        private bool HasHostileNearCaller(Agent phantom, Vector3 callerPos)
        {
            Region region = phantom.Region ?? Region;
            if (region == null) return false;
            Vector3 phantomPos = phantom.RegionLocation.Position;
            var sphere = new Sphere(phantomPos, PhantomSearchRange);
            foreach (var we in region.IterateEntitiesInVolume(sphere, new(EntityRegionSPContextFlags.PrimaryPartition)))
            {
                if (we == null || we.IsInWorld == false || we.IsDead) continue;
                if (we is not Agent) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable) continue;

                // Same caller-distance filter avatar phantoms use � only a
                // hostile close to the CALLER counts as something worth
                // breaking formation for.
                float callerDistSq = Vector3.DistanceSquared2D(we.RegionLocation.Position, callerPos);
                if (callerDistSq > PhantomFriendlyEngageMaxCallerDistSq) continue;

                return true;
            }
            return false;
        }

        // ================================================================
        //  Phantom damage-scaling curve
        //
        //  Real avatars pick up damage the same way from levels 1 -> 60:
        //  a level curve on the base power damage (already baked into
        //  each PowerPrototype) PLUS gear-scaling from DamageRating.
        //  Phantoms have no gear, so we synthesise the "gear" side by
        //  interpolating three properties along the level track:
        //
        //    DamageMult      1.5   ->  3.0   (final-damage multiplier)
        //    DamagePctBonus  0.2   ->  1.5   (percent bonus)
        //    DamageRating    0     ->  5000  (feeds combat-globals curve;
        //                                     ~100 rating � 10% damage,
        //                                     5000 � a fully-BiS endgame
        //                                     avatar)
        //
        //  Tuning runs on t = ((level - 1) / 59)^2 � QUADRATIC, not linear.
        //  Playtesting showed linear scaling made phantoms hit too hard
        //  through the story levels (1-30): at level 30 linear-t was 0.49,
        //  handing out half the endgame bonus while mobs still have
        //  story-tier health pools. Squaring t keeps the ramp shallow
        //  early (t=0.24 at level 30, t=0.06 at level 15) and steep into
        //  endgame, where mob health scales up to meet it. Level 1 and
        //  level 60 anchors are unaffected.
        //
        //  Clamped to [0,1] so a level-lock override (e.g. `!phantom
        //  spawn 4 45`) still gets the level-45 damage anchors and
        //  doesn't stay at spawn-time values while the human levels past
        //  it.
        //
        //  If you want phantoms to hit harder / softer, adjust the six
        //  anchor constants � the interpolation and call sites don't
        //  need to change.
        // ================================================================
        // Rebalanced after gear landed: rolled equipment now provides real
        // affix stats, so the synthetic curve only needs to cover the gap
        // between "AI that never dodges or optimizes" and a live player �
        // not simulate an entire BiS loadout. The old anchors (up to 3.0x /
        // +150% / 5000 rating) double-dipped with gear affixes and made
        // phantoms shred everything from level 1 to 60.
        // Damage curve � friendly phantoms stay at the "helpful teammate"
        // anchor. Enemy phantoms use a separate, higher-anchored curve so
        // rogue encounters actually threaten a geared 60. Nemesis rank
        // multiplies on top of the ENEMY curve, not the friendly one.
        private const float PhantomDmgMultLvl1  = 1.0f;
        private const float PhantomDmgMultLvl60 = 1.6f;
        private const float PhantomDmgPctBonusLvl1  = 0.0f;
        private const float PhantomDmgPctBonusLvl60 = 0.4f;
        private const float PhantomDmgRatingLvl1  = 0f;
        private const float PhantomDmgRatingLvl60 = 1200f;

        // Enemy-phantom damage curve (Rogue Encounter, Wave Director,
        // Enemy Phantoms tool). Only mildly higher than the friendly curve
        // � the challenge should come from HP + kit variety + rank scaling,
        // NOT from base damage numbers so high they one-shot the player on
        // spawn. Previous 2.5�/0.75/1600 was well into "delete you on
        // ultimate" territory.
        private const float EnemyPhantomDmgMultLvl1  = 1.0f;
        private const float EnemyPhantomDmgMultLvl60 = 1.7f;
        private const float EnemyPhantomDmgPctBonusLvl1  = 0.0f;
        private const float EnemyPhantomDmgPctBonusLvl60 = 0.45f;
        private const float EnemyPhantomDmgRatingLvl1  = 0f;
        private const float EnemyPhantomDmgRatingLvl60 = 1250f;

        // Deathmatch PvP damage curve � applied to every combatant in the
        // mode, ally or opponent, instead of either curve above. Deathmatch
        // opponents were being spawned through SpawnEnemyPhantomHero and so
        // inherited the enemy/rogue curve above, which was tuned for one
        // human fighting one solo rogue threat, not for facing 2-4
        // simultaneous PvP combatants at once. Deliberately at the LOW end
        // (below even the friendly curve): a fair fight needs headroom for
        // real gear and skill to matter, not a phantom that already hits
        // like a geared endgame rogue on top of everything else the
        // aggregate DamageMult picks up (see ApplyPhantomDamageScaling's
        // deathmatch branch and the SpawnPhantomHeroCore MiniBoss-rank-tag
        // skip below for the other half of this fix).
        private const float DeathmatchPhantomDmgMultLvl1  = 1.0f;
        private const float DeathmatchPhantomDmgMultLvl60 = 1.35f;
        private const float DeathmatchPhantomDmgPctBonusLvl1  = 0.0f;
        private const float DeathmatchPhantomDmgPctBonusLvl60 = 0.25f;
        private const float DeathmatchPhantomDmgRatingLvl1  = 0f;
        private const float DeathmatchPhantomDmgRatingLvl60 = 900f;

        // Follow-stop bounds. 50u = "on top of the target" (old behaviour),
        // 1000u = a comfortable ranged-cast distance well inside the widest
        // player-attack ranges (~1400u for artillery-tier abilities). If a
        // phantom's collection has no usable ranged option we fall back to
        // PhantomFollowStopMin � melee heroes get closed distance the same
        // way real players do.
        private const float PhantomFollowStopMin = 50f;
        private const float PhantomFollowStopMax = 1000f;
        // Margin subtracted from the picked power's range so the phantom
        // stops just inside effective range rather than exactly at the edge
        // (where the target moving away one tick would kick the shot out).
        private const float PhantomFollowRangeMargin = 100f;

        /// <summary>
        /// Estimates a power's per-activation base damage for AI ranking.
        /// </summary>
        /// <remarks>
        /// Reads the same inputs <see cref="Powers.PowerPayload"/>'s
        /// CalculateInitialDamage uses, so the AI's notion of "big hit"
        /// matches what the power will actually deal:
        /// DamageBase + DamageBaseBonus + DamageBasePerLevel * CombatLevel,
        /// summed over the three real damage types.
        ///
        /// DamageBase/DamageBasePerLevel are CURVE properties indexed by
        /// PowerRank. PropertyCollection.UpdateCurvePropertyValue resolves
        /// the curve at the index and writes the result into the base store,
        /// so a plain read here returns the already-rank-resolved value �
        /// verified, not assumed.
        ///
        /// Returns 0 for powers that deal damage indirectly (conditions,
        /// summons, procs) rather than via DamageBase. Callers must treat 0
        /// as "unknown", NOT as "harmless" � see the scoring block in
        /// TryPhantomAttack, which keeps such powers selectable instead of
        /// dropping them from the rotation.
        /// </remarks>
        private static float EstimatePhantomPowerDamage(Power power, int combatLevel)
        {
            if (power == null) return 0f;
            PropertyCollection props = power.Properties;
            if (props == null) return 0f;

            float bonus = props[PropertyEnum.DamageBaseBonus];
            float total = 0f;

            for (int damageType = 0; damageType < (int)DamageType.NumDamageTypes; damageType++)
            {
                float baseDamage = props[PropertyEnum.DamageBase, damageType];
                baseDamage += (float)props[PropertyEnum.DamageBasePerLevel, damageType] * combatLevel;

                // Only count damage types this power actually uses, so the
                // flat bonus isn't multiplied across the two unused types.
                if (baseDamage > 0f)
                    total += baseDamage + bonus;
            }

            return total;
        }

        /// <summary>
        /// Drops per-phantom AI state for a despawned phantom.
        /// </summary>
        /// <remarks>
        /// These trackers are process-wide statics keyed by entity id, so
        /// without this every phantom that ever spawned would leave residue
        /// behind. Called from every phantom-removal path alongside the
        /// existing PruneBlacklistFor / PrunePowerBlacklistFor cleanup.
        ///
        /// Squad focus is keyed by HOST id rather than phantom id, so it is
        /// deliberately not cleared here � it expires on its own (5s TTL) and
        /// clearing it when one squadmate dies would drop the whole squad's
        /// focus target mid-fight.
        /// </remarks>
        // ---- Combat strafe -------------------------------------------------
        //
        // A short sidestep during the gap between attacks. Perpendicular to the
        // target direction (with a random sign and a little arc wobble) so the
        // distance to the target stays roughly constant � this is repositioning,
        // not disengaging, and must never fight the kite/standoff logic.
        // Navmesh-validated exactly like the kite step.
        private const long PhantomStrafeCooldownMs = 3500;
        private const long PhantomStrafeCooldownJitterMs = 3000;
        private const float PhantomStrafeDistMin = 140f;
        private const float PhantomStrafeDistMax = 240f;
        private static readonly Dictionary<ulong, long> s_phantomNextStrafeMs = new();

        /// <summary>
        /// Issues a lateral micro-move around <paramref name="threatPos"/>.
        /// Returns true if a move was issued.
        /// </summary>
        private static bool TryPhantomCombatStrafe(Agent phantom, Region region, Vector3 threatPos,
            long nowMs, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (region == null) return false;

            if (s_phantomNextStrafeMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt)
                return false;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            Vector3 toTarget = threatPos - phantomPos;
            toTarget.Z = 0f;
            if (Vector3.LengthSqr(toTarget) < 1f) return false;
            toTarget = Vector3.Normalize(toTarget);

            // Perpendicular, random side.
            float side = rng.NextDouble() < 0.5 ? 1f : -1f;
            Vector3 lateral = new(-toTarget.Y * side, toTarget.X * side, 0f);

            float dist = PhantomStrafeDistMin + (float)rng.NextDouble() * (PhantomStrafeDistMax - PhantomStrafeDistMin);
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            float radius = MathF.Max(20f, phantom.Bounds.Radius);

            // Straight sideways first, then slight forward/backward arcs so a
            // phantom against geometry slides along it instead of giving up.
            ReadOnlySpan<float> arcs = stackalloc float[] { 0f, 0.35f, -0.35f, 0.7f, -0.7f };
            for (int i = 0; i < arcs.Length; i++)
            {
                float a = arcs[i];
                float cos = MathF.Cos(a), sin = MathF.Sin(a);
                Vector3 dir = new(lateral.X * cos - lateral.Y * sin, lateral.X * sin + lateral.Y * cos, 0f);
                Vector3 candidate = phantomPos + dir * dist;
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(candidate, radius, walkCheck) == false) continue;

                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                if (phantom.Locomotor?.MoveTo(candidate, ref opts) == true)
                {
                    s_phantomNextStrafeMs[phantom.Id] = nowMs + PhantomStrafeCooldownMs
                        + (long)(rng.NextDouble() * PhantomStrafeCooldownJitterMs);
                    return true;
                }
            }

            // Nothing walkable either side � try again in a shortened window
            // rather than burning the full cooldown on a failure.
            s_phantomNextStrafeMs[phantom.Id] = nowMs + 1000;
            return false;
        }

        private static void PrunePhantomAiStateFor(ulong phantomId)
        {
            s_phantomNextKiteMs.Remove(phantomId);
            s_phantomNextSupportMs.Remove(phantomId);
            s_phantomNextHazardMs.Remove(phantomId);
            s_phantomNextStrafeMs.Remove(phantomId);
            s_phantomCommittedTargetId.Remove(phantomId);
        }

        // ---- Hazard / ground-effect avoidance -----------------------------
        //
        // Phantoms had no concept of standing in fire � the spacing dash is
        // random anti-clustering, not evasion, so a phantom parked in a
        // damaging pool would happily burn there for the whole fight.
        //
        // "Is this hotspot harmful to ME" is answered authoritatively rather
        // than inferred: Power.IsValidTarget(powerProto, hotspot,
        // hotspot.Alliance, phantom) is the same check the hotspot itself
        // runs before applying its powers (Hotspot.cs), so if it returns true
        // for a hostile hotspot's applied power, that power really would land
        // on this phantom. That deliberately avoids trying to read damage
        // numbers off a PowerPrototype: DamageBase is a curve property
        // indexed by PowerRank and is NOT resolved on an uninstantiated
        // prototype, so a damage-based test there would silently read 0 and
        // never detect anything.
        //
        // HARD RULE: mission hotspots are never avoided. They're trigger
        // volumes for objectives/cutscenes, not damage � and fleeing them
        // would break mission participation, which is exactly the class of
        // bug that cost us the Age of Ultron cutscene. Alliance alone isn't
        // a sufficient guard there, so IsMissionHotspot is checked explicitly.
        private const float PhantomHazardScanRadius = 500f;
        private const long  PhantomHazardCheckCooldownMs = 1200;
        private static readonly Dictionary<ulong, long> s_phantomNextHazardMs = new();

        private static bool IsHotspotHarmfulTo(Hotspot hotspot, Agent phantom)
        {
            if (hotspot == null || phantom == null) return false;
            if (hotspot.IsMissionHotspot) return false;              // never flee objective triggers
            if (hotspot.IsHostileTo(phantom) == false) return false;  // friendly/neutral field � leave it alone

            HotspotPrototype hotspotProto = hotspot.HotspotPrototype;
            if (hotspotProto == null) return false;

            if (HotspotPowersHitPhantom(hotspotProto.AppliesPowers, hotspot, phantom)) return true;
            if (HotspotPowersHitPhantom(hotspotProto.AppliesIntervalPowers, hotspot, phantom)) return true;
            return false;
        }

        private static bool HotspotPowersHitPhantom(PowerPrototype[] powerProtos, Hotspot hotspot, Agent phantom)
        {
            if (powerProtos == null) return false;
            for (int i = 0; i < powerProtos.Length; i++)
            {
                var powerProto = powerProtos[i];
                if (powerProto == null) continue;
                if (Power.IsValidTarget(powerProto, hotspot, hotspot.Alliance, phantom))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Steps a phantom out of any harmful ground effect it is standing in.
        /// Returns true if an escape move was issued.
        /// </summary>
        private static bool TryPhantomAvoidHazard(Agent phantom, Region region, long nowMs)
        {
            if (region == null) return false;
            if (s_phantomNextHazardMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt)
                return false;
            s_phantomNextHazardMs[phantom.Id] = nowMs + PhantomHazardCheckCooldownMs;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            var scanSphere = new Sphere(phantomPos, PhantomHazardScanRadius);
            var scanCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(
                MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            // Only react to hazards the phantom is ACTUALLY standing in.
            // Reacting to merely-nearby ones would have phantoms edging around
            // the arena constantly and fighting the follow/kite logic for
            // control of movement.
            Hotspot standingIn = null;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(scanSphere, scanCtx))
            {
                if (we is not Hotspot hs) continue;
                if (hs.ContainsAvatar(phantom) == false) continue;
                if (IsHotspotHarmfulTo(hs, phantom) == false) continue;
                standingIn = hs;
                break;
            }

            if (standingIn == null) return false;

            PhantomAIEvents.RaiseHazardDetected(phantom, standingIn.RegionLocation.Position);

            // Walk out the short way: directly away from the hazard centre,
            // far enough to clear its radius with margin.
            Vector3 hazardPos = standingIn.RegionLocation.Position;
            Vector3 away = phantomPos - hazardPos;
            away.Z = 0f;
            if (Vector3.LengthSqr(away) < 1f)
                away = new Vector3(1f, 0f, 0f);   // dead centre � any direction beats standing still
            away = Vector3.Normalize(away);

            float escapeDist = standingIn.Bounds.Radius + phantom.Bounds.Radius + 150f;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            float radius = MathF.Max(20f, phantom.Bounds.Radius);

            ReadOnlySpan<float> arcs = stackalloc float[] { 0f, 0.5f, -0.5f, 1.0f, -1.0f, 1.6f, -1.6f };
            for (int i = 0; i < arcs.Length; i++)
            {
                float a = arcs[i];
                float cos = MathF.Cos(a), sin = MathF.Sin(a);
                Vector3 dir = new(away.X * cos - away.Y * sin, away.X * sin + away.Y * cos, 0f);
                Vector3 candidate = phantomPos + dir * escapeDist;
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(candidate, radius, walkCheck) == false) continue;

                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                if (phantom.Locomotor?.MoveTo(candidate, ref opts) == true)
                {
                    // VERIFICATION DIAGNOSTIC � remove once confirmed live.
                    PhantomLogger.Info($"[PhantomHero:Hazard] {phantom} escaping {standingIn.PrototypeName} (radius={standingIn.Bounds.Radius:F0}) dist={escapeDist:F0}");
                    return true;
                }
            }

            return false;
        }

        // ---- Support / buff powers ---------------------------------------
        //
        // Support powers (TargetsFriendly) were filtered out of combat
        // entirely, so every hero with a team-buff kit � Kitty Pryde, Emma,
        // Jean, Cap � never used half of what it had.
        //
        // ARCHITECTURAL REQUIREMENT, not a preference: this runs as its OWN
        // pass over its OWN target list and must never contribute entries to
        // the attack candidate list. A previous attempt let allies into that
        // shared list; because the caller is almost always the closest entity
        // to a friendly phantom, the caller became candidates[0] and won
        // "nearest" over real enemies � phantoms stopped advancing on
        // hostiles and lost their idle-follow spacing (live regression,
        // 2026-07-19, reverted). Keeping the two resolutions disjoint is what
        // makes this safe to re-attempt.
        //
        // Enemy phantoms are excluded: a rogue/nemesis buffing itself mid-duel
        // is not the fantasy, and it would also hand them a survivability
        // boost that isn't in any balance pass.
        private const long PhantomSupportCooldownMs = 12000;
        private const long PhantomSupportJitterMs   = 4000;
        private const float PhantomSupportRange     = 900f;
        private static readonly Dictionary<ulong, long> s_phantomNextSupportMs = new();

        /// <summary>
        /// Fires one ready TargetsFriendly power on the ally that most needs
        /// it (lowest health fraction, self included). Returns true if a
        /// support power was activated.
        /// </summary>
        private bool TryPhantomSupport(Agent phantom, Region region, long nowMs,
            MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (region == null) return false;

            if (s_phantomNextSupportMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt)
                return false;

            var pc = phantom.PowerCollection;
            if (pc == null) return false;

            // Roll the next window regardless of outcome, so a phantom whose
            // support powers are all on cooldown isn't re-scanned every tick.
            s_phantomNextSupportMs[phantom.Id] = nowMs
                + PhantomSupportCooldownMs + (long)(rng.NextDouble() * PhantomSupportJitterMs);

            // --- Resolve support targets (SEPARATE list, never the attack one) ---
            Player host = PhantomHost;
            if (host == null) return false;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            float rangeSq = PhantomSupportRange * PhantomSupportRange;

            WorldEntity neediest = phantom;
            float neediestPct = 1f;

            float selfMax = phantom.Properties[PropertyEnum.HealthMax];
            if (selfMax > 0f)
                neediestPct = (float)phantom.Properties[PropertyEnum.Health] / selfMax;

            // The caller (the real player) counts as an ally worth supporting.
            if (IsInWorld && IsDead == false)
            {
                float callerMax = Properties[PropertyEnum.HealthMax];
                if (callerMax > 0f
                    && Vector3.DistanceSquared(RegionLocation.Position, phantomPos) <= rangeSq)
                {
                    float pct = (float)Properties[PropertyEnum.Health] / callerMax;
                    if (pct < neediestPct) { neediestPct = pct; neediest = this; }
                }
            }

            // ...and so do squadmates.
            var manager = Game.EntityManager;
            foreach (ulong mateId in host.PhantomAvatarIds)
            {
                if (mateId == phantom.Id) continue;
                var mate = manager.GetEntity<Avatar>(mateId);
                if (mate == null || mate.IsInWorld == false || mate.IsDead) continue;
                float mateMax = mate.Properties[PropertyEnum.HealthMax];
                if (mateMax <= 0f) continue;
                if (Vector3.DistanceSquared(mate.RegionLocation.Position, phantomPos) > rangeSq) continue;

                float pct = (float)mate.Properties[PropertyEnum.Health] / mateMax;
                if (pct < neediestPct) { neediestPct = pct; neediest = mate; }
            }

            if (neediest == null) return false;

            // --- Pick a ready support power ---
            float targetDist = Vector3.Distance(neediest.RegionLocation.Position, phantomPos);
            PrototypeId chosen = PrototypeId.Invalid;

            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled || pp.IsTravelPower) continue;
                if (power.IsOnCooldown()) continue;
                if (IsPhantomPowerBlacklisted(phantom.Id, kvp.Key, nowMs)) continue;

                // Must be an ally-targeting power, and must NOT be one that
                // hits enemies � a power flagged for both is an attack that
                // happens to allow friendly targets, not a buff, and firing it
                // at a squadmate is not the intent here.
                var reach = pp.GetTargetingReach();
                if (reach == null) continue;
                if (reach.TargetsFriendly == false) continue;
                if (reach.TargetsEnemy) continue;

                float r = power.GetRange();
                if (r > 0f && r + 50f < targetDist) continue;
                if (r <= 0f && neediest.Id != phantom.Id) continue;  // self-only power, ally chosen

                chosen = kvp.Key;
                break;
            }

            if (chosen == PrototypeId.Invalid) return false;

            int fxSeed = rng.Next(1, 10000);
            var settings = new PowerActivationSettings(neediest.Id,
                neediest.RegionLocation.Position, phantomPos)
            {
                Flags = PowerActivationSettingsFlags.NotifyOwner | PowerActivationSettingsFlags.ServerCombo,
                FXRandomSeed = fxSeed,
                PowerRandomSeed = fxSeed,
            };

            PowerUseResult result = phantom.ActivatePower(chosen, ref settings);
            if (result != PowerUseResult.Success)
            {
                // Same treatment attack powers get (see the blacklist block in
                // TryPhantomAttack): structural failures park the power for a
                // long window, transient CC only briefly.
                if (result == PowerUseResult.WeaponMissing
                    || result == PowerUseResult.NotAllowedByTransformMode)
                    s_phantomPowerBlacklist[(phantom.Id, chosen)] = nowMs + PhantomPowerBlacklistMs;
                else if (result == PowerUseResult.RestrictiveCondition)
                    s_phantomPowerBlacklist[(phantom.Id, chosen)] = nowMs + PhantomTransientPowerBlacklistMs;
                return false;
            }

            return true;
        }

        // ---- Ranged kiting -----------------------------------------------
        //
        // ComputePhantomFollowStopDist already parks a ranged phantom at its
        // weapon range, but FollowEntity only limits how close the phantom
        // ADVANCES � it never backs up. So once a melee attacker closed the
        // gap, a ranged phantom just stood there taking hits at point-blank.
        // This restores the distance.
        //
        // Deliberately conservative, because this subsystem has bitten us
        // before (the spacing dash's approach-dash-approach loop, fixed in
        // the 2026-07-20 audit):
        //   * Only ranged-role phantoms kite (no ready melee power).
        //   * Only when a hostile is well INSIDE the comfort band, not merely
        //     at its edge � the hysteresis gap is what prevents oscillation.
        //   * Rate-limited per phantom on top of that.
        //   * Never kites while already at/behind the preferred distance.
        private const float PhantomKiteTriggerPct = 0.55f;  // retreat once inside 55% of standoff
        private const float PhantomKiteRecoverPct = 0.90f;  // aim to restore to 90% of standoff
        private const long  PhantomKiteCooldownMs = 1500;
        private const float PhantomKiteMinStandoff = 250f;  // below this a kit isn't really "ranged"
        private static readonly Dictionary<ulong, long> s_phantomNextKiteMs = new();

        /// <summary>
        /// Backs a ranged phantom away from <paramref name="threatPos"/> when
        /// it has been closed down. Returns true if a retreat was issued.
        /// </summary>
        private static bool TryPhantomKite(Agent phantom, Region region, Vector3 threatPos,
            float threatDist, float preferredStandoff, long nowMs,
            MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (region == null) return false;
            if (preferredStandoff < PhantomKiteMinStandoff) return false;   // melee/brawler kit � never kite
            if (threatDist >= preferredStandoff * PhantomKiteTriggerPct) return false;

            if (s_phantomNextKiteMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt)
                return false;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            Vector3 away = phantomPos - threatPos;
            away.Z = 0f;
            if (Vector3.LengthSqr(away) < 1f) return false;                 // stacked exactly � no usable direction
            away = Vector3.Normalize(away);

            float wanted = preferredStandoff * PhantomKiteRecoverPct - threatDist;
            if (wanted <= 0f) return false;

            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            float radius = MathF.Max(20f, phantom.Bounds.Radius);

            // Try straight back first, then progressively wider arcs, so a
            // phantom backed against geometry slides along it instead of
            // giving up and standing still in melee.
            ReadOnlySpan<float> arcs = stackalloc float[] { 0f, 0.4f, -0.4f, 0.8f, -0.8f };
            for (int i = 0; i < arcs.Length; i++)
            {
                float a = arcs[i];
                float cos = MathF.Cos(a), sin = MathF.Sin(a);
                Vector3 dir = new(away.X * cos - away.Y * sin, away.X * sin + away.Y * cos, 0f);
                Vector3 candidate = phantomPos + dir * wanted;
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(candidate, radius, walkCheck) == false) continue;

                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                if (phantom.Locomotor?.MoveTo(candidate, ref opts) == true)
                {
                    s_phantomNextKiteMs[phantom.Id] = nowMs + PhantomKiteCooldownMs;
                    return true;
                }
            }

            return false;
        }

        // Widest radius we bother gathering hostiles within when evaluating
        // AoE powers. Comfortably larger than any real power radius, so one
        // sweep per attack evaluation serves every AoE power in the kit.
        private const float PhantomAoeClusterScanRadius = 800f;

        /// <summary>
        /// Collects positions of hostiles near <paramref name="center"/> for
        /// AoE cluster counting. Called at most once per attack evaluation,
        /// and only when the phantom actually has an AoE power to score.
        /// </summary>
        private static void GatherPhantomAoeCluster(Agent phantom, Vector3 center, List<Vector3> into)
        {
            into.Clear();
            Region region = phantom.Region;
            if (region == null) return;

            var scanSphere = new Sphere(center, PhantomAoeClusterScanRadius);
            var scanCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(
                MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            foreach (WorldEntity we in region.IterateEntitiesInVolume(scanSphere, scanCtx))
            {
                if (we == null || we.Id == phantom.Id) continue;
                if (we.IsDead || we.IsInWorld == false) continue;
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                into.Add(we.RegionLocation.Position);
            }
        }

        /// <summary>
        /// How many hostiles an AoE centered on <paramref name="center"/> with
        /// the given radius would actually catch, capped by the power's own
        /// MaxAOETargets.
        /// </summary>
        private static int CountPhantomAoeHits(List<Vector3> clusterPositions, Vector3 center,
            float radius, int maxAoeTargets)
        {
            if (radius <= 0f) return 1;

            float radiusSq = radius * radius;
            int hits = 0;
            for (int i = 0; i < clusterPositions.Count; i++)
                if (Vector3.DistanceSquared(clusterPositions[i], center) <= radiusSq)
                    hits++;

            if (hits < 1) hits = 1;                                   // always at least the primary target
            if (maxAoeTargets > 0 && hits > maxAoeTargets) hits = maxAoeTargets;
            return hits;
        }

        private static float ComputePhantomFollowStopDist(Agent phantom, WorldEntity target,
            PhantomCombatRangePref rangePref = PhantomCombatRangePref.Auto)
        {
            var pc = phantom.PowerCollection;
            if (pc == null) return PhantomFollowStopMin;

            float bestRange = 0f;
            bool hasUsableMelee = false;
            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled) continue;
                if (pp.IsTravelPower) continue;
                if (power.IsOnCooldown()) continue;

                float r = power.GetRange();
                // Power.IsMelee reads the real, data-driven
                // TargetingReachPrototype.Melee flag � trustworthy. The
                // "|| r <= 0f" fallback this used to have was not: plenty of
                // non-melee powers (self-targeted buffs/heals, anything
                // whose range isn't a travel distance) also return 0 range
                // without being melee attacks at all, and every one of them
                // was flipping hasUsableMelee and forcing a genuinely ranged
                // hero to close to point-blank range. Confirmed live
                // 2026-08-08: ranged heroes standing "right on top of the
                // enemy" instead of holding their weapon range. Melee-ness
                // is now judged solely by the real flag.
                if (Power.IsMelee(pp))
                    hasUsableMelee = true;
                else if (r > bestRange)
                    bestRange = r;
            }

            // If the kit has ANY usable melee power ready, close all the way
            // in � regardless of what other ranged powers are also
            // available. Previously this always used the WIDEST range
            // among ready powers, so a hero with a mixed kit (both melee
            // and ranged options, which is common) stopped at ranged
            // distance and never actually entered true melee range �
            // meaning TryPhantomAttack's melee range gate
            // (targetDistSq <= PhantomMeleeRangeSq) could never pass, so
            // the melee powers in their kit could structurally never be
            // picked even though they "had" them. Confirmed via a live
            // user report (2026-07-19): "seeing heroes use more range
            // powers than melee if they have melee powers."
            // FollowEntity's stop distance is measured center-to-center by
            // the Locomotor (confirmed � it never subtracts either side's
            // Bounds.Radius), unlike the real in-game range validation
            // (Power.Validation.cs's IsInRangeInternal, which subtracts the
            // target's radius). Without adding it back here, "stop 50u from
            // the target's CENTER" could put the walk destination inside a
            // large-radius target's own collision (bosses especially),
            // which is what produced the "walks into the enemy for a few
            // seconds" visual even after the melee range gate above was
            // already fixed to be edge-aware. Add the target's own radius so
            // the phantom actually stops at its edge.
            float targetRadius = target != null ? target.Bounds.Radius : 0f;

            // Explicit per-hero override (see Player.CombatRange.cs for why this
            // isn't auto-detected). Melee forces the close-in behavior even for
            // a kit with long-range options; Ranged suppresses the
            // "any ready melee power wins" rule below so a ranged hero carrying
            // a couple of melee moves (Iron Man) holds its weapon range instead
            // of brawling. Auto leaves the original behavior untouched.
            if (rangePref == PhantomCombatRangePref.Melee)
                return PhantomFollowStopMin + targetRadius;

            if (rangePref == PhantomCombatRangePref.Ranged)
            {
                // Fall through to the ranged branch, but only if the kit
                // actually has a genuine ranged option to hold. A hero with no
                // ranged power at all would otherwise be told to stand off at a
                // distance from which it can never attack.
                if (bestRange > 0f)
                    return Math.Clamp(bestRange - PhantomFollowRangeMargin,
                        PhantomFollowStopMin, PhantomFollowStopMax) + targetRadius;
                return PhantomFollowStopMin + targetRadius;
            }

            if (hasUsableMelee)
                return PhantomFollowStopMin + targetRadius;

            if (bestRange <= 0f) return PhantomFollowStopMin + targetRadius;

            float dist = Math.Clamp(bestRange - PhantomFollowRangeMargin,
                PhantomFollowStopMin, PhantomFollowStopMax) + targetRadius;
            return dist;
        }

        // ================================================================
        //  Phantom gear
        //
        //  Rolls one level-appropriate item per equip slot using the same
        //  data the loot system uses for real drops:
        //  AvatarPrototype.EquipmentInventories declares the slots, and
        //  LootUtilities.BuildInventoryLootPicker resolves every concrete
        //  item prototype that fits a given (avatar, slot) pair from the
        //  loaded client data. Nothing item-specific lives in source.
        //
        //  Besides stats, this un-breaks weapon-gated powers: powers that
        //  returned WeaponMissing (e.g. shield-throw style kits) work once
        //  the hero-specific weapon slot is filled.
        // ================================================================

        // ----------------------------------------------------------------
        //  Gear rarity bands (see PickPhantomGearRarity for the data-
        //  reality notes on the top tiers):
        //    levels  1-10  ? tier 1                       (white)
        //    levels 11-19  ? tiers 2-3                    (green/blue)
        //    levels 20-30  ? tier 4                       (purple)
        //    levels 31-50  ? tier 4 + RarityCosmic        (purple/yellow)
        //    levels 51-60  ? RarityCosmic + RarityUnique  (yellow/orange)
        // ----------------------------------------------------------------
        private static readonly object s_rarityTierLock = new();
        private static Dictionary<int, PrototypeId> s_rarityByTier;

        private static void EnsureRarityTiers()
        {
            lock (s_rarityTierLock)
            {
                if (s_rarityByTier != null) return;
                var map = new Dictionary<int, PrototypeId>();
                foreach (PrototypeId rarityRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<RarityPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    RarityPrototype rarityProto = rarityRef.As<RarityPrototype>();
                    if (rarityProto == null) continue;
                    // First proto wins per tier; the core ladder is a single
                    // DowngradeTo chain so collisions shouldn't happen.
                    map.TryAdd(rarityProto.Tier, rarityRef);
                }
                s_rarityByTier = map;
                // One-time dump of the resolved ladder so band issues are
                // diagnosable from the log (e.g. off-ladder special
                // rarities in data that shouldn't be rolled).
                var sb = new System.Text.StringBuilder($"[PhantomHero:Gear] rarity tier map built: {map.Count} tiers |");
                foreach (var kvp in map)
                    sb.Append($" T{kvp.Key}={kvp.Value.GetName()}");
                PhantomLogger.Info(sb.ToString());
            }
        }

        /// <summary>
        /// The rarities a phantom's gear is ALLOWED to end up at for a
        /// given level. This is both the roll pool and the acceptance
        /// filter: some item prototypes carry their own rarity
        /// restrictions (red "Ultimate" items, Runeword items), and
        /// MakeRestrictionsDroppable silently overrides whatever rarity we
        /// request to satisfy them � so forcing the rarity up front is not
        /// enough, the FINAL spec rarity must be validated against this
        /// list and off-band items re-picked.
        ///
        /// 1.52 data reality: the DowngradeTo tier chain covers
        /// Common(1) ? Uncommon(2) ? Rare(3) ? Epic(4), but yellow Cosmic
        /// and orange Unique are not on that chain � they're anchored
        /// directly by the engine's LootGlobalsPrototype refs. The chain
        /// above Epic holds the red special rarities we must never roll.
        /// </summary>
        private static List<PrototypeId> GetPhantomGearAllowedRarities(int level)
        {
            EnsureRarityTiers();
            var lootGlobals = GameDatabase.LootGlobalsPrototype;
            var allowed = new List<PrototypeId>(2);

            void AddTier(int tier)
            {
                if (s_rarityByTier.TryGetValue(tier, out PrototypeId r) && r != PrototypeId.Invalid)
                    allowed.Add(r);
            }

            if (level <= 10) AddTier(1);
            else if (level <= 19) { AddTier(2); AddTier(3); }
            else if (level <= 30) AddTier(4);
            else if (level <= 50)
            {
                AddTier(4);
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
            }
            else
            {
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
                if (lootGlobals.RarityUnique != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityUnique);
            }

            return allowed;
        }

        /// <summary>
        /// Rarity band for an Endless Challenge reward chest, keyed by wave
        /// count instead of character level (see Player.WaveDirector.cs's
        /// SpawnEndlessChest). Reuses the same real, verified rarity-tier
        /// ladder as GetPhantomGearAllowedRarities above (s_rarityByTier,
        /// built from the actual RarityPrototype hierarchy + LootGlobals'
        /// Cosmic/Unique anchors) but NARROWS the band as waves climb
        /// instead of picking a level-appropriate band once � dropping the
        /// low tiers out of the pool as it goes, so a higher wave count is a
        /// strictly better chance at the top of the ladder, not just more
        /// tiers competing for the same roll. bumpOneBand shifts the
        /// breakpoints down by one step (used for the every-20-waves
        /// loot-splosion so that milestone always lands one band ahead of
        /// where the smooth curve would otherwise put it).
        /// </summary>
        internal static List<PrototypeId> GetEndlessChestAllowedRarities(int endlessCycle, bool bumpOneBand = false)
        {
            EnsureRarityTiers();
            var lootGlobals = GameDatabase.LootGlobalsPrototype;
            var allowed = new List<PrototypeId>(3);

            void AddTier(int tier)
            {
                if (s_rarityByTier.TryGetValue(tier, out PrototypeId r) && r != PrototypeId.Invalid)
                    allowed.Add(r);
            }

            int cycle = bumpOneBand ? endlessCycle + 15 : endlessCycle;

            if (cycle < 10) { AddTier(1); AddTier(2); }
            else if (cycle < 20) { AddTier(2); AddTier(3); }
            else if (cycle < 30) { AddTier(3); AddTier(4); }
            else if (cycle < 40)
            {
                AddTier(4);
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
            }
            else
            {
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
                if (lootGlobals.RarityUnique != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityUnique);
            }

            return allowed;
        }

        /// <summary>
        /// Equip the phantom. If <paramref name="gearOverride"/> is
        /// non-empty, those exact item protos are recreated at the
        /// phantom's level (squad/migration restore); otherwise one random
        /// valid item is rolled per unlocked equip slot. Rarity follows the
        /// level band table above. Returns the applied item proto refs for
        /// descriptor storage.
        ///
        /// Works for both avatar phantoms and team-up phantoms � the two
        /// prototype classes each declare their own (identically-shaped but
        /// unrelated) EquipmentInventories property, so it's read via the
        /// concrete type rather than a common base.
        /// </summary>
        internal static List<ulong> ApplyPhantomGear(Player phantomPlayer, Agent phantomAgent, int level, List<ulong> gearOverride,
            IReadOnlyDictionary<EquipmentInvUISlot, PrototypeId> bisLoadout = null)
        {
            var applied = new List<ulong>();
            PrototypeId phantomRef = phantomAgent.PrototypeDataRef;
            AvatarEquipInventoryAssignmentPrototype[] equipmentInventories = phantomAgent is Avatar phantomAvatar
                ? phantomAvatar.AvatarPrototype?.EquipmentInventories
                : phantomRef.As<GameData.Prototypes.AgentTeamUpPrototype>()?.EquipmentInventories;
            if (equipmentInventories == null) return applied;

            Game game = phantomAgent.Game;
            var lootManager = game.LootManager;
            var rng = game.Random;

            bool useOverride = gearOverride != null && gearOverride.Count > 0;
            int overrideIdx = 0;

            List<PrototypeId> allowedRarities = GetPhantomGearAllowedRarities(level);

            // Red "Ultimate" tier � banned from every slot, core or special.
            EnsureRarityTiers();
            s_rarityByTier.TryGetValue(5, out PrototypeId bannedUltimateRef);

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in equipmentInventories)
            {
                if (assignment.UnlocksAtCharacterLevel > level) continue;

                InventoryPrototype invProto = assignment.Inventory;
                if (invProto == null) continue;
                // The costume slot is driven by the phantom costume system �
                // equipping a rolled costume item here would clobber it.
                if (invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;

                // Slot policy:
                //  - Core armor (Gear01-05): rarity strictly follows the
                //    level band table � this is what colors the paper doll.
                //  - Special classes (artifacts, medal, relic, insignia,
                //    ring, legendary, uru-forged): these item families have
                //    their own natural rarity ranges, and forcing the band
                //    on them excluded their ENTIRE pools (empty artifact /
                //    rune / legendary slots on the level-60 paper doll).
                //    They roll their natural rarity instead � red Ultimate
                //    stays banned everywhere.
                //  - Anything else (crafting, consumables, misc): skipped.
                EquipmentInvUISlot uiSlot = assignment.UISlot;
                bool isCoreGear = uiSlot >= EquipmentInvUISlot.Gear01 && uiSlot <= EquipmentInvUISlot.Gear05;
                bool isSpecial = uiSlot == EquipmentInvUISlot.Artifact01 || uiSlot == EquipmentInvUISlot.Artifact02 ||
                                 uiSlot == EquipmentInvUISlot.Artifact03 || uiSlot == EquipmentInvUISlot.Artifact04 ||
                                 uiSlot == EquipmentInvUISlot.Medal      || uiSlot == EquipmentInvUISlot.Relic      ||
                                 uiSlot == EquipmentInvUISlot.Insignia   || uiSlot == EquipmentInvUISlot.Ring       ||
                                 uiSlot == EquipmentInvUISlot.Legendary  || uiSlot == EquipmentInvUISlot.UruForged;
                if (isCoreGear == false && isSpecial == false) continue;

                Inventory equipInventory = phantomAgent.GetInventoryByRef(assignment.Inventory.DataRef);
                if (equipInventory == null) continue;

                // Stored ref on restore (consumed even if it fails, to keep
                // slot alignment); random picks otherwise.
                PrototypeId overrideItemRef = PrototypeId.Invalid;
                if (useOverride && overrideIdx < gearOverride.Count)
                    overrideItemRef = (PrototypeId)gearOverride[overrideIdx++];

                // BiS override (rank-5 level-60 nemesis): wear the community
                // best-in-slot item for this slot. Tried first; if it fails to
                // build in-band the normal random roll below still runs, so a
                // slot is never left empty just because one BiS ref didn't
                // resolve.
                if (bisLoadout != null && bisLoadout.TryGetValue(uiSlot, out PrototypeId bisRef) && bisRef != PrototypeId.Invalid)
                    overrideItemRef = bisRef;

                var picker = new MHServerEmu.Core.Collections.Picker<Prototype>(rng);
                // LootUtilities.BuildInventoryLootPicker hard-casts its ref to
                // AvatarPrototype specifically, so it silently fails (empty
                // picker) for a team-up ref even though the underlying
                // GameDataTables.LootPickingTable lookup it wraps accepts any
                // AgentPrototype. We're already scoped to this exact
                // assignment/slot here, so for a team-up just replicate the
                // same picker build using its AgentTeamUpPrototype directly.
                if (phantomAgent is Avatar)
                {
                    LootUtilities.BuildInventoryLootPicker(picker, phantomRef, assignment.UISlot);
                }
                else if (invProto != null)
                {
                    picker.Clear();
                    AgentPrototype teamUpAgentProto = phantomRef.As<AgentPrototype>();
                    foreach (PrototypeId typeRef in invProto.EntityTypeFilter)
                        MHServerEmu.Games.GameData.Tables.GameDataTables.Instance.LootPickingTable.GetConcreteLootPicker(picker, typeRef, teamUpAgentProto);
                }

                try
                {
                    // An item prototype can carry its own rarity restriction
                    // (red Ultimate / Runeword items live in the same slot
                    // pools as normal gear) and the spec builder overrides
                    // our requested rarity to match it � so the FINAL spec
                    // rarity is what gets validated. The pool is DRAINED via
                    // PickRemove rather than sampled: every rejected item is
                    // removed and never retried, and every remaining item is
                    // eventually tried at EVERY allowed rarity. If any item
                    // in the pool can exist in-band (and every hero has
                    // Uniques/Cosmics per slot), it is guaranteed to be
                    // found � red can never come out of this loop.
                    ItemSpec acceptedSpec = null;
                    PrototypeId acceptedItemRef = PrototypeId.Invalid;

                    bool TryBuildInBandSpec(PrototypeId itemProtoRef)
                    {
                        if (itemProtoRef == PrototypeId.Invalid) return false;

                        if (isCoreGear)
                        {
                            // Core armor: try every banded rarity, random
                            // start for variety. A Unique-class item may
                            // only build at Unique while a normal piece
                            // only reaches Cosmic � one random rarity per
                            // item would wrongly discard valid items.
                            int rarityCount = Math.Max(1, allowedRarities.Count);
                            int start = rng.Next(0, rarityCount);
                            for (int i = 0; i < rarityCount; i++)
                            {
                                PrototypeId rarityRef = allowedRarities.Count > 0
                                    ? allowedRarities[(start + i) % allowedRarities.Count]
                                    : PrototypeId.Invalid;

                                ItemSpec spec = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level, rarityRef);
                                if (spec == null) continue;
                                if (allowedRarities.Count > 0 && allowedRarities.Contains(spec.RarityProtoRef) == false) continue;

                                acceptedSpec = spec;
                                acceptedItemRef = itemProtoRef;
                                return true;
                            }
                            return false;
                        }

                        // Special slots: natural (level-based) rarity roll �
                        // artifacts, medals, runewords, legendaries etc. own
                        // their rarity ranges. Only red Ultimate is banned.
                        ItemSpec naturalSpec = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level);
                        if (naturalSpec == null) return false;
                        if (bannedUltimateRef != PrototypeId.Invalid && naturalSpec.RarityProtoRef == bannedUltimateRef) return false;

                        acceptedSpec = naturalSpec;
                        acceptedItemRef = itemProtoRef;
                        return true;
                    }

                    // Stored override item first (squad/migration restore)...
                    if (overrideItemRef != PrototypeId.Invalid)
                        TryBuildInBandSpec(overrideItemRef);

                    // ...then drain the slot pool until something lands in-band.
                    while (acceptedSpec == null && picker.Empty() == false)
                    {
                        if (picker.PickRemove(out Prototype pickedProto) == false || pickedProto == null) break;
                        TryBuildInBandSpec(pickedProto.DataRef);
                    }

                    if (acceptedSpec == null)
                    {
                        PhantomLogger.Warn($"[PhantomHero:Gear] slot pool for {assignment.UISlot} on {phantomRef.GetName()} has NO item usable at the level-{level} band rarities � slot left empty");
                        continue;
                    }

                    Item item;
                    using (var itemSettingsHandle = EntitySettingsPool.Get(out EntitySettings itemSettings))
                    {
                        itemSettings.EntityRef = acceptedItemRef;
                        itemSettings.ItemSpec = acceptedSpec;
                        item = game.EntityManager.CreateEntity(itemSettings) as Item;
                    }
                    if (item == null)
                    {
                        PhantomLogger.Warn($"[PhantomHero:Gear] CreateEntity returned null for {acceptedItemRef.GetName()} (slot {assignment.UISlot}) on {phantomRef.GetName()} � slot left empty");
                        continue;
                    }

                    // Legendary items have their own affix-rank progression
                    // (Rank 1-5 in the client UI) separate from item level/
                    // rarity, gated behind a large XP grind that GROWS per
                    // rank (confirmed live 2026-07-19: rank 3->4 alone
                    // needed 240,000,000 � the curve isn't flat). A phantom
                    // that spawns with the right Legendary but isn't at max
                    // rank is only wearing a fraction of what that item
                    // actually does � the higher-rank bonuses/procs never
                    // apply. AwardAffixXP is the real leveling entry point
                    // (Item.cs) � it walks TryLevelUpAffix's loop and calls
                    // AwardLevelUpAffixes for every rank crossed, so this
                    // grants the actual rank bonuses, not just a rank
                    // number.
                    //
                    // AwardAffixXP(long) internally does `(int)amount`
                    // before applying it, so a single call is hard-capped
                    // at int.MaxValue (~2.147B) worth of XP regardless of
                    // what's passed in � confirmed live twice: first
                    // long.MaxValue/2 overflowed that cast into garbage and
                    // granted nothing, then a single 2B-XP call only
                    // reached rank 3 because the real cumulative cost to
                    // rank 5 exceeds what one call can carry. Loop it
                    // instead of assuming one call is enough � each call
                    // self-caps at GetAffixLevelCap() so this is safe, and
                    // stops as soon as the real cap is actually reached.
                    if (item.Prototype is LegendaryPrototype)
                    {
                        int affixLevelCap = item.GetAffixLevelCap();
                        for (int guard = 0; guard < 10 && item.Properties[PropertyEnum.ItemAffixLevel] < affixLevelCap; guard++)
                            item.AwardAffixXP(2_000_000_000L);
                    }

                    InventoryResult moveResult = item.ChangeInventoryLocation(equipInventory);
                    if (moveResult != InventoryResult.Success)
                    {
                        PhantomLogger.Warn($"[PhantomHero:Gear] ChangeInventoryLocation failed ({moveResult}) for {acceptedItemRef.GetName()} (slot {assignment.UISlot}) on {phantomRef.GetName()} � slot left empty");
                        item.Destroy();
                        continue;
                    }

                    applied.Add((ulong)acceptedItemRef);
                }
                catch (Exception ex)
                {
                    PhantomLogger.Warn($"[PhantomHero:Gear] equip roll for slot {assignment.UISlot} on {phantomAgent.Id:X} failed: {ex.Message}");
                }
            }

            return applied;
        }

        // Floor for ScaleHealthMultForLevel � at level 1, an ambush phantom
        // gets this fraction of its full rank-tuned HealthMaxMult; ramps
        // (quadratically, same curve as ApplyPhantomDamageScaling) up to
        // 100% by level 60. Keeps rank still meaningful at low level (a
        // rank 5 nemesis is still tankier than rank 1 at any given level)
        // without being effectively unkillable relative to a low-level
        // hero's real damage output.
        private const float NemesisHealthMultLevelFloorFactor = 0.35f;

        private static float ScaleHealthMultForLevel(float fullMult, int level)
        {
            float t = Math.Clamp((level - 1) / 59f, 0f, 1f);
            t *= t;
            float floorMult = fullMult * NemesisHealthMultLevelFloorFactor;
            return floorMult + t * (fullMult - floorMult);
        }

        /// <summary>
        /// Re-rolls a phantom's equipment for <paramref name="level"/> after its level changed.
        /// </summary>
        /// <remarks>
        /// Levelling a phantom DOWN goes through <see cref="Avatar.OnLevelUp"/>, which calls
        /// CheckEquipmentRestrictions() and unequips every item whose level requirement the
        /// phantom no longer meets � phantoms have a real (headless) Player owner, so that path
        /// runs for them exactly as it does for a player levelling down via prestige. Without a
        /// re-roll the phantom would be left stripped. The same applies in reverse: once a
        /// phantom can level down it can also level back up, and it would otherwise be stuck in
        /// whatever low-level gear the downlevel gave it.
        ///
        /// Gear is rolled fresh rather than recreated from the stored descriptor refs, because
        /// ApplyPhantomGear() consumes an override list positionally while skipping slots whose
        /// UnlocksAtCharacterLevel is above the target level � a stored list captured at level 60
        /// would misalign against the smaller slot set of a low-level phantom. A BiS loadout
        /// applied via `!phantom gear bis` therefore needs re-applying after a level change.
        /// </remarks>
        private static void RegearPhantomForLevel(Player host, Agent phantom, int level)
        {
            Player phantomOwner = phantom.GetOwnerOfType<Player>();
            if (phantomOwner == null) return;

            AvatarEquipInventoryAssignmentPrototype[] equipmentInventories = phantom is Avatar phantomAvatar
                ? phantomAvatar.AvatarPrototype?.EquipmentInventories
                : phantom.PrototypeDataRef.As<AgentTeamUpPrototype>()?.EquipmentInventories;
            if (equipmentInventories == null) return;

            // Strip what's there. The costume slot belongs to the phantom costume system and
            // must not be touched (team-ups have no costume slot at all).
            foreach (AvatarEquipInventoryAssignmentPrototype assignment in equipmentInventories)
            {
                InventoryPrototype invProto = assignment.Inventory;
                if (invProto == null || invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume)
                    continue;

                phantom.GetInventoryByRef(invProto.DataRef)?.DestroyContained();
            }

            List<ulong> applied = ApplyPhantomGear(phantomOwner, phantom, level, null);
            host.UpdatePhantomGear(phantom.Id, applied);
        }

        private static void ApplyPhantomDamageScaling(Agent phantom, int level, bool enemy = false, bool deathmatch = false)
        {
            float t = Math.Clamp((level - 1) / 59f, 0f, 1f);
            t *= t; // quadratic � shallow through story levels, steep into endgame

            float dmgMult, pctBonus, dmgRating;
            if (deathmatch)
            {
                // Same curve for both teams � see DeathmatchPhantomDmgMultLvl60's
                // comment for why this is its own profile, not the enemy one.
                dmgMult   = DeathmatchPhantomDmgMultLvl1     + t * (DeathmatchPhantomDmgMultLvl60     - DeathmatchPhantomDmgMultLvl1);
                pctBonus  = DeathmatchPhantomDmgPctBonusLvl1 + t * (DeathmatchPhantomDmgPctBonusLvl60 - DeathmatchPhantomDmgPctBonusLvl1);
                dmgRating = DeathmatchPhantomDmgRatingLvl1   + t * (DeathmatchPhantomDmgRatingLvl60   - DeathmatchPhantomDmgRatingLvl1);
            }
            else if (enemy)
            {
                dmgMult   = EnemyPhantomDmgMultLvl1     + t * (EnemyPhantomDmgMultLvl60     - EnemyPhantomDmgMultLvl1);
                pctBonus  = EnemyPhantomDmgPctBonusLvl1 + t * (EnemyPhantomDmgPctBonusLvl60 - EnemyPhantomDmgPctBonusLvl1);
                dmgRating = EnemyPhantomDmgRatingLvl1   + t * (EnemyPhantomDmgRatingLvl60   - EnemyPhantomDmgRatingLvl1);
            }
            else
            {
                dmgMult   = PhantomDmgMultLvl1     + t * (PhantomDmgMultLvl60     - PhantomDmgMultLvl1);
                pctBonus  = PhantomDmgPctBonusLvl1 + t * (PhantomDmgPctBonusLvl60 - PhantomDmgPctBonusLvl1);
                dmgRating = PhantomDmgRatingLvl1   + t * (PhantomDmgRatingLvl60   - PhantomDmgRatingLvl1);
            }

            phantom.Properties[PropertyEnum.DamageMult]     = dmgMult;
            phantom.Properties[PropertyEnum.DamagePctBonus] = pctBonus;
            phantom.Properties[PropertyEnum.DamageRating]   = dmgRating;
        }

        // Enemy phantoms' final DamageMult was observed reaching 12.70-14.38
        // in live combat (2026-07-21 balance investigation, read directly
        // from the SAME property real damage calculations use � this is
        // ground truth, not an estimate) � far beyond the ~1.7-2.38 the
        // rank-scaling formula alone was ever meant to produce.
        //
        // Root cause, traced rather than guessed: DamageMult is an
        // AGGREGATE property. ApplyPhantomDamageScaling's flat assignment
        // sets the base contribution, but equipped gear contributes
        // SEPARATELY as child property collections that sum on top
        // automatically (see PropertyCollection.AddChildCollection) � so by
        // the time the nemesis rank-boost multiply below reads
        // Properties[DamageMult] back, it's already reading base-formula
        // PLUS real gear's stacked bonus, then multiplies that COMBINED
        // number by another 1.15-1.40x on top. Two real, independently
        // correct systems (rank scaling, gear itemization) compounding
        // multiplicatively rather than each contributing independently.
        //
        // Capped here, at the very end of the sequence (after both gear and
        // rank-boost have already applied for this phantom), using the same
        // read-then-write-back technique the rank-boost line above already
        // uses � which is proven to durably set the value real combat reads,
        // since that's exactly how the already-shipped rank boost works.
        // Chosen conservatively: roughly double the highest value the
        // rank-scaling formula alone can produce (rank 5: 1.7 x 1.40 = 2.38),
        // so real gear still meaningfully increases damage over an ungeared
        // phantom, without letting it compound into double digits.
        private const float EnemyPhantomDamageMultCap = 5.0f;

        private static void ClampEnemyPhantomDamageMult(Agent phantom)
        {
            float current = phantom.Properties[PropertyEnum.DamageMult];
            if (current <= EnemyPhantomDamageMultCap) return;

            phantom.Properties[PropertyEnum.DamageMult] = EnemyPhantomDamageMultCap;

            // A flat Properties[x] = v assignment only replaces the BASE layer �
            // it does NOT zero out child property collections (gear, and the
            // Rank prototype's own baked-in Mod bundle attached via
            // ModChangeModEffects during SetSimulated), which keep summing on
            // top of whatever base we just set. Confirmed live (2026-07-21):
            // this single-assignment version left rank 1-5 nemeses landing on
            // a uniform dmgMult=10.00 � exactly double the 5.0 cap � because
            // the Boss rank tag's child kept adding back ~5.0 every read.
            //
            // Solve algebraically for the base value that makes base+child
            // land exactly at the cap: after setting base := cap, the
            // read-back aggregate is (cap + child), so child = aggregate - cap.
            // Setting base := cap - child = 2*cap - aggregate makes the next
            // read-back (base + child) equal exactly cap.
            float afterFirstSet = phantom.Properties[PropertyEnum.DamageMult];
            if (afterFirstSet > EnemyPhantomDamageMultCap)
            {
                float correctedBase = (2f * EnemyPhantomDamageMultCap) - afterFirstSet;
                phantom.Properties[PropertyEnum.DamageMult] = Math.Max(0.1f, correctedBase);
            }
        }

        /// <summary>
        /// Deathmatch enemy phantoms keep the MiniBoss Rank tag (it's the
        /// source of their orange glow � see the call site), but that means
        /// the same Mod-bundle stacking ClampEnemyPhantomDamageMult exists
        /// to correct for also lands on HealthMaxMult, which that method
        /// never touches. Same read-then-solve-for-base algebra as
        /// ClampEnemyPhantomDamageMult, applied to both properties,
        /// targeting the exact deathmatch curve values instead of a fixed
        /// cap.
        /// </summary>
        private static void CorrectDeathmatchPhantomStatsAfterRankTag(Avatar phantom, int level)
        {
            float t = Math.Clamp((level - 1) / 59f, 0f, 1f);
            t *= t;
            float targetDmgMult = DeathmatchPhantomDmgMultLvl1 + t * (DeathmatchPhantomDmgMultLvl60 - DeathmatchPhantomDmgMultLvl1);
            float targetHealthMult = ScaleHealthMultForLevel(DeathmatchPhantomHealthMult, level);

            float currentDmgMult = phantom.Properties[PropertyEnum.DamageMult];
            if (Math.Abs(currentDmgMult - targetDmgMult) > 0.01f)
                phantom.Properties[PropertyEnum.DamageMult] = Math.Max(0.1f, (2f * targetDmgMult) - currentDmgMult);

            float currentHealthMult = phantom.Properties[PropertyEnum.HealthMaxMult];
            if (Math.Abs(currentHealthMult - targetHealthMult) > 0.01f)
            {
                phantom.Properties[PropertyEnum.HealthMaxMult] = Math.Max(0.1f, (2f * targetHealthMult) - currentHealthMult);
                phantom.ResetResources(false);
            }
        }

        // VERIFICATION DIAGNOSTIC (2026-07-21) � logs the FINAL DamageMult
        // (post-clamp) and HealthMaxMult (NOT yet clamped � data-gathering
        // only) right after SetSimulated, so the Rank mod's real contribution
        // to both is visible directly rather than guessed at. Specifically
        // aimed at the "MiniBoss (rank 0) phantoms feel tougher to beat than
        // rank 5" report � if MiniBoss's own baked-in Mod bundle happens to
        // carry a larger health/defense bonus than Boss's, this will show it
        // as real numbers instead of another assumption. Remove once settled.
        private static void LogEnemyPhantomFinalStats(Agent phantom, int nemesisRank)
        {
            float dmgMult = phantom.Properties[PropertyEnum.DamageMult];
            float healthMaxMult = phantom.Properties[PropertyEnum.HealthMaxMult];
            long healthMax = phantom.Properties[PropertyEnum.HealthMax];
            PhantomLogger.Info($"[PhantomHero:FinalStats] {phantom} rank={nemesisRank} (0=plain rogue/MiniBoss tag) " +
                $"finalDamageMult={dmgMult:F2} (capped at {EnemyPhantomDamageMultCap:F1}) finalHealthMaxMult={healthMaxMult:F2} finalHealthMax={healthMax}");
        }

        // Rate-limit the "why isn't my phantom attacking" dump to at most one
        // per phantom every 5 seconds so a 500ms tick doesn't spam the log.
        private static readonly Dictionary<ulong, long> s_phantomNextDiagMs = new();
        private const long PhantomDiagIntervalMs = 5000;

        private bool ShouldEmitPhantomDiag(ulong phantomId)
        {
            long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextDiagMs.TryGetValue(phantomId, out long nextAt) && now < nextAt) return false;
            s_phantomNextDiagMs[phantomId] = now + PhantomDiagIntervalMs;
            return true;
        }

        // ---- Threat-based target selection -------------------------------
        //
        // Targets used to be picked by raw proximity ("nearest wins"), which
        // is why a squad would spread its damage across whatever happened to
        // be closest to each member, ignore the thing beating on the player,
        // and leave nearly-dead enemies alive. Candidates are now scored.
        //
        // Distance stays the dominant term so phantoms don't abandon a local
        // fight to chase a high-value target across the arena � the bonuses
        // re-order targets that are all broadly nearby, they don't override
        // proximity outright.
        //
        // IMPORTANT: this scoring changes the ORDER of the candidate list
        // only. It does not change which entities are eligible � the sweep's
        // filters above are untouched, so allies still never enter this list
        // (folding the caller in is exactly what broke combat AI on
        // 2026-07-19; see the long comment in the sweep).
        private const float PhantomThreatDistanceWeight = 100f;
        private const float PhantomThreatPeelWeight     = 60f;   // hostile is attacking my owner
        private const float PhantomThreatFinishWeight   = 50f;   // nearly dead � finish it
        private const float PhantomThreatFocusWeight    = 40f;   // squad focus-fire convergence
        private const float PhantomThreatFinishHpPct    = 0.35f; // "nearly dead" threshold

        // Sticky targeting � a bonus for whatever this phantom committed to
        // LAST tick. The sweep re-sorts from scratch every tick, so two targets
        // whose scores hover near each other can alternate rank tick after
        // tick, and every swap wastes facing, pathing, and any cast that was
        // about to go out. The bonus means an incumbent only loses its spot to
        // a challenger that is genuinely better, not to score jitter.
        //
        // 45 sits deliberately between FocusWeight (40) and FinishWeight (50):
        // squad focus alone cannot yank a phantom off its current target, but
        // a kill-securable one still can.
        private const float PhantomThreatStickyWeight   = 45f;

        // Deathmatch sorts by distance, so stickiness there is a distance
        // DISCOUNT instead of a threat bonus: the committed target's distSq is
        // halved for ordering, which means a rival has to be closer than ~70%
        // of the committed target's distance (sqrt 0.5) to steal the slot.
        private const float DeathmatchStickyDistSqFactor = 0.5f;

        /// <summary>Target this phantom committed to on its previous tick � see PhantomThreatStickyWeight.</summary>
        private static readonly Dictionary<ulong, ulong> s_phantomCommittedTargetId = new();

        // Event Hooks � one-shot-per-encounter tracking for PhantomAIEvents'
        // OnBossSpawn/OnEliteSpawn. Keyed by (phantomId, entityId) so the
        // same boss doesn't re-fire the event every tick it stays in a
        // phantom's candidate sweep, but a DIFFERENT phantom seeing the same
        // boss (or the same phantom seeing a different boss) still fires
        // its own event.
        private static readonly HashSet<(ulong phantomId, ulong entityId)> s_phantomSeenBossIds = new();
        private static readonly HashSet<(ulong phantomId, ulong entityId)> s_phantomSeenEliteIds = new();

        // Squad focus-fire: the first phantom of a given host to commit to a
        // target publishes it here; squadmates get a scoring bonus for the
        // same target while the entry is fresh. Soft convergence, not a hard
        // lock � it decays so the squad can re-target naturally and never
        // gets stuck on something unkillable.
        //
        // Keyed by (hostId, enemyMode), NOT hostId alone. Friendly phantoms
        // and enemy phantoms (nemesis/rogue) for the SAME human player share
        // one hostId, but hunt in opposite directions � a friendly phantom's
        // committed target is a hostile mob, an enemy phantom's is the real
        // player or a friendly phantom. Keying on hostId alone meant every
        // tick's write from one side silently clobbered the other side's
        // entry (last-writer-wins on the same dictionary slot), so the enemy
        // squad's OWN focus-fire coordination was intermittently reset by
        // unrelated friendly-phantom writes and vice versa. The two candidate
        // ID spaces never overlap, so this never caused a phantom to target
        // the wrong side � it just made focus-fire flakier than intended.
        // Found during a 2026-07-21 balance investigation, fixed regardless
        // of the tuning question since it's a real, isolated correctness bug.
        private const long PhantomSquadFocusTtlMs = 5000;
        private static readonly Dictionary<(ulong hostId, bool enemyMode), (ulong targetId, long expiryMs)> s_phantomSquadFocus = new();

        private static ulong GetPhantomSquadFocusTarget(ulong hostId, bool enemyMode, long nowMs)
        {
            if (hostId == 0) return 0;
            if (s_phantomSquadFocus.TryGetValue((hostId, enemyMode), out var entry) == false) return 0;
            if (nowMs >= entry.expiryMs) return 0;
            return entry.targetId;
        }

        private static void SetPhantomSquadFocusTarget(ulong hostId, bool enemyMode, ulong targetId, long nowMs)
        {
            if (hostId == 0 || targetId == 0) return;
            s_phantomSquadFocus[(hostId, enemyMode)] = (targetId, nowMs + PhantomSquadFocusTtlMs);
        }

        // ---- Personality profiles (Utility AI archetype weighting) -------
        //
        // Assigns each phantom a fixed behavioral archetype on first use,
        // analogous to build-based role weighting in documented companion AI
        // (Guild Wars 2 Hero AI, Diablo III follower presets). Personality
        // only scales the EXISTING threat weights above and the human-
        // simulation timings below � it never changes candidate eligibility,
        // so it can't make a phantom attack something it otherwise couldn't
        // reach or see.
        private enum PhantomPersonality
        {
            Aggressive,  // favors Finish/Distance � rushes to close and kill
            Defensive,   // favors Peel � prioritizes protecting its owner
            Supportive,  // favors Focus (squad convergence) over finishing blows
            Reckless,    // aggressive weighting, higher mistake rate
            Smart,       // balanced weighting, lowest mistake rate, fastest reaction
        }

        private static readonly Dictionary<ulong, PhantomPersonality> s_phantomPersonality = new();

        private static PhantomPersonality GetPhantomPersonality(ulong phantomId, PrototypeId heroRef, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (s_phantomPersonality.TryGetValue(phantomId, out PhantomPersonality p)) return p;

            // Per-hero file override (Data/Game/PhantomHeroes/AIProfiles/<Hero>.json,
            // PersonalityOverride field) � hand-edited, takes priority over the
            // random roll below when set to anything but "Auto".
            PhantomHeroAIProfile fileProfile = Player.GetPhantomAIProfile(heroRef);
            if (fileProfile != null && Enum.TryParse(fileProfile.PersonalityOverride, true, out PhantomPersonality filePersonality))
            {
                s_phantomPersonality[phantomId] = filePersonality;
                return filePersonality;
            }

            // Weighted roll, not uniform: Smart/Aggressive are the common
            // baseline, Reckless/Supportive are less common flavor variants.
            int roll = rng.Next(100);
            p = roll switch
            {
                < 30 => PhantomPersonality.Aggressive,
                < 55 => PhantomPersonality.Smart,
                < 75 => PhantomPersonality.Defensive,
                < 90 => PhantomPersonality.Supportive,
                _ => PhantomPersonality.Reckless,
            };
            s_phantomPersonality[phantomId] = p;
            return p;
        }

        /// <summary>Per-term threat weight multiplier for a personality � see PhantomPersonality.</summary>
        private static void GetPersonalityThreatMult(PhantomPersonality p, out float distanceMult, out float peelMult, out float finishMult, out float focusMult)
        {
            distanceMult = peelMult = finishMult = focusMult = 1f;
            switch (p)
            {
                case PhantomPersonality.Aggressive:
                    finishMult = 1.4f; distanceMult = 1.2f; peelMult = 0.7f;
                    break;
                case PhantomPersonality.Defensive:
                    peelMult = 1.6f; finishMult = 0.8f;
                    break;
                case PhantomPersonality.Supportive:
                    focusMult = 1.5f; finishMult = 0.7f;
                    break;
                case PhantomPersonality.Reckless:
                    finishMult = 1.3f; distanceMult = 1.3f;
                    break;
                case PhantomPersonality.Smart:
                    // Balanced � Smart's edge is lower mistake rate and faster
                    // reaction time (below), not skewed threat weights.
                    break;
            }
        }

        // ---- Human simulation layer ----------------------------------------
        //
        // Reaction delay: documented bot-avoidance/humanization technique �
        // a short delay between a stimulus (a brand-new primary target) and
        // the AI's response (its first attack on it), instead of reacting
        // the same tick a target is acquired. Modeled on typical human
        // visual-motor reaction time (~200-350ms), the same baseline range
        // used for companion-AI humanization elsewhere (e.g. Warframe
        // Specter response delay). Only gates the FIRST attack on a newly
        // acquired target � an already-engaged target still fires on the
        // normal attack cooldown with no added delay, matching the
        // documented pattern that reaction time applies to new stimuli, not
        // sustained action.
        private static readonly Dictionary<ulong, ulong> s_phantomReactionTargetId = new();
        private static readonly Dictionary<ulong, long> s_phantomReactionReadyMs = new();

        private const int PhantomReactionDelayMinMs = 180;
        private const int PhantomReactionDelayMaxMs = 380;
        // Smart personality reacts faster (closer to a skilled player).
        private const int PhantomReactionDelaySmartMinMs = 120;
        private const int PhantomReactionDelaySmartMaxMs = 220;

        /// <summary>
        /// True once the reaction-delay window for this phantom's CURRENT
        /// target has elapsed. Checked once per tick alongside the existing
        /// attack-cooldown gate in UpdatePhantomHunt's attackReady check.
        /// </summary>
        private static bool IsPhantomReactionReady(ulong phantomId, ulong targetId, PhantomPersonality personality, long nowMs, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (s_phantomReactionTargetId.TryGetValue(phantomId, out ulong lastTargetId) == false || lastTargetId != targetId)
            {
                // New target acquired this tick � roll a fresh delay window.
                s_phantomReactionTargetId[phantomId] = targetId;
                int minMs = personality == PhantomPersonality.Smart ? PhantomReactionDelaySmartMinMs : PhantomReactionDelayMinMs;
                int maxMs = personality == PhantomPersonality.Smart ? PhantomReactionDelaySmartMaxMs : PhantomReactionDelayMaxMs;
                s_phantomReactionReadyMs[phantomId] = nowMs + minMs + rng.Next(maxMs - minMs);
                return false;
            }

            return s_phantomReactionReadyMs.TryGetValue(phantomId, out long readyMs) == false || nowMs >= readyMs;
        }

        // Mistake rate: documented imperfect-decision-modeling technique � a
        // small chance the AI picks its second-best option instead of the
        // objectively best one, so target selection doesn't read as a
        // perfect optimizer. Personality-tuned: Smart phantoms rarely
        // misjudge, Reckless ones misjudge more often (impulsive picks).
        private static float GetPersonalityMistakeRate(PhantomPersonality p) => p switch
        {
            PhantomPersonality.Smart => 0.03f,
            PhantomPersonality.Reckless => 0.18f,
            _ => 0.08f,
        };

        private static void DumpPhantomHuntDiag(Agent phantom, Vector3 phantomPos, WorldEntity picked,
            float pickedDistSq, List<(WorldEntity we, float distSq, string reason)> rejected,
            Region region, Sphere sweepSphere, MHServerEmu.Games.Entities.EntityRegionSPContext ctx)
        {
            // Full state dump of every hostile Agent in the sweep sphere so we
            // can identify exactly which flag is stopping the attack on a
            // cutscene boss. Runs at most once per 5s per phantom.
            var sb = new System.Text.StringBuilder();
            sb.Append($"[PhantomHero:Diag] {phantom} pos={phantomPos.ToStringNames()} ");
            if (picked != null)
                sb.Append($"picked={picked} dist={MathF.Sqrt(pickedDistSq):F0}");
            else
                sb.Append("picked=<none>");

            int n = 0;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id) continue;
                if (we is not Agent) continue;
                if (we.IsDead) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                if (n++ >= 8) { sb.Append(" ...(more truncated)"); break; }
                float d = Vector3.Distance2D(we.RegionLocation.Position, phantomPos);
                string allianceRef = we.Alliance != null ? we.Alliance.DataRef.GetName() : "<null>";
                sb.Append($" | {we} dist={d:F0} dormant={we.IsDormant} untargetable={we.IsUntargetable} unaffectable={we.IsUnaffectable} affectedByPowers={we.IsAffectedByPowers()} sim={we.IsSimulated} inWorld={we.IsInWorld} alliance={allianceRef}");
            }
            if (rejected != null)
            {
                sb.Append(" | rejected=[");
                for (int i = 0; i < rejected.Count && i < 5; i++)
                    sb.Append($"{rejected[i].we}({rejected[i].reason},{MathF.Sqrt(rejected[i].distSq):F0}) ");
                sb.Append(']');
            }
            PhantomLogger.Info(sb.ToString());
        }

        // Fraction of HealthMax at or below which a phantom will try to
        // medkit itself instead of continuing whatever it was doing.
        private const float PhantomSelfHealHpThreshold = 0.35f;

        // Own throttle, independent of the real medkit power's own
        // (probably short, spammable-by-design) cooldown. Without this, a
        // phantom whose HP sits at/under the threshold for an extended
        // stretch of a long fight (incoming damage outpacing what one heal
        // restores) re-triggers the instant the real cooldown clears and
        // preempts the ENTIRE combat tick every single time � chain-looping
        // into "spend most ticks healing, almost none attacking" the longer
        // that stretch runs. This mirrors the exact "aggressive early,
        // passive later" shape of a real, separate pre-existing bug found
        // the same session (power-blacklist duration mismatch, see
        // PhantomTransientPowerBlacklistMs) � same failure shape, different
        // mechanism, so hardened preventively even though it wasn't
        // confirmed as the cause of that specific report.
        private const long PhantomSelfHealThrottleMs = 8_000;
        // Enemy/nemesis phantoms get a much longer throttle than friendly
        // squadmates. Their HealthMax is already multiplied 8-32x by rank
        // (Player.NemesisHealthMultForRank) � the same medkit heal a real
        // player uses is a %-of-HealthMax-scale effect on that data, not
        // determinable exactly from source, but an 8s throttle would let a
        // ranked nemesis camp the low-HP threshold and out-heal a solo
        // player's damage indefinitely, effectively making it unkillable
        // rather than "a tough fight." A rare clutch heal is fine (mirrors
        // a real player's own medkit use); a repeatable stalling tactic is
        // not. Flagged in the 2026-07-20 audit, hardened here rather than
        // shipped unverified.
        private const long PhantomSelfHealThrottleMsEnemy = 45_000;
        private static readonly Dictionary<ulong, long> s_phantomNextSelfHealMs = new();

        // ---- Survival Logic -------------------------------------------
        //
        // Soft/hard retreat thresholds � a standard AI survival heuristic:
        // create distance from the threat well before death, then fully
        // disengage if that isn't enough. Checked in UpdatePhantomHunt right
        // after target commitment, AFTER self-heal already had its shot at
        // the top of the tick � this is the fallback survivability response
        // for when healing isn't available (on cooldown, throttled, or (on
        // 1.48) no such power exists at all), not a competing one.
        private const float PhantomSoftRetreatHpPct = 0.40f;
        private const float PhantomHardRetreatHpPct = 0.15f;
        // Deliberately wider than any real weapon-range standoff � this is
        // about survival distance, not attack range, so it can exceed what
        // the phantom's own kit could ever justify on its own.
        private const float PhantomSoftRetreatStandoff = 700f;

        /// <summary>
        /// Below PhantomSoftRetreatHpPct: kites away from the current threat
        /// at a wider-than-normal standoff (reuses TryPhantomKite, the same
        /// primitive ranged kits already use to hold weapon range, just HP-
        /// triggered instead of kit-triggered). Below PhantomHardRetreatHpPct,
        /// friendly phantoms abandon the fight and run to the human player
        /// instead of retreating in a random direction, so they end up
        /// somewhere they can actually be helped. Enemy phantoms have no
        /// "owner" to run to, so soft retreat is their survival ceiling.
        /// Returns true if it took over movement this tick (caller should
        /// skip its normal engagement logic), false otherwise (full HP,
        /// no threat, kite already on its own cooldown, etc.).
        /// </summary>
        // Fires OnPhantomLowHP once per dip below the threshold, not every
        // single tick spent below it � cleared once the phantom recovers.
        private static readonly HashSet<ulong> s_phantomLowHpNotified = new();
        // Same one-shot-per-dip pattern, keyed by the real avatar's own id � see OnPhantomTick's OnPlayerLowHP hook.
        private static readonly HashSet<ulong> s_avatarLowHpNotified = new();

        private bool TryPhantomSurvivalRetreat(Agent phantom, Region region, WorldEntity threat, float threatDistSq, bool enemyMode, long nowMs, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (phantom.IsDead || phantom.IsInWorld == false) return false;
            if (threat == null) return false;

            float healthMax = phantom.Properties[PropertyEnum.HealthMax];
            if (healthMax <= 0f) return false;
            float hpPct = (float)phantom.Properties[PropertyEnum.Health] / healthMax;
            if (hpPct > PhantomSoftRetreatHpPct)
            {
                s_phantomLowHpNotified.Remove(phantom.Id);
                return false;
            }

            if (s_phantomLowHpNotified.Add(phantom.Id))
                PhantomAIEvents.RaisePhantomLowHP(phantom);

            if (enemyMode == false && hpPct <= PhantomHardRetreatHpPct)
            {
                var hardLoco = phantom.Locomotor;
                if (hardLoco != null)
                {
                    var hardOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                    hardLoco.FollowEntity(Id, PhantomFollowStopMin, PhantomFollowStopMin, ref hardOpts, false);
                    return true;
                }
            }

            Vector3 threatPos = threat.RegionLocation.Position;
            return TryPhantomKite(phantom, region, threatPos, MathF.Sqrt(threatDistSq), PhantomSoftRetreatStandoff, nowMs, rng);
        }

        /// <summary>
        /// Fires the same medkit/self-heal power real players use
        /// (GlobalsPrototype.AvatarHealPower) if this phantom is below
        /// PhantomSelfHealHpThreshold and the power isn't on cooldown.
        /// Returns true if it fired (caller should skip the rest of its
        /// tick), false if not applicable (full HP, no such power, on
        /// cooldown, throttled, etc.) so the normal hunt/attack/revive
        /// logic proceeds as usual.
        /// </summary>
        private bool TryPhantomSelfHeal(Agent phantom, bool enemyMode)
        {
            if (phantom.IsDead || phantom.IsInWorld == false) return false;

            float healthMax = phantom.Properties[PropertyEnum.HealthMax];
            if (healthMax <= 0f) return false;
            float health = phantom.Properties[PropertyEnum.Health];
            if (health / healthMax > PhantomSelfHealHpThreshold) return false;

            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextSelfHealMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt)
                return false;

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
            PrototypeId healPowerRef = GameDatabase.GlobalsPrototype?.AvatarHealPower ?? PrototypeId.Invalid;
#else
            // GlobalsPrototype.AvatarHealPower doesn't exist under 1.48 --
            // no known equivalent to grant instead, so self-heal simply
            // never fires on that version.
            PrototypeId healPowerRef = PrototypeId.Invalid;
#endif
            if (healPowerRef == PrototypeId.Invalid) return false;

            Power healPower = phantom.GetPower(healPowerRef);
            if (healPower == null || healPower.IsOnCooldown()) return false;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            var settings = new PowerActivationSettings(phantom.Id, phantomPos, phantomPos);
            settings.Flags |= PowerActivationSettingsFlags.NotifyOwner;

            try
            {
                PowerUseResult result = phantom.ActivatePower(healPowerRef, ref settings);
                if (result == PowerUseResult.Success)
                {
                    long throttleMs = enemyMode ? PhantomSelfHealThrottleMsEnemy : PhantomSelfHealThrottleMs;
                    s_phantomNextSelfHealMs[phantom.Id] = nowMs + throttleMs;

                    // VERIFICATION DIAGNOSTIC (2026-07-21) � part of the "rank
                    // 4/5 nemesis out-heals everything" balance investigation.
                    // Logs the ACTUAL applied heal (post-mitigation, same idea
                    // as the DPS meter's real-damage hook) rather than assuming
                    // the medkit heals a fixed amount. If ActivatePower applies
                    // the heal asynchronously rather than synchronously,
                    // healthAfter will read unchanged here and that itself is
                    // useful information � remove once this is settled with
                    // real numbers.
                    float healthAfterRaw = phantom.Properties[PropertyEnum.Health];
                    float healedAmount = healthAfterRaw - health;
                    bool hadRank = TryGetEnemyPhantomRank(phantom.Id, out int nemesisRank, out int nemesisLevel);
                    PhantomLogger.Info($"[PhantomHero:SelfHeal] {phantom} enemyMode={enemyMode} rank={(hadRank ? nemesisRank : -1)} usedMedkitAt={health / healthMax:P0} healthMax={healthMax:F0} healedAmount={healedAmount:F0} ({healedAmount / healthMax:P0} of max) healthAfter={healthAfterRaw / healthMax:P0}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                PhantomLogger.Warn($"[PhantomHero:SelfHeal] {phantom.Id:X} medkit threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fires one of the phantom's own movement/dash powers toward a
        /// random nearby spot, purely to break up squad clustering � see
        /// PhantomSpacingDashCooldownMs for why. Returns true if it fired
        /// (caller should skip its normal attack this tick so the phantom
        /// doesn't try to cast two powers at once), false otherwise (not
        /// its turn yet, no movement power in the kit, or activation
        /// failed).
        /// </summary>
        private bool TryPhantomSpacingDash(Agent phantom, Region region, MHServerEmu.Core.System.Random.GRandom rng)
        {
            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextDashMs.TryGetValue(phantom.Id, out long nextAt) == false)
            {
                // First time this phantom has ever been checked � seed the
                // cooldown instead of treating "no entry yet" as
                // "immediately eligible." Without this, a phantom's very
                // first attack-eligible tick fired a dash instead of an
                // attack � first contact with an enemy read as "dodge away"
                // (confirmed live 2026-07-20 audit).
                s_phantomNextDashMs[phantom.Id] = nowMs + PhantomSpacingDashCooldownMs + (long)(rng.NextDouble() * PhantomSpacingDashJitterMs);
                return false;
            }
            if (nowMs < nextAt) return false;

            var pc = phantom.PowerCollection;
            if (pc == null) return false;

            PrototypeId dashPowerRef = PrototypeId.Invalid;
            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp is not MovementPowerPrototype) continue;
                if (pp.Activation == PowerActivationType.Passive || pp.IsToggled || pp.IsTravelPower) continue;
                if (power.IsOnCooldown()) continue;
                dashPowerRef = kvp.Key;
                break;
            }

            // Roll the next eligible time regardless of outcome � a phantom
            // with no ready movement power right now shouldn't be re-checked
            // every 500ms tick until one comes off cooldown.
            s_phantomNextDashMs[phantom.Id] = nowMs + PhantomSpacingDashCooldownMs + (long)(rng.NextDouble() * PhantomSpacingDashJitterMs);

            if (dashPowerRef == PrototypeId.Invalid) return false;

            Vector3 phantomPos = phantom.RegionLocation.Position;
            Vector3 destPos = ChoosePhantomDashDestination(region, phantomPos, rng, phantom.Bounds.Radius);
            var settings = new PowerActivationSettings(0, destPos, phantomPos);
            settings.Flags |= PowerActivationSettingsFlags.NotifyOwner;

            try
            {
                PowerUseResult result = phantom.ActivatePower(dashPowerRef, ref settings);
                return result == PowerUseResult.Success;
            }
            catch (Exception ex)
            {
                PhantomLogger.Warn($"[PhantomHero:Dash] {phantom.Id:X} spacing dash threw: {ex.Message}");
                return false;
            }
        }

        private PowerUseResult TryPhantomAttack(Agent phantom, WorldEntity target, float targetDistSq, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (target == null || phantom.PowerCollection == null) return PowerUseResult.GenericError;
            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Refill Endurance so InsufficientEndurance doesn't gate every non-basic
            // power. Phantom has no resource regen wiring; we just keep the pool at
            // ceiling. Loop across every ManaType the avatar declares so multi-pool
            // heroes (Iron Man / Nova / Storm) all get topped up.
            //
            // Avatar-only: PrimaryResourceBehaviors is declared on
            // AvatarPrototype, not AgentPrototype � team-ups have no
            // equivalent concept in the game's own data model (confirmed:
            // AgentTeamUpPrototype carries no such field), so there is
            // nothing to refill for them here. If a team-up power ever
            // turns out to gate on Endurance in practice, that would surface
            // as InsufficientEndurance in the attack-rejection log and can
            // be revisited then rather than guessed at now.
            if (phantom is Avatar avatarForMana)
            {
                foreach (PrimaryResourceManaBehaviorPrototype manaBehavior in avatarForMana.GetPrimaryResourceManaBehaviors())
                {
                    var manaType = manaBehavior.ManaType;
                    float max = phantom.Properties[PropertyEnum.EnduranceMax, manaType];
                    if (max > 0) phantom.Properties[PropertyEnum.Endurance, manaType] = max;
                }
            }

            float targetDist = MathF.Sqrt(targetDistSq);

            // Build the candidate list with real prioritization instead of pure
            // reservoir sampling.
            //
            //   Filters (hard rejects):
            //     - is a Movement / Travel / Passive / Toggled power
            //     - is not NormalPower category
            //     - power.GetRange() < target distance (would return OutOfPosition)
            //     - power is currently on cooldown
            //     - ultimates: additionally gated behind a 20-minute
            //       per-phantom timer (see s_phantomNextUltimateMs)
            //
            //   Score = cooldown duration in ms (used as a proxy for hit weight �
            //   powers with longer cooldowns are baked bigger, and it's the only
            //   universal numeric signal we can get without a per-hero damage table).
            //
            //   Pick strategy: a ready ultimate wins outright � 20 minutes
            //   apart it should never lose a coin flip to a basic attack.
            //   Otherwise sort survivors by score desc, weighted-random among
            //   the top 5. Favors real cooldown-worthy hits while still varying,
            //   and always fires the basic (0 cd) when nothing bigger is available.
            // Skill Rotation opt-in � user picks a preferred power per hero
            // in the app. If it's in the candidate pool AND ready AND in
            // range, it wins outright (same tier as an ultimate). Blank
            // (Invalid) falls through to the default weighted picker.
            PrototypeId preferredPowerRef = PhantomHost?.GetPreferredPower(phantom.PrototypeDataRef) ?? PrototypeId.Invalid;

            // (powerRef, estimatedDamage, cooldownMs) � damage drives the
            // pick; cooldown is retained as the fallback signal for powers
            // whose damage isn't expressed through DamageBase.
            var candidates = ListPool<(PrototypeId, float, long)>.Get();

            // Populated lazily, only if this kit actually has an AoE power to
            // score � kits without one never pay for the spatial sweep.
            List<Vector3> aoeClusterPositions = null;
            try
            {
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                PrototypeId readyUltimate = PrototypeId.Invalid;
                PrototypeId readyPreferred = PrototypeId.Invalid;

                // Fallback for the "every candidate is blacklisted at once"
                // case (small kit, several powers hit RestrictiveCondition/
                // WeaponMissing around the same time) � without this, the
                // phantom returns OutOfPosition and does nothing every tick
                // until the longest blacklist entry (up to
                // PhantomPowerBlacklistMs = 10 minutes) expires, which reads
                // as "stands still for a long time, then randomly attacks"
                // (confirmed live 2026-07-25). Tracks whichever blacklisted-
                // but-otherwise-in-range/off-cooldown power comes off its
                // bench soonest, so it can be used instead of full idling if
                // nothing else is available this tick.
                PrototypeId fallbackBlacklistedPower = PrototypeId.Invalid;
                long fallbackBlacklistedExpiresAt = long.MaxValue;

                foreach (var kvp in phantom.PowerCollection)
                {
                    PowerCollectionRecord rec = kvp.Value;
                    Power power = rec?.Power;
                    if (power == null) continue;
                    PowerPrototype pp = power.Prototype;
                    if (pp == null) continue;
                    if (pp is MovementPowerPrototype) continue;
                    if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                    if (pp.Activation == PowerActivationType.Passive) continue;
                    if (pp.IsToggled) continue;
                    if (pp.IsTravelPower) continue;

                    // A power may only be picked here if it's actually meant
                    // for the relationship it would be used against. Without
                    // this, support/buff powers (KittyPryde/BuddySystem,
                    // SheHulk/LawyerUp, etc.) could make it into the
                    // candidate pool and get their hard target set to the
                    // real player when a phantom is hostile-aligned
                    // (rogue/nemesis) � normally EvalCanTrigger would catch a
                    // misfire like that, but Power.CanTrigger bypasses
                    // EvalCanTrigger entirely for every phantom-owned power
                    // (see Power.Validation.cs), so this is the only
                    // remaining gate. TargetsEnemy/TargetsFriendly are the
                    // same fields Power.Validation.cs's AI-specific target
                    // check already treats as authoritative.
                    //
                    // Friendly (party) phantoms CAN target the real player �
                    // that's intentional, so their squadmate can buff/heal
                    // them � but only with a power flagged TargetsFriendly,
                    // never one that's only meant to hit hostiles. Hostile
                    // (rogue/nemesis) phantoms targeting the player must use
                    // a TargetsEnemy power, which excludes every buff/support
                    // power by definition � this is what stops enemy
                    // phantoms from ever landing a "buff" on the player.
                    var targetingReach = pp.GetTargetingReach();
                    if (targetingReach == null) continue;
                    bool targetIsHostileToPhantom = phantom.IsHostileTo(target);
                    if (targetIsHostileToPhantom)
                    {
                        if (targetingReach.TargetsEnemy == false) continue;
                    }
                    else
                    {
                        if (targetingReach.TargetsFriendly == false) continue;
                    }

                    // GetRange() reporting <= 0 does NOT mean "unlimited" �
                    // it means this power has no genuine ranged-targeting
                    // distance in its data (melee reach, self-centered
                    // effects, etc). Treating that as "no limit" is what let
                    // ANY such power � not just melee ones � fire from all
                    // the way out at PhantomAttackRange (1200u), which is the
                    // "attacking way before they get close" bug players saw,
                    // both for melee attacks and for ranged powers whose
                    // GetRange() happens to come back 0 too. Only a power
                    // with a genuine positive declared range gets to use that
                    // range as its own gate; everything else is held to true
                    // melee reach.
                    // Mirror the engine's own activation-range test rather than
                    // approximating it - see Power.Validation.cs's
                    // IsInRangeInternal. targetDist here is already EDGE-based
                    // (the sweep subtracts the target's Bounds.Radius), which is
                    // the same quantity the engine compares, so no radius is
                    // added back on this side.
                    float pRange = power.GetRange();
                    float engineRange = MathF.Max(phantom.Bounds.Radius, pRange) + 5f;
                    if (targetDist > engineRange) continue;

                    if (power.IsOnCooldown()) continue;

                    // Skip powers that recently failed with power-specific
                    // errors (RestrictiveCondition / WeaponMissing) � they
                    // won't start working by themselves, and their big
                    // cooldown weights would otherwise get them picked
                    // every single tick.
                    if (s_phantomPowerBlacklist.TryGetValue((phantom.Id, rec.PowerPrototypeRef), out long blacklistExpiresAt) && nowMs < blacklistExpiresAt)
                    {
                        if (blacklistExpiresAt < fallbackBlacklistedExpiresAt)
                        {
                            fallbackBlacklistedExpiresAt = blacklistExpiresAt;
                            fallbackBlacklistedPower = rec.PowerPrototypeRef;
                        }
                        continue;
                    }

                    // Ultimates fire on any target, but at most once per
                    // 20 minutes per phantom (on top of whatever cooldown
                    // the power data itself carries). The original
                    // FullscreenMovie failure that got them blanket-banned
                    // was the phantom-Player fullscreen-state bug, fixed in
                    // Player.PlayKismetSeq.
                    string pName = pp.DataRef.GetName() ?? string.Empty;
                    if (pName.EndsWith("Ultimate.prototype", StringComparison.Ordinal))
                    {
                        if (s_phantomNextUltimateMs.TryGetValue(phantom.Id, out long ultReadyAt) && nowMs < ultReadyAt)
                            continue;
                        readyUltimate = rec.PowerPrototypeRef;
                        continue; // not part of the weighted pool � it wins outright below
                    }

                    long cdMs = (long)power.GetCooldownDuration().TotalMilliseconds;
                    float estDamage = EstimatePhantomPowerDamage(power, phantom.CombatLevel);

                    // AoE awareness: an area power's real value is its damage
                    // times how many enemies it actually catches. Scoring it as
                    // single-target damage (what we did before) meant a phantom
                    // would poke a pack of eight with a single-target jab, and
                    // conversely dump a big cone into one lone straggler.
                    //
                    // Multiplying by the hit count makes both cases fall out
                    // naturally with no special-casing: against one enemy the
                    // multiplier is 1 and AoE competes on raw damage alone;
                    // against a cluster it scales up and wins, which is exactly
                    // when you'd want it spent.
                    if (Power.TargetsAOE(pp))
                    {
                        if (aoeClusterPositions == null)
                        {
                            aoeClusterPositions = ListPool<Vector3>.Get();
                            GatherPhantomAoeCluster(phantom, target.RegionLocation.Position, aoeClusterPositions);
                        }

                        int hits = CountPhantomAoeHits(aoeClusterPositions,
                            target.RegionLocation.Position, power.GetAOERadius(), pp.MaxAOETargets);
                        estDamage *= hits;
                    }

                    candidates.Add((rec.PowerPrototypeRef, estDamage, cdMs));

                    // Note whether the user's preferred power made it through
                    // every gate (in range, not on cooldown, not blacklisted).
                    if (preferredPowerRef != PrototypeId.Invalid && rec.PowerPrototypeRef == preferredPowerRef)
                        readyPreferred = preferredPowerRef;
                }

                if (candidates.Count == 0 && readyUltimate == PrototypeId.Invalid)
                {
                    // Nothing legitimately usable � but if the ONLY reason is
                    // that every in-range/off-cooldown power is benched, try
                    // the one closest to coming off its bench rather than
                    // freezing for the rest of the blacklist window. Worst
                    // case it fails again and re-blacklists normally; best
                    // case whatever condition benched it (RestrictiveCondition
                    // clearing, etc.) has already resolved.
                    if (fallbackBlacklistedPower == PrototypeId.Invalid)
                    {
                        // NOT a positioning failure - every power was on
                        // cooldown or filtered out. Returning OutOfPosition here
                        // (what this used to do) was measured live producing
                        // "all candidates rejected" at dist=101, i.e. standing
                        // adjacent to an enemy doing nothing while the log
                        // blamed position. AbilityMissing is the honest result
                        // and keeps the caller from treating it as a range
                        // problem it can never fix by walking.
                        if (s_phantomNoPowerLogged.Add(phantom.Id))
                            PhantomLogger.Info($"[PhantomHero:NoPower] {phantom} has no usable power vs {target} at dist={targetDist:F0} (all on cooldown / filtered)");
                        return PowerUseResult.AbilityMissing;
                    }

                    candidates.Add((fallbackBlacklistedPower, 0f, 0));
                }

                PrototypeId chosenPower;
                bool chosenIsUltimate = readyUltimate != PrototypeId.Invalid;
                if (chosenIsUltimate)
                {
                    chosenPower = readyUltimate;
                }
                else if (readyPreferred != PrototypeId.Invalid)
                {
                    chosenPower = readyPreferred;
                }
                else
                {
                    // Rank by ESTIMATED DAMAGE, not cooldown.
                    //
                    // The old score was "cooldown duration in ms", used as a
                    // proxy for hit weight on the theory that longer-cooldown
                    // powers are baked bigger. That's only loosely true and it
                    // mis-ranked plenty of kits (long-cooldown utility powers
                    // outranked genuinely hard-hitting low-cooldown ones).
                    // EstimatePhantomPowerDamage reads the real DamageBase
                    // curve the payload will use, so the ranking now reflects
                    // actual output.
                    //
                    // Powers that report 0 damage aren't necessarily harmless
                    // � DoTs, summons and proc-driven powers deal their damage
                    // outside DamageBase. Dropping them would silently delete
                    // whole kits from the rotation, so they stay in the pool
                    // and are scored off the OLD cooldown proxy, normalized
                    // into the damage scale and deliberately capped below the
                    // best known-damage power so they're used but not favored.
                    float maxDamage = 0f;
                    for (int i = 0; i < candidates.Count; i++)
                        if (candidates[i].Item2 > maxDamage) maxDamage = candidates[i].Item2;

                    if (maxDamage > 0f)
                    {
                        // Map unknown-damage powers onto the damage scale via
                        // their cooldown, capped at 40% of the best real hit.
                        const float UnknownDamageCapPct = 0.40f;
                        long maxCdAmongUnknown = 0;
                        for (int i = 0; i < candidates.Count; i++)
                            if (candidates[i].Item2 <= 0f && candidates[i].Item3 > maxCdAmongUnknown)
                                maxCdAmongUnknown = candidates[i].Item3;

                        for (int i = 0; i < candidates.Count; i++)
                        {
                            if (candidates[i].Item2 > 0f) continue;
                            float frac = maxCdAmongUnknown > 0
                                ? (float)candidates[i].Item3 / maxCdAmongUnknown
                                : 0.5f;
                            candidates[i] = (candidates[i].Item1,
                                             maxDamage * UnknownDamageCapPct * frac,
                                             candidates[i].Item3);
                        }
                    }
                    else
                    {
                        // No candidate exposes DamageBase at all (unusual �
                        // e.g. a kit that's entirely proc/summon driven).
                        // Fall back wholesale to the original cooldown proxy
                        // so behavior is no worse than before this change.
                        for (int i = 0; i < candidates.Count; i++)
                            candidates[i] = (candidates[i].Item1, candidates[i].Item3, candidates[i].Item3);
                    }

                    // Sort by score desc � biggest hitter first.
                    candidates.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

                    // Take top 5 (or fewer). Weighted-random pick � weight = 1 + cooldownMs/1000
                    // so a 5s power is ~6x more likely than a basic (0s) attack.
                    //
                    // BUG FIXED (2026-07-20): `(long)rng.NextDouble()` truncates a
                    // [0,1) double to 0 BEFORE the multiply � the old expression
                    // computed `((long)rng.NextDouble() * totalWeight * 1000L) % ...`,
                    // so `roll` was always exactly 0 regardless of the actual roll.
                    // That made this whole "weighted random" block a no-op: every
                    // phantom deterministically picked candidates[0] (the sorted
                    // longest-cooldown power) every single time, never varying its
                    // rotation. Cast happens AFTER the multiply now.
                    // Weight is now relative damage rather than raw cooldown
                    // seconds. candidates[0] is the highest scorer after the
                    // sort above, so normalizing against it keeps every weight
                    // in the same 1..6 spread the cooldown version produced �
                    // the top pick stays ~6x more likely than the weakest of
                    // the top 5, preserving the tuned feel while making the
                    // ranking itself meaningful.
                    int take = Math.Min(5, candidates.Count);
                    float best = candidates[0].Item2;
                    if (best <= 0f) best = 1f;

                    Span<float> weights = stackalloc float[take];
                    float totalWeight = 0f;
                    for (int i = 0; i < take; i++)
                    {
                        float w = 1f + (candidates[i].Item2 / best) * 5f;
                        weights[i] = w;
                        totalWeight += w;
                    }

                    float roll = (float)(rng.NextDouble() * totalWeight);
                    if (roll < 0f) roll = 0f;

                    chosenPower = candidates[0].Item1;
                    float acc = 0f;
                    for (int i = 0; i < take; i++)
                    {
                        acc += weights[i];
                        if (roll < acc) { chosenPower = candidates[i].Item1; break; }
                    }
                }
                candidates.Clear();

                // Pre-generate FXRandomSeed BEFORE the call so it's stored
                // in settings.FXRandomSeed. Without this the seed is 0,
                // ArchiveMessageBuilder auto-generates a random one for
                // the outbound NetMessageActivatePower (line 357), but
                // that generated seed never gets back into the
                // PowerApplication / PowerPayload / PowerResult. Client
                // sees ActivatePower(fxSeed=N) then PowerResult(fxSeed=0)
                // � mismatched. Body-emitter particle systems that vary
                // by seed treat 0 as "no effect" or drop it because the
                // cast doesn't correlate to the hit. Missile / projectile
                // FX works even with seed=0 because those spawn from a
                // separate Missile entity.
                //
                // ServerCombo forces the broadcast path (Power.cs line
                // 3680) to include the owner client and skip the combo-
                // effect early-out.
                int fxSeed = rng.Next(1, 10000);
                var settings = new PowerActivationSettings(target.Id, target.RegionLocation.Position, phantomPos)
                {
                    Flags = PowerActivationSettingsFlags.NotifyOwner | PowerActivationSettingsFlags.ServerCombo,
                    FXRandomSeed = fxSeed,
                    PowerRandomSeed = fxSeed,
                };
                var result = phantom.ActivatePower(chosenPower, ref settings);

                // Charge-and-release powers: the first activation only
                // STARTS the charge and waits for a button-release message
                // that will never come (no client). Release immediately �
                // ReleaseVariableActivation schedules the actual firing at
                // the power's MinReleaseTimeMS on the game scheduler, so
                // the shot still charges the minimum time and then goes
                // off on its own. Without this, phantoms wound up holding
                // the charge until the stuck-power watchdog cancelled it
                // 5 seconds later, and the power never fired at all.
                if (result == PowerUseResult.Success)
                {
                    Power chosenPowerInstance = phantom.PowerCollection?.GetPower(chosenPower);
                    if (chosenPowerInstance?.Prototype?.ExtraActivation is SecondaryActivateOnReleasePrototype)
                    {
                        try { chosenPowerInstance.ReleaseVariableActivation(ref settings); }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] ReleaseVariableActivation({chosenPower.GetName()}) failed on {phantom.Id:X}: {ex.Message}"); }
                    }
                }

                // Start the 20-minute ultimate timer only on a successful
                // cast � a rejected attempt (target died mid-windup etc.)
                // shouldn't burn the ult for the next 20 minutes.
                if (chosenIsUltimate && result == PowerUseResult.Success)
                    s_phantomNextUltimateMs[phantom.Id] = nowMs + PhantomUltimateCooldownMs;

                // Power-specific failures won't clear by retrying with a
                // different target � park the power so the picker falls
                // back to ones that actually work (see s_phantomPowerBlacklist).
                // NotAllowedByTransformMode confirmed live in server logs
                // (2026-07-15): Rogue's GlovesOff kept failing this way
                // against every target, every tick, forever � it requires a
                // transform state the phantom AI never enters, so retrying
                // never helps. Without blacklisting it, a high-cooldown-
                // weighted power like this keeps winning the weighted pick
                // and the phantom looks like it "randomly stops attacking"
                // even though it's retrying every tick and always failing.
                if (result == PowerUseResult.WeaponMissing || result == PowerUseResult.NotAllowedByTransformMode)
                    s_phantomPowerBlacklist[(phantom.Id, chosenPower)] = nowMs + PhantomPowerBlacklistMs;
                else if (result == PowerUseResult.RestrictiveCondition)
                    s_phantomPowerBlacklist[(phantom.Id, chosenPower)] = nowMs + PhantomTransientPowerBlacklistMs;

                // Log every failed activation so we can see WHY a cutscene boss
                // rejects the phantom's power (Dormant/Unaffectable/etc). Log
                // successes only once per (phantom, target) pair to avoid spam
                // � EXCEPT when the target is a real human player, where every
                // successful hit is logged regardless of repeats. The "once
                // per pair" suppression was hiding exactly the case under
                // investigation: a phantom's FIRST successful hit on a player
                // gets logged, but if it later lands a different (buff-type)
                // power on the same player, that second success was silently
                // dropped � making it impossible to confirm from logs alone
                // whether a buff power ever actually connects.
                bool targetIsRealPlayer = target is Avatar targetAv && targetAv.GetOwnerOfType<Player>()?.PlayerConnection != null;
                bool logThis = result != PowerUseResult.Success || targetIsRealPlayer;
                ulong key = phantom.Id ^ (target.Id * 0x9E3779B97F4A7C15UL);
                if (!logThis && s_phantomAttackTargetLogged.Add(key)) logThis = true;
                if (logThis)
                {
                    Power probePower = phantom.PowerCollection?.GetPower(chosenPower);
                    bool isValid = probePower != null && probePower.IsValidTarget(target);
                    string allianceRef = target.Alliance != null ? target.Alliance.DataRef.GetName() : "<null>";
                    var reach = probePower?.Prototype?.GetTargetingReach();
                    string reachStr = reach == null ? "<null>" : $"TargetsEnemy={reach.TargetsEnemy} TargetsFriendly={reach.TargetsFriendly}";
                    PhantomLogger.Info($"[PhantomHero:Attack] {phantom} ? target={target} power={chosenPower.GetName()} result={result} isValidTarget={isValid} tgtDormant={target.IsDormant} tgtUntargetable={target.IsUntargetable} tgtUnaffectable={target.IsUnaffectable} tgtAffectedByPowers={target.IsAffectedByPowers()} tgtSim={target.IsSimulated} tgtInWorld={target.IsInWorld} tgtAlliance={allianceRef} phantomAlliance={(phantom.Alliance?.DataRef.GetName() ?? "<null>")} reach=[{reachStr}]");
                }
                return result;
            }
            finally
            {
                ListPool<(PrototypeId, float, long)>.Return(candidates);
                if (aoeClusterPositions != null)
                    ListPool<Vector3>.Return(aoeClusterPositions);
            }
        }

        private class PhantomTickEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => (avatar) => avatar.OnPhantomTick();
        }

        // Cached list of resolvable pool entries. Filled lazily on first call
        // so we can log which paths fail once and pull them out of the rotation.
        private static readonly object s_phantomResolvedLock = new();
        private static List<PrototypeId> s_phantomResolved;
        private static List<PrototypeId> s_phantomTeamUpResolved;

        /// <summary>
        /// Deprecated/leftover test content lives in a "Testing" data folder
        /// and/or carries a "zzz" filename prefix by convention (e.g.
        /// "Entity/Characters/Avatars/Testing/zzzBrevikOLD.prototype" � a
        /// broken stand-in that resolves as a valid AvatarPrototype but has
        /// no real equipment setup, so phantoms rolled from it spawn with no
        /// gear). Same convention already used to filter the Wave Director's
        /// enemy catalog.
        /// </summary>
        private static bool IsDeprecatedTestContent(string prototypePath)
        {
            if (string.IsNullOrEmpty(prototypePath)) return false;
            if (prototypePath.IndexOf("/Testing/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            int slash = prototypePath.LastIndexOf('/');
            string fileName = slash >= 0 ? prototypePath[(slash + 1)..] : prototypePath;
            return fileName.StartsWith("zzz", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureResolvedPool()
        {
            lock (s_phantomResolvedLock)
            {
                if (s_phantomResolved != null) return;
                var resolved = new List<PrototypeId>(64);
                int excludedAvatars = 0;
                // Same iteration the login pipeline (PlayerConnection), the
                // equipment tables, and PowerCommands use to get "every real
                // playable hero for this client." Guarantees the pool tracks
                // the loaded client version exactly.
                foreach (PrototypeId avatarRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    if (avatarRef == PrototypeId.Invalid) continue;
                    if (avatarRef.As<AvatarPrototype>() == null) continue;
                    if (IsDeprecatedTestContent(avatarRef.GetName())) { excludedAvatars++; continue; }
                    resolved.Add(avatarRef);
                }
                s_phantomResolved = resolved;

                var teamUps = new List<PrototypeId>(24);
                int excludedTeamUps = 0;
                foreach (PrototypeId teamUpRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AgentTeamUpPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    if (teamUpRef == PrototypeId.Invalid) continue;
                    if (teamUpRef.As<AgentTeamUpPrototype>() == null) continue;
                    if (IsDeprecatedTestContent(teamUpRef.GetName())) { excludedTeamUps++; continue; }
                    teamUps.Add(teamUpRef);
                }
                s_phantomTeamUpResolved = teamUps;

                PhantomLogger.Info($"[PhantomHero] pool built from client data: {resolved.Count} playable avatars ({excludedAvatars} deprecated/test excluded), {teamUps.Count} team-ups ({excludedTeamUps} excluded)");
            }
        }

        /// <summary>
        /// The full team-up pool, resolved from loaded client data. Used
        /// by the OmegaDev2 catalog endpoint to list team-up phantoms
        /// alongside avatar phantoms.
        /// </summary>
        public static List<(PrototypeId TeamUpRef, string ShortName)> GetAllPhantomTeamUpRefs()
        {
            var results = new List<(PrototypeId, string)>();
            EnsureResolvedPool();
            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId teamUpRef in s_phantomTeamUpResolved)
                    results.Add((teamUpRef, ExtractPrototypeShortName(teamUpRef.GetName())));
            }
            results.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
            return results;
        }

        /// <summary>
        /// The full playable-avatar pool, resolved from the loaded client
        /// data. Used by the OmegaDev2 phantom tool's hero roster.
        /// </summary>
        public static List<(PrototypeId AvatarRef, string ShortName)> GetAllPhantomHeroRefs()
        {
            var results = new List<(PrototypeId, string)>();
            EnsureResolvedPool();
            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId avatarRef in s_phantomResolved)
                    results.Add((avatarRef, ExtractPrototypeShortName(avatarRef.GetName())));
            }
            results.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
            return results;
        }

        /// <summary>
        /// Match a user-typed hero name against the playable-avatar pool
        /// resolved from the loaded client data. Matching is entirely
        /// runtime � hero names come from the user's own data files and
        /// their chat input, never from this source tree. Exact short-name
        /// match (case-insensitive) wins immediately; otherwise all
        /// substring matches are returned so the caller can ask the user
        /// to be more specific.
        /// </summary>
        public static List<(PrototypeId AvatarRef, string ShortName)> FindPhantomHeroRefs(string query)
        {
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;
            EnsureResolvedPool();

            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId avatarRef in s_phantomResolved)
                {
                    string shortName = ExtractPrototypeShortName(avatarRef.GetName());
                    if (shortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Clear();
                        results.Add((avatarRef, shortName));
                        return results;
                    }
                    if (shortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add((avatarRef, shortName));
                }
            }

            return results;
        }

        /// <summary>
        /// All costumes usable by the given avatar, resolved from the loaded
        /// client data (CostumePrototype.UsableBy). Like the hero pool, this
        /// is entirely runtime � no costume names live in server source.
        /// Deliberately NOT using ApprovedOnly: some real, fully-populated
        /// costumes (e.g. the Age of Apocalypse Horsemen on 1.53) are still
        /// flagged DesignState=DevelopmentOnly in the retail data despite
        /// being complete and playable, and that flag would silently hide
        /// them from selection here.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> GetCostumesForAvatar(PrototypeId avatarRef)
        {
            var results = new List<(PrototypeId, string)>();
            if (avatarRef == PrototypeId.Invalid) return results;

            foreach (PrototypeId costumeRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<CostumePrototype>(PrototypeIterateFlags.NoAbstract))
            {
                CostumePrototype costumeProto = costumeRef.As<CostumePrototype>();
                if (costumeProto == null) continue;
                if (costumeProto.UsableBy != avatarRef) continue;
                if (costumeProto.CostumeUnrealClass == AssetId.Invalid) continue;
                results.Add((costumeRef, ExtractPrototypeShortName(costumeRef.GetName())));
            }

            return results;
        }

        /// <summary>
        /// Match a user-typed costume name against the avatar's costume
        /// pool. Exact short-name match wins; otherwise all substring
        /// matches are returned.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> FindCostumeRefs(PrototypeId avatarRef, string query)
        {
            var all = GetCostumesForAvatar(avatarRef);
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            foreach (var entry in all)
            {
                if (entry.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Clear();
                    results.Add(entry);
                    return results;
                }
                if (entry.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }

            return results;
        }

        /// <summary>Pick a random costume for the avatar, or Invalid if it has none.</summary>
        public static PrototypeId PickRandomCostume(PrototypeId avatarRef, MHServerEmu.Core.System.Random.GRandom rng)
        {
            var pool = GetCostumesForAvatar(avatarRef);
            if (pool.Count == 0) return PrototypeId.Invalid;
            return pool[rng.Next(0, pool.Count)].CostumeRef;
        }

        /// <summary>
        /// "Entity/Characters/Avatars/Shipping/SomeHero.prototype" ? "SomeHero".
        /// </summary>
        private static string ExtractPrototypeShortName(string prototypePath)
        {
            if (string.IsNullOrEmpty(prototypePath)) return string.Empty;
            int slash = prototypePath.LastIndexOf('/');
            string fileName = slash >= 0 ? prototypePath[(slash + 1)..] : prototypePath;
            const string suffix = ".prototype";
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^suffix.Length];
            return fileName;
        }

        /// <summary>
        /// Every real playable avatar ref (same pool NextPhantomHeroRef draws
        /// from) � a snapshot list, safe for a caller to shuffle/consume.
        /// Used by Player.TrialOfImpossible.cs to build a "fight every hero"
        /// roster.
        /// </summary>
        public static IReadOnlyList<PrototypeId> GetAllPlayableHeroRefs()
        {
            EnsureResolvedPool();
            return s_phantomResolved;
        }

        private PrototypeId NextPhantomHeroRef()
        {
            EnsureResolvedPool();
            lock (s_phantomDeckLock)
            {
                if (s_phantomResolved.Count == 0) return PrototypeId.Invalid;
                if (s_phantomDeckIdx >= s_phantomDeck.Count)
                {
                    s_phantomDeck.Clear();
                    for (int i = 0; i < s_phantomResolved.Count; i++) s_phantomDeck.Add(i);
                    for (int i = s_phantomDeck.Count - 1; i > 0; i--)
                    {
                        int j = Game.Random.Next(0, i + 1);
                        (s_phantomDeck[i], s_phantomDeck[j]) = (s_phantomDeck[j], s_phantomDeck[i]);
                    }
                    s_phantomDeckIdx = 0;
                }
                int idx = s_phantomDeck[s_phantomDeckIdx++];
                return s_phantomResolved[idx];
            }
        }

        /// <summary>
        /// Spawns a phantom hero (real AvatarPrototype) at a random offset from
        /// this avatar. Returns the Avatar entity id, or 0 with a reason in
        /// <paramref name="error"/>.
        /// </summary>
        public ulong SpawnPhantomHero(int levelOverride, string username, out string error, bool bypassCap = false)
            // A non-zero levelOverride from the chat command means the user
            // explicitly asked for a specific level (e.g. `!phantom spawn 4 45`).
            // Lock that level in � the tick loop will not auto-level these
            // phantoms as the caller gains XP. Costume 0 = roll random,
            // gear null = roll random per slot.
            => SpawnPhantomHeroCore(PrototypeId.Invalid, levelOverride, username, levelOverride > 0, 0, null, out error, bypassCap: bypassCap);

        /// <summary>
        /// Respawns a phantom from a MigrationData intent � same avatarRef +
        /// level + username + LockLevel + costume as the pre-transfer state.
        /// Used by Player.RestorePhantomsFromMigration after cross-region
        /// travel and by saved-squad spawns.
        /// </summary>
        public ulong SpawnPhantomHeroFromIntent(PrototypeId avatarRefOverride, int level, string username, bool lockLevel, ulong costumeRef, out string error, List<ulong> gearRefs = null, bool invincible = false, bool bypassCap = false)
        {
            // Team-up intents (stored with an AgentTeamUpPrototype ref in
            // AvatarRef) must go through the team-up spawn path, not the
            // avatar one � SpawnPhantomHeroCore's avatarRef.As<AvatarPrototype>()
            // returns null for a team-up ref and the phantom is silently lost
            // on region change.
            if (avatarRefOverride != PrototypeId.Invalid && avatarRefOverride.As<AgentTeamUpPrototype>() != null)
                return SpawnTeamUpPhantomHero(avatarRefOverride, level, out error, enemy: false, nemesisRank: 0, usernameOverride: username, gearOverride: gearRefs, bypassCap: bypassCap);

            // Same reasoning as the team-up branch above, for boss-phantom
            // intents (stored with a real curated-boss AgentPrototype ref).
            if (IsBossPhantomRef(avatarRefOverride))
                return SpawnBossPhantomHero(avatarRefOverride, level, out error, usernameOverride: username, bypassCap: bypassCap);

            return SpawnPhantomHeroCore(avatarRefOverride, level, username, lockLevel, costumeRef, gearRefs, out error, enemy: false, invincible: invincible, bypassCap: bypassCap);
        }

        /// <summary>
        /// Spawns a HOSTILE phantom hero � full avatar kit (powers, gear,
        /// costume) but alliance-flipped so it hunts the caller and their
        /// squad. Killable, never party-listed, never migrated, and kill
        /// credit is never remapped to the human who spawned it.
        /// </summary>
        public ulong SpawnEnemyPhantomHero(PrototypeId avatarRefOverride, int level, out string error, bool ambush = false)
        {
            // A caller-supplied team-up ref (Rogue Encounter, Enemy Phantoms
            // tool in the app, nemesis roster respawn) has to go through the
            // team-up spawn path � the avatar path chokes on non-Avatar refs
            // the same way SpawnPhantomHeroFromIntent did before the fix.
            if (avatarRefOverride != PrototypeId.Invalid && avatarRefOverride.As<AgentTeamUpPrototype>() != null)
                return SpawnTeamUpPhantomHero(avatarRefOverride, level, out error, enemy: true);

            return SpawnPhantomHeroCore(avatarRefOverride, level, null, lockLevel: true, 0, null, out error, enemy: true, ambush: ambush);
        }

        /// <summary>
        /// Spawn a team-up as a phantom. Team-ups have their own AI, powers
        /// and animations, so they engage targets without needing our AI
        /// tick to drive power selection. We still register them in the
        /// phantom tracking dicts so `!phantom clear`, cross-region purge,
        /// leash and stuck detection all still apply. Friendly team-ups
        /// use the caller's alliance and follow the caller; enemy team-ups
        /// use the hostile alliance override.
        /// </summary>
        public ulong SpawnTeamUpPhantomHero(PrototypeId teamUpRef, int level, out string error, bool enemy = false, int nemesisRank = 0, string usernameOverride = null, List<ulong> gearOverride = null, int nemesisEscapeCount = 0, bool bypassCap = false, int nemesisGrudgeScore = 0)
        {
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }
            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            if (enemy && ResolveHostileAllianceRef() == PrototypeId.Invalid)
            {
                error = "no hostile alliance in loaded data";
                return 0;
            }

            AgentTeamUpPrototype teamUpProto = teamUpRef.As<AgentTeamUpPrototype>();
            if (teamUpProto == null) { error = "not an AgentTeamUpPrototype"; return 0; }

            Player host = PhantomHost;
            if (host == null) { error = "no Player host to register phantom against"; return 0; }

            if (enemy == false && bypassCap == false)
            {
                // Confirmed live 2026-07-26 � combining via Math.Min() was
                // wrong: the Danger Room Endless arena's own 4-player-total
                // co-op cap (Player.GetEndlessPhantomSlotCap, meant to allow
                // up to 3 for a solo player) was getting silently clamped
                // down by the UNRELATED generic party-size cap
                // (GetPhantomPartyCap, driven by PlayerPartyMaxSize data
                // this custom arena has nothing to do with) � a player
                // brought 3 phantoms in and only 2 came through. The Endless
                // arena's own rule should be fully authoritative inside that
                // specific region, not further restricted by whatever normal
                // party-size limit happens to apply there; it already
                // returns int.MaxValue (no-op) everywhere else in the game.
                int endlessCap = Player.GetEndlessPhantomSlotCap(region, host);
                bool inEndlessArena = endlessCap != int.MaxValue;

                // The squad gate no-ops outside an active Bounty Board hunt,
                // so Endless Challenge (and everything else) is unaffected.
                string squadGate = CheckPhantomSquadGate(host);
                if (squadGate != null) { error = squadGate; return 0; }

                int cap = inEndlessArena ? endlessCap : GetPhantomPartyCap(region);
                // Confirmed live 2026-07-26 � this was ">= cap", an off-by-
                // one that rejected the LAST legitimate slot instead of only
                // rejecting once actually over cap: with cap=3 and 2 already
                // in, "1+2 >= 3" blocked the 3rd phantom even though 3 total
                // is exactly the intended limit, not past it. A cap of N
                // should allow N, not N-1.
                if (1 + host.PhantomHeroCount > cap)
                {
                    error = $"squad full ({host.PhantomHeroCount + 1}/{cap}) � this region's party/raid cap won't allow another phantom";
                    return 0;
                }
            }

            // Step 1: phantom Player as owner (same pattern as avatar spawn).
            ulong phantomDbId = System.Threading.Interlocked.Increment(ref s_phantomDbIdSeed);
            string username = string.IsNullOrEmpty(usernameOverride) ? NewPhantomUsername(Game.Random) : usernameOverride;
            Player phantomPlayer;
            using (var playerSettingsHandle = EntitySettingsPool.Get(out EntitySettings playerSettings))
            {
                playerSettings.DbGuid = phantomDbId;
                playerSettings.EntityRef = GameDatabase.GlobalsPrototype.DefaultPlayer;
                playerSettings.OptionFlags = EntitySettingsOptionFlags.PopulateInventories;
                playerSettings.PlayerConnection = null;
                playerSettings.PlayerName = username;
                playerSettings.ArchiveSerializeType = ArchiveSerializeType.Database;
                playerSettings.ArchiveData = null;
                phantomPlayer = Game.EntityManager.CreateEntity(playerSettings) as Player;
            }
            if (phantomPlayer == null) { error = "phantom Player entity create failed"; return 0; }
            if (enemy == false) phantomPlayer.PhantomCreatorId = host.Id;

            // Step 2: create the team-up Agent in the phantom's TeamUpLibrary
            // (same slot Player.UnlockTeamUpAgent uses for real players)�
            Inventory teamUpLibrary = phantomPlayer.GetInventory(InventoryConvenienceLabel.TeamUpLibrary);
            if (teamUpLibrary == null) { error = "TeamUpLibrary missing on phantom Player"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            Agent teamUp;
            using (var settingsHandle = EntitySettingsPool.Get(out EntitySettings settings))
            {
                settings.InventoryLocation = new(phantomPlayer.Id, teamUpLibrary.PrototypeDataRef);
                settings.EntityRef = teamUpRef;
                teamUp = Game.EntityManager.CreateEntity(settings) as Agent;
            }
            if (teamUp == null) { error = $"team-up create failed for {teamUpRef.GetName()}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Mark as phantom BEFORE the AvatarInPlay move: the Agent
            // override for CanChangeInventoryLocation keys off IsPhantomHero
            // to skip the containment filter that would otherwise reject a
            // team-up going into an Avatar-only inventory slot.
            teamUp.IsPhantomHero = true;

            // �then relocate into AvatarInPlay slot 0. The party HUD's HP
            // binding walks the phantom Player's AvatarInPlay slot to find
            // the "current avatar" entity. The high-level ChangeInventory-
            // Location path runs the InventoryPrototype's containment
            // filter, which rejects non-Avatar entities (the client's SIP
            // data restricts AvatarInPlay to AvatarPrototype).
            // Inventory.ChangeEntityInventoryLocation is the same low-level
            // mover the filter path eventually calls � it does the move
            // without the filter check, so we can place the team-up there
            // server-side and let the client's HP-bar binding follow
            // AvatarInPlay[0] to the team-up entity.
            Inventory avatarInPlay = phantomPlayer.GetInventory(InventoryConvenienceLabel.AvatarInPlay);
            if (avatarInPlay != null)
            {
                ulong? stackEntityId = null;
                var moveResult = Inventory.ChangeEntityInventoryLocation(teamUp, avatarInPlay, 0, ref stackEntityId, false);
                if (moveResult != InventoryResult.Success)
                    PhantomLogger.Warn($"[PhantomHero:TeamUp] AvatarInPlay move failed ({moveResult}) � HP bar may not bind on client");
            }

            int effectiveLevel = level > 0 ? level : CharacterLevel;
            teamUp.InitializeLevel(effectiveLevel);
            teamUp.CombatLevel = effectiveLevel;
            teamUp.Properties[PropertyEnum.PowerProgressionVersion] = teamUp.GetLatestPowerProgressionVersion();
            teamUp.Properties[PropertyEnum.Health] = teamUp.Properties[PropertyEnum.HealthMax];

            // Enter game so AOI broadcasts (mirrors SpawnPhantomHeroCore step 5).
            try { phantomPlayer.EnterGame(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] phantomPlayer.EnterGame() partial: {ex.Message}"); }
            try { phantomPlayer.OnLoadingScreenFinished(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] OnLoadingScreenFinished failed: {ex.Message}"); }

            // Step 3: SetAsPersistent(this, true) does the world entry �
            // it computes a position near this avatar and calls EnterWorld
            // internally. Then AssignTeamUpAgentPowers grants the team-up's
            // native power set. See Avatar.SpawnTeamUpAgent for the full
            // real-player flow we're mirroring.
            try
            {
                teamUp.SetAsPersistent(this, true);
                teamUp.AssignTeamUpAgentPowers();
                // Friendly team-ups also learn the caller's ResurrectOther
                // power so they can revive downed party members. Enemy team-
                // ups deliberately skip this � no reviving the player they
                // just took down.
                if (enemy == false)
                {
                    PrototypeId resurrectPowerRef = AvatarPrototype?.ResurrectOtherEntityPower ?? PrototypeId.Invalid;
                    if (resurrectPowerRef != PrototypeId.Invalid)
                    {
                        try { teamUp.AssignPower(resurrectPowerRef, new PowerIndexProperties(0, teamUp.CharacterLevel, teamUp.CombatLevel)); }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] AssignPower(ResurrectOther) failed: {ex.Message}"); }
                    }
                }
                AlliancePrototype alliancePlaceholder = Alliance;
                if (enemy)
                {
                    PrototypeId hostileRef = ResolveHostileAllianceRef();
                    if (hostileRef != PrototypeId.Invalid)
                        alliancePlaceholder = hostileRef.As<AlliancePrototype>();
                }
                teamUp.SetSummonedAllianceOverride(alliancePlaceholder);

                // Same reliability fix as avatar-type ambush phantoms above
                // and standalone curated bosses (EntityHelper.
                // ApplyStandaloneBossFixups) � a hostile team-up spawned away
                // from the player it's meant to ambush can sit inert until
                // hit once and then go idle again. Widen its aggro range
                // instead of hard-pinning one fixed target id: that still
                // lets normal sensing (Combat.GetValidTargetsInSphere) pick
                // ANY valid hostile in range, so it can engage the player's
                // OTHER friendly phantom heroes/team-ups too, not just this
                // one real player forever.
                if (enemy)
                {
                    teamUp.Properties[PropertyEnum.Dormant] = false;
                    if (teamUp.AIController != null)
                    {
                        var teamUpBlackboard = teamUp.AIController.Blackboard.PropertyCollection;
                        teamUpBlackboard[PropertyEnum.AIAggroRangeOverrideHostile] = PhantomStandaloneAggroRange;
                        teamUpBlackboard[PropertyEnum.AIAggroRangeOverrideAlly] = PhantomStandaloneAggroRange;
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"team-up world entry failed: {ex.Message}";
                try { if (teamUp.IsInWorld) teamUp.ExitWorld(); teamUp.Destroy(); }
                catch (Exception cleanupEx) { PhantomLogger.Warn($"[PhantomHero:TeamUp] cleanup after failed entry threw: {cleanupEx.Message}"); }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            if (teamUp.IsInWorld == false)
            {
                error = "team-up SetAsPersistent did not enter world";
                try { teamUp.Destroy(); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] Destroy() after failed world entry threw: {ex.Message}"); }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            // HP + damage curve: same enemy/friendly split as avatar phantoms
            // so team-up phantoms feel calibrated the same way. Nemesis rank
            // stacks on the enemy base.
            if (enemy)
            {
                float enemyHpBase = EnemyPhantomHealthMult;
                float hpMult = nemesisRank > 0
                    ? Player.NemesisHealthMultForRank(nemesisRank) * (1f + Player.NemesisEscapeHealthBonusPerEscape * nemesisEscapeCount)
                        * (1f + Player.NemesisGrudgeHealthBonusPerPoint * Math.Max(0, nemesisGrudgeScore))
                    : enemyHpBase;
                // Same low-level tankiness fix as avatar-type ambush
                // phantoms � see ScaleHealthMultForLevel.
                teamUp.Properties[PropertyEnum.HealthMaxMult] = ScaleHealthMultForLevel(hpMult, effectiveLevel);
                try
                {
                    var globals = GameDatabase.PopulationGlobalsPrototype;
                    if (globals != null)
                    {
                        var rankProto = nemesisRank > 0
                            ? globals.GetRankByEnum(Rank.Boss)
                            : globals.GetRankByEnum(Rank.MiniBoss);
                        if (rankProto != null)
                            teamUp.Properties[PropertyEnum.Rank] = rankProto.DataRef;
                    }
                }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] rank assignment failed: {ex.Message}"); }
            }
            else
            {
                teamUp.Properties[PropertyEnum.HealthMaxMult] = ScaleHealthMultForLevel(PhantomHealthMult, effectiveLevel);
            }
            teamUp.Properties[PropertyEnum.Health] = teamUp.Properties[PropertyEnum.HealthMax];
            ApplyPhantomDamageScaling(teamUp, effectiveLevel, enemy);
            if (enemy && nemesisRank > 0)
            {
                float dmgBoost = Player.NemesisDmgBoostForRank(nemesisRank) + Player.NemesisGrudgeDmgBoostPerPoint * Math.Max(0, nemesisGrudgeScore);
                float currentDmgMult = teamUp.Properties[PropertyEnum.DamageMult];
                teamUp.Properties[PropertyEnum.DamageMult] = (currentDmgMult <= 0f ? 1f : currentDmgMult) * (1f + dmgBoost);
            }

            // Team-ups carry 4 equipment slots in their prototype data (same
            // AvatarEquipInventoryAssignmentPrototype shape avatars use) but
            // previously never got anything rolled into them. Equip here the
            // same way avatar phantoms do � random roll, or the exact stored
            // refs on squad/migration restore.
            List<ulong> appliedGearRefs = null;
            try { appliedGearRefs = ApplyPhantomGear(phantomPlayer, teamUp, effectiveLevel, gearOverride); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] ApplyPhantomGear failed: {ex.Message}"); }

            // IsPhantomHero was set earlier (before the AvatarInPlay move
            // so the containment-filter override could see it). Just make
            // sure we're simulated so power activation doesn't
            // OwnerNotSimulated-reject.
            try { teamUp.SetSimulated(true); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] SetSimulated(true) failed: {ex.Message}"); }

            // Cap the final DamageMult AFTER SetSimulated, not before � see
            // ClampEnemyPhantomDamageMult's header for why. Confirmed live
            // (2026-07-21): setting Properties[PropertyEnum.Rank] earlier in
            // this function isn't purely cosmetic the way the original
            // comment assumed � WorldEntity's Rank property-change handler
            // defers applying the Rank prototype's own baked-in Mod bundle
            // (ModChangeModEffects) until SetSimulated fires, attaching real
            // base-game damage/health bonuses as another child collection.
            // A clamp placed before SetSimulated gets silently overridden by
            // this � confirmed by rank-5 phantoms landing on a suspiciously
            // uniform dmgMult=10.00 in live combat despite the 5.0 cap.
            if (enemy) ClampEnemyPhantomDamageMult(teamUp);
            if (enemy) LogEnemyPhantomFinalStats(teamUp, nemesisRank);

            // Register into the same tracking dicts avatar phantoms use so
            // !phantom clear, cross-region purge, party HUD sync, leash, and
            // nemesis retirement all reuse the existing plumbing.
            if (enemy)
            {
                host.RegisterEnemyPhantom(teamUp.Id, phantomPlayer.Id);
                if (nemesisRank > 0)
                    s_enemyPhantomRankLevel[teamUp.Id] = (nemesisRank, effectiveLevel);
            }
            else
            {
                var descriptor = new MHServerEmu.DatabaseAccess.Models.PhantomIntent
                {
                    AvatarRef = (ulong)teamUpRef,
                    Level = effectiveLevel,
                    Username = username,
                    LockLevel = false,
                    CostumeRef = 0,
                    GearRefs = appliedGearRefs,
                    Invincible = false,
                    BypassCap = bypassCap,
                };
                host.RegisterPhantom(teamUp.Id, phantomPlayer.Id, descriptor);
            }
            SchedulePhantomTick();

            // Take over combat decision-making from the native AIController
            // (target selection, power picking, follow) so team-up phantoms
            // get the same threat-scoring/damage-scoring/AoE/kiting/hazard-
            // avoidance logic avatar phantoms use, instead of the engine's
            // generic behavior tree.
            //
            // A one-time disable here is sufficient and safe � verified,
            // not assumed:
            //   - Agent.Resurrect() only calls AIController.OnAIResurrect()
            //     (which force re-enables) when CanBePlayerOwned() is FALSE.
            //     AgentTeamUpPrototype always makes CanBePlayerOwned() return
            //     true (Entity.CanBePlayerOwned), so a revived team-up
            //     phantom never gets its native brain silently switched back
            //     on.
            //   - The other re-enable path, Agent.ActivateAI() (gated on the
            //     AIStartsEnabled property), only runs once, from
            //     OnEnteredWorld � which has already happened by this point
            //     in spawn.
            try { teamUp.AIController?.SetIsEnabled(false); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:TeamUp] AIController disable failed: {ex.Message}"); }

            // Same missing client push as boss phantoms — see
            // PushPhantomToClients' header. Team-ups are the same shape
            // (non-Avatar Agent under a synthetic Player) and never received
            // their PowerCollection on the client either.
            PushPhantomToClients(teamUp, phantomPlayer);

            PhantomLogger.Info($"[PhantomHero:TeamUp] {this} spawned {(enemy ? "HOSTILE" : "friendly")} team-up '{teamUpRef.GetName()}' (agentId 0x{teamUp.Id:X}) at {teamUp.RegionLocation.Position.ToStringNames()} level {effectiveLevel}");
            return teamUp.Id;
        }

        /// <summary>
        /// True when <paramref name="bossRef"/> is a real curated boss-tier
        /// AgentPrototype (Sabretooth, Rhino, etc. — the same "/Bosses/"
        /// pool the Boss Roster tool spawns as hostile enemies from), NOT a
        /// mob, champion, or mini-boss. Used to dispatch a phantom spawn
        /// request to SpawnBossPhantomHero instead of the avatar/team-up
        /// paths, the same way an AgentTeamUpPrototype ref is detected and
        /// routed to SpawnTeamUpPhantomHero.
        /// </summary>
        private static bool IsBossPhantomRef(PrototypeId bossRef)
            => bossRef != PrototypeId.Invalid && Player.GetRawBossCandidatePool().Contains(bossRef);

        /// <summary>
        /// Spawn a real boss-tier AgentPrototype (Sabretooth, Rhino, etc. —
        /// see IsBossPhantomRef) as a FRIENDLY phantom teammate, fighting
        /// alongside the caller the same way avatar-type and team-up phantom
        /// heroes do. Friendly only — this is explicitly a squad member, not
        /// another hostile-enemy spawn path (that already exists via
        /// SpawnCuratedBoss/BossRosterWebHandler for the Boss Roster tool).
        ///
        /// Modeled directly on SpawnTeamUpPhantomHero, which already proved
        /// a non-Avatar Agent can be hosted by a synthetic phantom Player,
        /// bound to the party HUD via the AvatarInPlay inventory slot, and
        /// driven entirely by this AI (UpdatePhantomHunt) instead of the
        /// engine's own behavior tree — per user request 2026-08-08, a boss
        /// phantom must use "the same AI as my other [phantom heroes]", not
        /// its native boss brain, and the same HP/damage curve regular
        /// phantom heroes use, not native boss-tier stats.
        ///
        /// Real bosses don't have a TeamUpLibrary-shaped inventory of their
        /// own to create into first the way a team-up does — TeamUpLibrary
        /// is reused here purely as a container that is already proven to
        /// accept a non-Avatar Agent entity at creation time; the boss
        /// entity is moved out to AvatarInPlay immediately after and never
        /// visibly occupies a team-up slot.
        /// </summary>
        /// <summary>
        /// Push a freshly spawned non-avatar phantom (boss / team-up) to every
        /// real client, exactly the way SpawnPhantomHeroCore already does for
        /// avatar phantoms.
        ///
        /// Established by log comparison 2026-08-09: an avatar phantom spawn
        /// emits "[PhantomHero:PowerSync] ... collection (97 powers) sent=True"
        /// and "[PhantomHero:AOI] ...", while a boss phantom spawn emits
        /// NEITHER. Those lines come from a block that lives only inside the
        /// avatar path (Avatar.PhantomHero.cs ~7236-7294); SpawnBossPhantomHero
        /// and SpawnTeamUpPhantomHero never had an equivalent.
        ///
        /// Its own header explains why that matters, and describes the exact
        /// reported symptoms: "Without this the phantom exists server-side but
        /// no NetMessage tells clients about it -- invisible bot", and
        /// PowerCollection.AssignPower only ships
        /// NetMessagePowerCollectionAssignPower while _owner.IsInGame is true,
        /// which is false during the phantom's power assignment — so "the
        /// client's PowerCollection for this phantom stays empty ... the cast
        /// animation never plays". A client-side entity carrying an empty power
        /// collection has no animation data to drive it, which is what a T-pose
        /// is.
        ///
        /// Deliberately NOT gated on IsBossPhantom: team-up phantoms are the
        /// same shape (non-Avatar Agent hosted by a synthetic Player) and were
        /// missing this for the same reason.
        /// </summary>
        private void PushPhantomToClients(Agent phantom, Player phantomPlayer)
        {
            if (phantom == null || phantomPlayer == null) return;

            try
            {
                // Phantom Player first so the client can resolve the owner
                // before it receives the agent itself.
                phantomPlayer.UpdateInterestPolicies(true, null);
                phantom.UpdateInterestPolicies(true, null);

                if (phantom.PowerCollection == null)
                {
                    PhantomLogger.Warn($"[PhantomHero:PowerSync] phantom 0x{phantom.Id:X} has no PowerCollection at spawn — client has no animation data");
                    return;
                }

                int collectionSize = 0;
                foreach (var _ in phantom.PowerCollection) collectionSize++;

                foreach (Player realPlayer in new PlayerIterator(Game))
                {
                    if (realPlayer.PlayerConnection == null) continue;
                    var aoi = realPlayer.AOI;
                    if (aoi == null) continue;
                    if (aoi.InterestedInEntity(phantom.Id, AOINetworkPolicyValues.AOIChannelProximity) == false)
                    {
                        PhantomLogger.Info($"[PhantomHero:PowerSync] SKIP {realPlayer.GetName()} — not interested in phantom 0x{phantom.Id:X} (proximity=false), collectionSize={collectionSize}");
                        continue;
                    }

                    bool sent = phantom.PowerCollection.SendEntireCollection(realPlayer);
                    PhantomLogger.Info($"[PhantomHero:PowerSync] {realPlayer.GetName()} ← phantom 0x{phantom.Id:X} collection ({collectionSize} powers) sent={sent}");
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:PowerSync] push failed for 0x{phantom.Id:X}: {ex.Message}"); }
        }

        public ulong SpawnBossPhantomHero(PrototypeId bossRef, int level, out string error, string usernameOverride = null, bool bypassCap = false)
        {
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }
            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            if (IsBossPhantomRef(bossRef) == false) { error = "not a real curated boss (mobs/champions/mini-bosses are not eligible)"; return 0; }
            AgentPrototype bossProto = bossRef.As<AgentPrototype>();
            if (bossProto == null) { error = "bossRef did not resolve to an AgentPrototype"; return 0; }

            Player host = PhantomHost;
            if (host == null) { error = "no Player host to register phantom against"; return 0; }

            if (bypassCap == false)
            {
                string squadGate = CheckPhantomSquadGate(host);
                if (squadGate != null) { error = squadGate; return 0; }

                int cap = GetPhantomPartyCap(region);
                if (1 + host.PhantomHeroCount > cap)
                {
                    error = $"squad full ({host.PhantomHeroCount + 1}/{cap}) — this region's party/raid cap won't allow another phantom";
                    return 0;
                }
            }

            // Step 1: phantom Player as owner (same pattern as avatar/team-up spawn).
            ulong phantomDbId = System.Threading.Interlocked.Increment(ref s_phantomDbIdSeed);
            string username = string.IsNullOrEmpty(usernameOverride) ? NewPhantomUsername(Game.Random) : usernameOverride;
            Player phantomPlayer;
            using (var playerSettings = ObjectPoolManager.Instance.Get<EntitySettings>())
            {
                playerSettings.DbGuid = phantomDbId;
                playerSettings.EntityRef = GameDatabase.GlobalsPrototype.DefaultPlayer;
                playerSettings.OptionFlags = EntitySettingsOptionFlags.PopulateInventories;
                playerSettings.PlayerConnection = null;
                playerSettings.PlayerName = username;
                playerSettings.ArchiveSerializeType = ArchiveSerializeType.Database;
                playerSettings.ArchiveData = null;
                phantomPlayer = Game.EntityManager.CreateEntity(playerSettings) as Player;
            }
            if (phantomPlayer == null) { error = "phantom Player entity create failed"; return 0; }
            phantomPlayer.PhantomCreatorId = host.Id;

            // Step 2: create the boss Agent — see this method's header for why
            // TeamUpLibrary is reused purely as a creation-time container.
            Inventory teamUpLibrary = phantomPlayer.GetInventory(InventoryConvenienceLabel.TeamUpLibrary);
            if (teamUpLibrary == null) { error = "TeamUpLibrary missing on phantom Player"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            Agent boss;
            using (var settings = ObjectPoolManager.Instance.Get<EntitySettings>())
            {
                settings.InventoryLocation = new(phantomPlayer.Id, teamUpLibrary.PrototypeDataRef);
                settings.EntityRef = bossRef;
                boss = Game.EntityManager.CreateEntity(settings) as Agent;
            }
            if (boss == null) { error = $"boss create failed for {bossRef.GetName()}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Owning this boss to the CALLER (not just the phantom Player) via
            // PowerUserOverrideID makes Entity.CanBePlayerOwned() return true
            // (Entity.cs: owner is Avatar -> true). Without this, a plain
            // AgentPrototype's CanBePlayerOwned() is false, which means
            // Agent.Resurrect() force-re-enables the native AIController on
            // any revive (Entity.cs's CanBePlayerOwned==false branch) — that
            // would silently hand combat decisions back to the boss's own
            // native brain the first time it's revived, breaking the "same
            // AI as my other phantoms" requirement this exists to satisfy.
            boss.Properties[PropertyEnum.PowerUserOverrideID] = Id;

            boss.IsPhantomHero = true;

            // Strip the prototype's baked-in boss Rank and bind the caller's
            // alliance BEFORE the entity enters the world below.
            //
            // SetAsPersistent is what puts the boss in-world, which is also
            // when it first replicates to the real client. Everything that
            // made it "not a boss" used to run AFTER that point, so the client
            // received the entity in its native state — Rank=Mods/Ranks/Boss —
            // and only got corrections afterwards. Confirmed live 2026-08-09
            // via [BossDiag]: on every spawn the boss entered world carrying
            // Rank=Boss and was only stripped ~8ms later, and (via
            // ApplyStandaloneBossFixups) briefly flipped to the ENEMY alliance
            // in that same window. Whether the client latched boss treatment
            // depended purely on message timing, which matches the reported
            // non-determinism (sometimes the 1st boss, sometimes the 2nd).
            //
            // The late strip further down is deliberately kept as well: it
            // still catches anything that re-derives Rank between here and
            // SetSimulated. This one closes the replication window; that one
            // closes the re-derivation window. They fix different things.
            boss.Properties.RemoveProperty(PropertyEnum.Rank);
            boss.SetSummonedAllianceOverride(Alliance);

            // Suppress the boss's native wake / dramatic-entrance sequence.
            // THIS IS THE FIX FOR THE GLITCHY, STUTTERING SPAWN-IN — root
            // cause traced 2026-08-09 to Agent.OnPropertyChange's Dormant
            // handler (Agent.cs:2496):
            //
            //     if (dormant == false) СheckWakeDelay();
            //     if (!IsVisibleWhenDormant) Properties[Visible] = !dormant;
            //
            // Two separate problems, both hitting every boss phantom:
            //
            //  1. VISIBILITY IS SLAVED TO DORMANCY. For any boss whose
            //     prototype has WakeStartsVisible=false, each dormancy flip
            //     toggles the entity invisible/visible. The "glitchy" look is
            //     literal flickering, not a movement fault — which is why
            //     chasing the locomotor never fixed it, and why avatar
            //     phantoms (never dormant) looked fine in the matched
            //     [BossDiag:Move] traces.
            //
            //  2. CLEARING DORMANT REPLAYS THE DRAMATIC ENTRANCE.
            //     СheckWakeDelay (Agent.cs:3481) schedules the entrance
            //     whenever WakeDelayMS > 0, PlayDramaticEntrance != Never and
            //     DramaticEntrancePlayedOnce == false. We never set that flag,
            //     so every clear — including the 2s
            //     StandaloneBossDormantWatchdog's — re-armed the boss's
            //     cinematic entrance (physics resolve + entrance animation)
            //     while the phantom tick was already driving it around.
            //
            // The engine's own player-controlled-agent path already does this
            // correctly: Agent.SetControlledProperties sets
            // DramaticEntrancePlayedOnce BEFORE clearing Dormant, carrying the
            // explicit comment "IMPORTANT: Dormant needs to be turned off
            // after setting DramaticEntrancePlayedOnce." Mirror that ordering
            // exactly. With the flag set first, СheckWakeDelay takes its else
            // branch (TryAutoActivatePowersInCollection) and no entrance is
            // ever scheduled.
            //
            // Done here, before SetAsPersistent puts the boss in-world and
            // replicates it, so the client never observes the dormant/
            // invisible/mid-entrance state at all.
            // DO NOT set DramaticEntrancePlayedOnce here.
            //
            // Reverted 2026-08-09 — setting it is what CAUSED the T-pose, and
            // the timeline proves it: before that change the report was
            // "glitchy/stutter" and never a T-pose; the very next test after
            // adding it reported T-posing bosses.
            //
            // Mechanism: PlayDramaticEntrance is baked into the prototype
            // (BullseyeCH4 = Always, confirmed from live data), so the CLIENT
            // plays the entrance regardless of server state. Setting this flag
            // makes СheckWakeDelay (Agent.cs:3481) skip scheduling the wake
            // sequence, so WakeEndCallback -> OnDramaticEntranceEnd()
            // (Agent.cs:3507) never fires and the client is never told the
            // entrance finished — leaving the boss frozen mid-entrance, i.e.
            // T-posed.
            //
            // The known-good comparison is EntityHelper.ApplyStandaloneBossFixups,
            // used by the Boss Roster tool whose bosses animate correctly: it
            // clears Dormant and does NOT touch DramaticEntrancePlayedOnce.
            // Match that exactly and let the entrance run to completion.
            boss.Properties[PropertyEnum.Dormant] = false;

            // Log the prototype's own wake/entrance settings so the next trace
            // shows exactly which of these applied to each boss, rather than
            // leaving it inferred.
            PhantomLogger.Info($"[BossDiag:Proto] {bossRef.GetName()} WakeRange={bossProto.WakeRange} " +
                               $"WakeDelayMS={bossProto.WakeDelayMS} WakeRandomStartMS={bossProto.WakeRandomStartMS} " +
                               $"WakeStartsVisible={bossProto.WakeStartsVisible} PlayDramaticEntrance={bossProto.PlayDramaticEntrance}");

            Inventory avatarInPlay = phantomPlayer.GetInventory(InventoryConvenienceLabel.AvatarInPlay);
            if (avatarInPlay != null)
            {
                ulong? stackEntityId = null;
                var moveResult = Inventory.ChangeEntityInventoryLocation(boss, avatarInPlay, 0, ref stackEntityId, false);
                if (moveResult != InventoryResult.Success)
                    PhantomLogger.Warn($"[PhantomHero:Boss] AvatarInPlay move failed ({moveResult}) — HP bar may not bind on client");
            }

            int effectiveLevel = level > 0 ? level : CharacterLevel;

            try { phantomPlayer.EnterGame(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Boss] phantomPlayer.EnterGame() partial: {ex.Message}"); }
            try { phantomPlayer.OnLoadingScreenFinished(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Boss] OnLoadingScreenFinished failed: {ex.Message}"); }

            try
            {
                // newOnServer:false — THIS is what stops the T-pose / replayed
                // dramatic entrance. Traced 2026-08-09 by fact, not theory:
                //
                //  * WorldEntity.SetAsPersistent only sets
                //    EntitySettingsOptionFlags.IsNewOnServer when newOnServer
                //    is true (WorldEntity.cs:572-574), and that flag is sent
                //    to the client as EntityCreateMessageFlags.IsNewOnServer
                //    (ArchiveMessageBuilder.cs:164). It is the client-side
                //    trigger for the spawn "materialize" sequence — the same
                //    mechanism WorldEntity.cs:576 documents as "the client's
                //    own 'materialize' entrance sequence (PlayDramaticEntrance)",
                //    noting that "a fresh login passes newOnServer=false,
                //    never sets the flag, and always rendered fine".
                //
                //  * Every engine-native call site passes false
                //    (Agent.cs:3158, Avatar.cs:5829, Avatar.cs:5876). Only the
                //    phantom spawn paths passed true.
                //
                //  * Confirmed against real prototype data this session:
                //    BullseyeCH4 has PlayDramaticEntrance=Always, so it
                //    replayed its entrance on EVERY respawn and landed in
                //    T-pose; LizardBase has WakeDelayMS=3000 (spawns dormant)
                //    and stuttered through its wake. Suppressing the server's
                //    own wake path via DramaticEntrancePlayedOnce did NOT stop
                //    either, because the client was driving it off this flag.
                //
                // Boss phantoms are never IsTeamUpAgent, so the
                // IsClientEntityHidden branch below that flag never applied to
                // them — dropping IsNewOnServer cannot leave a boss invisible
                // the way it could a team-up (see that branch's 1.48 note).
                boss.SetAsPersistent(this, false);

                // SetAsPersistent's own placement (WorldEntity.GetPositionNearAvatar)
                // validates the spot against the CALLER's bounds, not the boss's own —
                // fine for a team-up (roughly avatar-sized), but a boss like Rhino,
                // Blob, or a Sentinel is much bigger and can end up overlapping real
                // geometry at a spot that reads as clear for a normal-sized avatar.
                // Confirmed live 2026-08-08: every single boss phantom's first
                // FollowEntity attempt failed at short range (52-567u, nowhere near
                // the long-distance pathfinder cap), meaning they were stuck from the
                // very first tick — never actually followed the caller at all.
                // Re-validate against the boss's REAL bounds the same way
                // SpawnCuratedBoss/BossRosterWebHandler already do for hostile boss
                // spawns, and correct the position if it moved.
                if (EntityHelper.GetSpawnPositionNearAvatar(this, region, bossProto.Bounds, 250f, out Vector3 validatedPos))
                    boss.ChangeRegionPosition(validatedPos, null);
                else
                    PhantomLogger.Warn($"[PhantomHero:Boss] no bounds-validated spawn spot found for {bossRef.GetName()} — keeping SetAsPersistent's placement");

                // CreateAgent (the standalone-enemy-boss path) sets
                // DifficultyTier explicitly rather than relying on defaults —
                // mirror that here since we're not going through CreateAgent.
                boss.Properties[PropertyEnum.DifficultyTier] = region.DifficultyTierRef;

                boss.InitializeLevel(effectiveLevel);
                boss.CombatLevel = effectiveLevel;
                boss.Properties[PropertyEnum.PowerProgressionVersion] = boss.GetLatestPowerProgressionVersion();

                // Same fixups a standalone hostile boss gets (Dormant clear,
                // LootCooldown fallback, AICustomThinkRateMS, MODOK AI-
                // bootstrap fix) MINUS the AllianceOverride line — a friendly
                // boss phantom uses the CALLER's own alliance instead of the
                // hostile phantom alliance those fixups apply.
                // applyEnemyAlliance:false — this call site never wanted the
                // hostile-alliance override (see this block's comment), but
                // until now the helper applied it unconditionally and we just
                // overwrote it immediately after, leaving the boss genuinely
                // hostile to its own summoner for a moment while already
                // in-world and replicated.
                EntityHelper.ApplyStandaloneBossFixups(boss, bossProto, applyEnemyAlliance: false);
                boss.SetSummonedAllianceOverride(Alliance);

                boss.Properties[PropertyEnum.Health] = boss.Properties[PropertyEnum.HealthMax];
            }
            catch (Exception ex)
            {
                error = $"boss world entry failed: {ex.Message}";
                try { if (boss.IsInWorld) boss.ExitWorld(); boss.Destroy(); }
                catch (Exception cleanupEx) { PhantomLogger.Warn($"[PhantomHero:Boss] cleanup after failed entry threw: {cleanupEx.Message}"); }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            if (boss.IsInWorld == false)
            {
                error = "boss SetAsPersistent did not enter world";
                try { boss.Destroy(); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Boss] Destroy() after failed world entry threw: {ex.Message}"); }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            // Same friendly HP/damage curve regular phantom heroes and
            // team-ups use — per user request, NOT the boss's native
            // (much higher/lower, un-tuned-for-a-squad-slot) stats.
            boss.Properties[PropertyEnum.HealthMaxMult] = ScaleHealthMultForLevel(PhantomHealthMult, effectiveLevel);
            boss.Properties[PropertyEnum.Health] = boss.Properties[PropertyEnum.HealthMax];
            ApplyPhantomDamageScaling(boss, effectiveLevel, enemy: false);

            // Rank is deliberately NOT left at bossProto.Rank (Boss) — confirmed
            // live 2026-08-08/09: it triggers the client's full boss-encounter UI
            // (top-screen health bar + "Boss" banner), the same Rank-driven
            // treatment already established this session for Deathmatch's boss
            // glow. A friendly boss phantom is a squad member, not an encounter —
            // every other friendly phantom (team-up, avatar) is left at
            // Rank.Player, never Boss/MiniBoss.
            //
            // Set here, as the LAST property write before SetSimulated, not
            // earlier (originally set right after DifficultyTier, before
            // InitializeLevel/ApplyStandaloneBossFixups) — confirmed live it was
            // inconsistent: some bosses (Gorgon) correctly lost the boss banner,
            // others (DrDoomPhase2, BullseyeCH4 — both multi-phase/scripted
            // story-boss variants) kept it even with the override already
            // applied earlier. Per this session's own earlier Deathmatch Rank
            // investigation: WorldEntity's Rank property-change handler defers
            // attaching the Rank prototype's baked-in Mod bundle
            // (ModChangeModEffects, which is what actually drives the client's
            // boss-tier UI) until SetSimulated fires — so whatever Rank value
            // is current AT that exact moment is what sticks. InitializeLevel
            // (OnLevelUp) and ApplyStandaloneBossFixups run between the old set
            // point and SetSimulated, and for these scripted variants something
            // in that window was re-deriving Rank from the prototype before the
            // mod bundle attached, undoing an earlier override. Setting it here
            // instead closes that window entirely — nothing runs after this
            // before SetSimulated captures the value.
            // Confirmed live (2026-08-09): GetRankByEnum(Rank.Player) returns
            // null for every boss on every version — the population Globals
            // RankDefaults table has no RankPrototype entry for Rank.Player at
            // all (it's not a rank real players are ever tagged with), so
            // there was never a value to override TO. The actual working
            // pattern, confirmed by how friendly (non-enemy) team-up phantoms
            // handle this a few hundred lines up in SpawnTeamUpPhantomHero:
            // they never set Properties[PropertyEnum.Rank] in the first
            // place. So instead of overriding to a value, just strip
            // whatever Rank the boss prototype's own defaults baked in.
            boss.Properties.RemoveProperty(PropertyEnum.Rank);

            try { boss.SetSimulated(true); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Boss] SetSimulated(true) failed: {ex.Message}"); }

            // Force the boss awake AFTER SetSimulated — this is the boss-only
            // difference behind the reported glitchy/stuttering movement.
            //
            // Measured 2026-08-09 with matched [BossDiag:Move] traces of boss
            // and avatar phantoms in the same session: both stop-and-go at the
            // same rate (~58% vs ~60% of samples stationary — that cycling is
            // the shared friendly-follow design and is NOT boss-specific, so
            // it is deliberately left alone), but boss phantoms were dormant
            // in ~10% of samples while avatar phantoms were dormant in ZERO.
            //
            // Cause: any AgentPrototype with WakeRange > 0 is created dormant
            // (Agent.cs:161), and SetSimulated only auto-clears dormancy when
            // WakeRange <= 0 (Agent.cs:2304) — so a boss stayed dormant right
            // through spawn until the 2s StandaloneBossDormantWatchdog
            // happened to clear it. Every BASELINE confirmed it:
            // dormant=True at spawn-complete despite the earlier fixup clear.
            // The phantom tick issues movement to it during that window, so
            // it lurches instead of walking.
            //
            // Written directly rather than via SetDormant(false), which for a
            // prototype with WakeRandomStartMS > 0 does NOT clear dormancy at
            // all — it schedules a randomized delayed wake instead
            // (Agent.cs:2286), reintroducing the same nondeterministic window.
            boss.Properties[PropertyEnum.Dormant] = false;

            PrototypeId finalRankRef = boss.Properties[PropertyEnum.Rank];
            PhantomLogger.Info($"[PhantomHero:Boss] {bossRef.GetName()} final Rank after override: {(finalRankRef != PrototypeId.Invalid ? finalRankRef.GetNameFormatted() : "<unset>")}");

            var descriptor = new MHServerEmu.DatabaseAccess.Models.PhantomIntent
            {
                AvatarRef = (ulong)bossRef,
                Level = effectiveLevel,
                Username = username,
                LockLevel = false,
                CostumeRef = 0,
                GearRefs = null,
                Invincible = false,
                BypassCap = bypassCap,
            };
            host.RegisterPhantom(boss.Id, phantomPlayer.Id, descriptor);
            SchedulePhantomTick();

            // Take over combat decision-making from the native AIController —
            // same one-time disable as team-up phantoms. Safe here because of
            // the PowerUserOverrideID fix above (see its comment).
            try { boss.AIController?.SetIsEnabled(false); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Boss] AIController disable failed: {ex.Message}"); }

            PhantomLogger.Info($"[PhantomHero:Boss] {this} spawned friendly boss phantom '{bossRef.GetName()}' (agentId 0x{boss.Id:X}) at {boss.RegionLocation.Position.ToStringNames()} level {effectiveLevel}");

            // [BossDiag] Record end-of-spawn state as the baseline, so a boss
            // that comes in ALREADY wrong is distinguishable from one that
            // regresses later — the two have completely different causes and
            // the reported symptom covers both.
            // Same client push the avatar phantom path performs — without this
            // the client holds the boss with an empty PowerCollection and no
            // animation data (T-pose). See PushPhantomToClients' header.
            PushPhantomToClients(boss, phantomPlayer);

            BossDiagBaseline(boss, this, "spawn-complete");
            BossDiagOrphanScan(this, host, "after-spawn");

            return boss.Id;
        }

        /// <summary>
        /// Spawn a HOSTILE phantom carrying nemesis flavor — a fixed username
        /// (so it recognizably returns), rank-based HP scaling, and a name
        /// suffix applied to the avatar's nameplate. Used by Rogue Encounter
        /// when the roll picks a nemesis instead of a random hero.
        /// </summary>
        /// <param name="costumeRef">
        /// Optional forced costume. 0 (the default, and what every existing
        /// caller passes) keeps the original behaviour of picking a random
        /// costume. Only themed Bounty Board hunts pass a real value, so
        /// nothing outside that mode changes appearance.
        /// </param>
        public ulong SpawnNemesisPhantomHero(PrototypeId avatarRef, int level, string killerName, int rank, out string error, int escapeCount = 0, int grudgeScore = 0, ulong costumeRef = 0)
        {
            // Team-up nemeses go through the team-up spawn path so the
            // AgentTeamUpPrototype dispatch, native AI, and inventory
            // containment override all apply. usernameOverride is the killer
            // name from the roster entry so the returning team-up carries
            // the same recognizable name it did when it killed the player.
            if (avatarRef != PrototypeId.Invalid && avatarRef.As<AgentTeamUpPrototype>() != null)
                return SpawnTeamUpPhantomHero(avatarRef, level, out error, enemy: true, nemesisRank: rank, usernameOverride: killerName, nemesisEscapeCount: escapeCount, nemesisGrudgeScore: grudgeScore);

            // Nemeses are always ambush phantoms � see the constants block
            // above OnPhantomTick for what that changes (spawn distance,
            // detection range, patrol-vs-leash).
            return SpawnPhantomHeroCore(avatarRef, level, killerName, lockLevel: true, costumeRef, null, out error, enemy: true, nemesisRank: rank, nemesisEscapeCount: escapeCount, ambush: true, nemesisGrudgeScore: grudgeScore);
        }

        // Cached mutually-hostile alliance for enemy phantoms, resolved from
        // the loaded client data at runtime (first alliance that is hostile
        // both ways with the player alliance).
        private static PrototypeId s_enemyAllianceRef = PrototypeId.Invalid;
        private static bool s_enemyAllianceResolved;

        /// <summary>
        /// Public entry point for non-phantom enemy spawns (curated bosses,
        /// regular wave mobs) that need to share the exact same enemy
        /// alliance phantom heroes use � see Player.WaveDirector.cs's
        /// SpawnCuratedBoss and SpawnNextWave for why: without this, a boss
        /// or wave mob keeps its own native AgentPrototype alliance, which
        /// can be (and was, confirmed live 2026-07-26) genuinely mutually
        /// hostile with the phantom-hero alliance per the real game's own
        /// alliance table � two different "enemy" NPCs then fight each
        /// other. Alliances are auto-friendly to themselves, so putting all
        /// three enemy categories on this same override alliance makes them
        /// mutually friendly while all three stay hostile to the player.
        /// </summary>
        // ---------------- Squad size / solo-only gate (BOUNTY BOARD ONLY) ----------------
        //
        // Design rule: a squad is at most THREE members total, and phantom
        // heroes are a SOLO feature. So the only legal shapes are:
        //   1 player + 2 phantoms
        //   2 players (no phantoms)
        //   3 players (no phantoms)
        // The moment a real party has more than one human in it, phantoms are
        // off entirely - they exist to give a solo player a squad, not to pad
        // an already-grouped one.
        //
        // Applies to friendly phantoms only. Enemy phantoms (nemeses, Rogue
        // Encounter ambushes, Bounty Board targets) are not squad members and
        // are never gated by this, and neither is anything passing bypassCap.
        //
        // The Danger Room Endless arena is EXEMPT: it has its own deliberately
        // higher 4-total co-op cap (Player.GetEndlessPhantomSlotCap) and that
        // mode's existing behaviour must not change.

        /// <summary>Hard ceiling on total squad size � real players plus friendly phantoms.</summary>
        public const int PhantomMaxSquadSize = 3;

        /// <summary>Most friendly phantoms a solo player may field (squad cap minus the player themselves).</summary>
        public const int PhantomMaxForSoloPlayer = PhantomMaxSquadSize - 1;

        /// <summary>
        /// Null when this host may spawn one more friendly phantom, otherwise
        /// the reason they may not.
        ///
        /// BOUNTY BOARD ONLY. Returns null immediately outside an active board
        /// hunt, so normal play, Trial of the Impossible and Endless Challenge
        /// all keep unrestricted phantom counts exactly as before. Checked by
        /// both friendly spawn paths (SpawnPhantomHeroCore and
        /// SpawnTeamUpPhantomHero).
        /// </summary>
        public static string CheckPhantomSquadGate(Player host)
        {
            if (host == null) return "no player host";
            if (host.IsBountyBoardHuntActive == false) return null;   // not a board hunt - no limit
            return CheckPhantomSquadGateForBountyBoard(host, spawningAnother: true);
        }

        /// <summary>
        /// The Bounty Board squad rule itself, with no mode check of its own so
        /// the caller decides when it applies. A bounty is a three-member
        /// fight: one player plus two phantoms, or two/three real players with
        /// no phantoms.
        /// </summary>
        /// <param name="spawningAnother">
        /// True when about to add one more phantom (so the cap is checked
        /// against current + 1), false when validating the squad as it stands
        /// - e.g. at the moment a bounty is posted.
        /// </param>
        public static string CheckPhantomSquadGateForBountyBoard(Player host, bool spawningAnother)
        {
            if (host == null) return "no player host";

            // Real party members, counting the host. GetParty() is null when
            // solo, and a party of 1 is effectively solo too.
            var party = host.GetParty();
            int realPlayers = party != null && party.NumMembers > 0 ? party.NumMembers : 1;

            if (realPlayers > 1 && host.PhantomHeroCount > 0)
                return $"bounties are solo-only for phantom squads � you're grouped with {realPlayers - 1} other player(s). Retire your phantoms or leave the party.";

            if (realPlayers > PhantomMaxSquadSize)
                return $"party too large for a bounty ({realPlayers}/{PhantomMaxSquadSize})";

            int squad = realPlayers + host.PhantomHeroCount + (spawningAnother ? 1 : 0);
            if (squad > PhantomMaxSquadSize)
                return $"squad too large for a bounty ({squad}/{PhantomMaxSquadSize}) � you + {PhantomMaxForSoloPlayer} phantoms, or up to {PhantomMaxSquadSize} players";

            return null;
        }

        public static PrototypeId GetEnemyPhantomAllianceRef() => ResolveHostileAllianceRef();

        private static PrototypeId ResolveHostileAllianceRef()
        {
            if (s_enemyAllianceResolved) return s_enemyAllianceRef;

            AlliancePrototype playerAlliance = GameDatabase.GlobalsPrototype?.PlayerAlliance;
            if (playerAlliance != null)
            {
                // Prefer the real game's own standard "Enemies" alliance �
                // the same one native AI mobs already use, mutually hostile
                // with Players only. Confirmed live 2026-08-03 via
                // /webapi/protoeditor/fields: the old "first mutually-hostile
                // alliance found by arbitrary DataDirectory iteration order"
                // scan below happened to land on Entity/Alliances/
                // DestructablesHostile.prototype instead, whose own
                // HostileTo list also includes Destructables (props) AND
                // Enemies itself � explaining reported phantom/nemesis
                // damage to props/throwables and to other (native-mob)
                // enemies. Only falls back to the scan if Enemies.prototype
                // itself is missing or somehow not mutually hostile.
                PrototypeId preferredRef = GameDatabase.GetPrototypeRefByName("Entity/Alliances/Enemies.prototype");
                if (preferredRef != PrototypeId.Invalid)
                {
                    var preferredProto = preferredRef.As<AlliancePrototype>();
                    if (preferredProto != null && preferredProto.IsHostileTo(playerAlliance) && playerAlliance.IsHostileTo(preferredProto))
                        s_enemyAllianceRef = preferredRef;
                }

                if (s_enemyAllianceRef == PrototypeId.Invalid)
                {
                    foreach (PrototypeId allianceRef in DataDirectory.Instance
                        .IteratePrototypesInHierarchy<AlliancePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                    {
                        var allianceProto = allianceRef.As<AlliancePrototype>();
                        if (allianceProto == null) continue;
                        if (allianceProto.IsHostileTo(playerAlliance) && playerAlliance.IsHostileTo(allianceProto))
                        {
                            s_enemyAllianceRef = allianceRef;
                            break;
                        }
                    }
                }
            }

            s_enemyAllianceResolved = true;
            if (s_enemyAllianceRef == PrototypeId.Invalid)
                PhantomLogger.Warn("[PhantomHero:Enemy] no mutually-hostile alliance found in loaded data � enemy phantoms unavailable");
            else
                PhantomLogger.Info($"[PhantomHero:Enemy] hostile alliance resolved: {s_enemyAllianceRef.GetName()}");
            return s_enemyAllianceRef;
        }

        // Mirrors RegionPrototype.GetQueueGroupLimit()'s Behavior switch (that
        // method is private, and TeamLimits there is PvP matchplay sizing,
        // not relevant to a friendly squad) � friendly phantoms/team-ups are
        // "party" members in spirit, so a squad shouldn't be able to exceed
        // what the game itself would ever allow a real party/raid to be in
        // this region. Town/PublicCombatZone/MatchPlay/etc aren't party-size
        // gated in the real game either, so they're left uncapped here too.
        public static int GetPhantomPartyCap(Region region)
        {
            RegionPrototype proto = region?.Prototype;
            if (proto == null) return int.MaxValue;

            var globals = GameDatabase.GlobalsPrototype;
            if (globals == null) return int.MaxValue;

            switch (proto.Behavior)
            {
                case RegionBehavior.PrivateRaid:
                    return Math.Min(globals.PlayerRaidMaxSize, proto.PlayerLimit);
                case RegionBehavior.PrivateStory:
                case RegionBehavior.PrivateNonStory:
                    return Math.Min(globals.PlayerPartyMaxSize, proto.PlayerLimit);
                default:
                    return int.MaxValue;
            }
        }

        private ulong SpawnPhantomHeroCore(PrototypeId avatarRefOverride, int levelOverride, string username, bool lockLevel, ulong costumeRef, List<ulong> gearRefs, out string error, bool enemy = false, bool invincible = false, int nemesisRank = 0, int nemesisEscapeCount = 0, bool bypassCap = false, bool ambush = false, int nemesisGrudgeScore = 0)
        {
            if (enemy && ResolveHostileAllianceRef() == PrototypeId.Invalid)
            {
                error = "no hostile alliance in loaded data";
                return 0;
            }
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }

            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            if (enemy == false && bypassCap == false)
            {
                Player capHost = PhantomHost;
                if (capHost != null)
                {
                    // See the other GetEndlessPhantomSlotCap call site (this
                    // file's SpawnTeamUpPhantomHero) for why the Endless
                    // cap fully overrides the generic one instead of being
                    // Math.Min'd against it.
                    int endlessCap = Player.GetEndlessPhantomSlotCap(region, capHost);
                    bool inEndlessArena = endlessCap != int.MaxValue;

                    string squadGate = CheckPhantomSquadGate(capHost);
                    if (squadGate != null) { error = squadGate; return 0; }

                    int cap = inEndlessArena ? endlessCap : GetPhantomPartyCap(region);
                    // See the other call site's comment � off-by-one fixed:
                    // a cap of N should allow N phantoms, not N-1.
                    if (1 + capHost.PhantomHeroCount > cap)
                    {
                        error = $"squad full ({capHost.PhantomHeroCount + 1}/{cap}) � this region's party/raid cap won't allow another phantom";
                        return 0;
                    }
                }
            }

            PrototypeId avatarRef = avatarRefOverride != PrototypeId.Invalid ? avatarRefOverride : NextPhantomHeroRef();
            if (avatarRef == PrototypeId.Invalid) { error = "hero ref resolve failed"; return 0; }

            AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
            if (avatarProto == null) { error = "not an AvatarPrototype"; return 0; }

            // Step 1: phantom Player entity, no PlayerConnection. The Player
            // is created inside this Game's EntityManager so InventoryLocation
            // ContainerId lookups (used by Avatar.ApplyInitialReplicationState)
            // resolve locally without touching the login/DB path.
            ulong phantomDbId = System.Threading.Interlocked.Increment(ref s_phantomDbIdSeed);
            // If caller didn't supply a name, mint a comic-book-flavored one so
            // nameplates read "CrimsonFalcon042" instead of "Bot001".
            if (string.IsNullOrEmpty(username))
                username = NewPhantomUsername(Game.Random);
            Player phantomPlayer;
            using (var playerSettingsHandle = EntitySettingsPool.Get(out EntitySettings playerSettings))
            {
                playerSettings.DbGuid = phantomDbId;
                playerSettings.EntityRef = GameDatabase.GlobalsPrototype.DefaultPlayer;
                playerSettings.OptionFlags = EntitySettingsOptionFlags.PopulateInventories;
                playerSettings.PlayerConnection = null; // Player.SendMessage is null-conditional; OK.
                playerSettings.PlayerName = username;
                playerSettings.ArchiveSerializeType = ArchiveSerializeType.Database;
                playerSettings.ArchiveData = null; // fresh account, triggers new-account init

                phantomPlayer = Game.EntityManager.CreateEntity(playerSettings) as Player;
            }
            if (phantomPlayer == null) { error = "phantom Player entity create failed"; return 0; }

            // Stamp the human's Player entity id on the phantom so kill /
            // damage-tag paths can substitute the phantom out and credit the
            // real player. Without this, mission counters and loot rolls skip
            // phantom kills because Player.IsMissionPlayer on the synthetic
            // phantom Player always returns false.
            // ENEMY phantoms deliberately skip this � their kills must never
            // credit the human who spawned them.
            Player humanHost = PhantomHost;
            if (humanHost != null && enemy == false) phantomPlayer.PhantomCreatorId = humanHost.Id;

            // Step 2: create the Avatar as a child of the phantom Player. Uses
            // the same Player.CreateAvatar helper the real login path calls
            // (PlayerConnection.LoadFromDBAccount:222).
            Avatar phantomAvatar = phantomPlayer.CreateAvatar(avatarRef);
            if (phantomAvatar == null) { error = $"CreateAvatar failed for {avatarRef.GetName()}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 3: move avatar from AvatarLibrary to AvatarInPlay so it can
            // enter the world. Same handshake the real client's SwitchAvatar
            // NetMessage triggers (PlayerConnection.LoadFromDBAccount:246-249).
            Inventory avatarInPlay = phantomPlayer.GetInventory(InventoryConvenienceLabel.AvatarInPlay);
            if (avatarInPlay == null) { error = "AvatarInPlay inventory missing"; DestroyPhantomPlayer(phantomPlayer); return 0; }
            InventoryResult moveResult = phantomAvatar.ChangeInventoryLocation(avatarInPlay);
            if (moveResult != InventoryResult.Success) { error = $"ChangeInventoryLocation failed: {moveResult}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 4: level + resources so the avatar has real stats.
            int effectiveLevel = levelOverride > 0 ? levelOverride : CharacterLevel;
            phantomAvatar.InitializeLevel(effectiveLevel);
            phantomAvatar.CombatLevel = effectiveLevel;
            phantomAvatar.ResetResources(false);

            // Step 4b: costume. Explicit ref (squad restore / migration /
            // command) wins; otherwise roll a random one from the avatar's
            // costume pool so phantom crowds don't all wear the default.
            // Applied before EnterWorld so the initial replication already
            // carries the final look. The ACTUAL applied ref is stored in
            // the descriptor below, so saves/transfers reproduce this
            // costume instead of re-rolling.
            PrototypeId appliedCostumeRef = costumeRef != 0 ? (PrototypeId)costumeRef : PickRandomCostume(avatarRef, Game.Random);
            if (appliedCostumeRef != PrototypeId.Invalid)
            {
                if (phantomAvatar.ChangeCostume(appliedCostumeRef) == false)
                {
                    PhantomLogger.Warn($"[PhantomHero] ChangeCostume({appliedCostumeRef.GetName()}) failed for {avatarRef.GetName()}");
                    appliedCostumeRef = PrototypeId.Invalid;
                }
            }

            // Step 4c: gear � one level-appropriate item per unlocked equip
            // slot (or the stored set on squad/migration restore). Fills
            // hero-specific weapon slots too, which un-breaks WeaponMissing
            // powers. The applied refs go on the descriptor below.
            //
            // Rank-5 level-60 nemeses WEAR the community best-in-slot set for
            // their hero � a real gear-check fight, and (because the drop path
            // drops what's worn) a full BiS jackpot on defeat. Level-60
            // friendly phantoms get the same BiS set for the same reason a
            // real level-60 player would be geared, not because they're
            // "the content" � a squadmate that's still rolling random
            // level-banded gear at max level reads as broken, not balanced
            // (confirmed live 2026-07-19: "they are just wearing crappy
            // random gear"). Sub-max-rank enemy phantoms and everyone below
            // level 60 still roll the normal level-banded random gear.
            IReadOnlyDictionary<EquipmentInvUISlot, PrototypeId> bisLoadout = null;
            bool wantsBiS = effectiveLevel >= 60 && (enemy == false || nemesisRank >= Player.NemesisMaxRank);
            if (wantsBiS && PhantomBiSData.TryGetLoadout(avatarRef, Game, out var bis))
            {
                bisLoadout = bis;
            }
            List<ulong> appliedGearRefs = ApplyPhantomGear(phantomPlayer, phantomAvatar, effectiveLevel, gearRefs, bisLoadout);

            // Step 5: pick a spawn point and enter the world.
            //   * Friendly / farm-tool enemy phantoms: close (150-320u) so
            //     they land within visible radius and read as "with you"
            //     instead of "over there".
            //   * Ambush phantoms (Rogue Encounter, nemeses): far enough
            //     (900-1600u) that they never spawn on top of the player �
            //     they're meant to be discovered, not appear in your face.
            //   * No stacking � reject candidates within PhantomMinSpacing of
            //     any already-alive phantom. Up to 8 tries; last try
            //     accepted regardless so we never fail-to-spawn on a crowd.
            var rng = Game.Random;
            Vector3 origin = RegionLocation.Position;
            Vector3 candidate = origin;
            float minRadius = ambush ? NemesisSpawnMinRadius : 150f;
            float maxRadius = ambush ? NemesisSpawnMaxRadius : 320f;
            const float PhantomMinSpacing = 130f;              // � 1.4 avatar widths
            const float PhantomMinSpacingSq = PhantomMinSpacing * PhantomMinSpacing;
            Player spacingHost = PhantomHost;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            float navRadius = MathF.Max(20f, phantomAvatar.Bounds.Radius);
            Vector3 bestWalkableCandidate = origin;
            bool foundWalkableCandidate = false;
            // Ambush spawns cover a much bigger radius (900-1600u) than the
            // old close-in 150-320u range, which makes landing off-navmesh
            // (a narrow catwalk, a railed-off ledge, a disconnected platform)
            // far more likely � confirmed live: a nemesis spawned onto a dead
            // -end walkway and could never leave it. Reject candidates the
            // navmesh doesn't consider walkable instead of trusting whatever
            // ProjectToFloor happens to land on.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = minRadius + (float)(rng.NextDouble() * (maxRadius - minRadius));
                candidate = origin + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0f);
                Vector3 floored = RegionLocation.ProjectToFloor(region, candidate);

                // Confirmed live 2026-07-31 (Trial of the Impossible, 1.48,
                // NightclubRegion): the navmesh alone isn't enough -- a point
                // can be NaviMesh.Contains()-walkable yet have no backing
                // Cell at all (region.GetCellAtPosition returns null), which
                // makes EnterWorld's ChangeRegionPosition fail with
                // Result=InvalidCell. That aborted the whole run because
                // early stages only try a single hero. Require both checks.
                bool isWalkable = region.NaviMesh.Contains(floored, navRadius, walkCheck)
                    && region.GetCellAtPosition(floored) != null;
                if (isWalkable && foundWalkableCandidate == false)
                {
                    bestWalkableCandidate = floored;
                    foundWalkableCandidate = true;
                }

                bool tooClose = false;
                if (spacingHost != null && spacingHost.PhantomHeroCount > 0)
                {
                    for (int i = 0; i < spacingHost.PhantomAvatarIds.Count; i++)
                    {
                        Avatar existing = Game.EntityManager.GetEntity<Avatar>(spacingHost.PhantomAvatarIds[i]);
                        if (existing == null || existing.IsInWorld == false) continue;
                        if (Vector3.DistanceSquared2D(existing.RegionLocation.Position, floored) < PhantomMinSpacingSq) { tooClose = true; break; }
                    }
                }

                if (isWalkable && tooClose == false)
                {
                    candidate = floored;
                    break;
                }
                // On the last attempt, fall back to the best walkable spot
                // found (even if it clipped spacing). If NOTHING in the
                // whole ring came back walkable � confirmed live 2026-07-25:
                // in a small enclosed scenario room (a repurposed Danger
                // Room arena, far smaller than the 1200-2000u ambush ring
                // this loop assumes), EVERY one of the 8 attempts lands
                // outside the room, off-navmesh � shrink the radius
                // progressively and keep checking the navmesh instead of
                // blindly accepting an unchecked point. Only falls back to
                // an unchecked point in the genuinely degenerate case where
                // nothing is walkable anywhere down to a close-in radius
                // either (old behavior, kept as the last resort � still
                // never literally on top of the player).
                if (attempt == 7)
                {
                    if (foundWalkableCandidate)
                    {
                        candidate = bestWalkableCandidate;
                    }
                    else
                    {
                        bool shrunkCandidateFound = false;
                        for (float shrinkRadius = minRadius * 0.5f; shrinkRadius >= 100f; shrinkRadius *= 0.5f)
                        {
                            for (int shrinkAttempt = 0; shrinkAttempt < 4; shrinkAttempt++)
                            {
                                float shrinkAng = (float)(rng.NextDouble() * Math.PI * 2.0);
                                Vector3 shrinkCandidate = origin + new Vector3((float)Math.Cos(shrinkAng) * shrinkRadius, (float)Math.Sin(shrinkAng) * shrinkRadius, 0f);
                                Vector3 shrinkFloored = RegionLocation.ProjectToFloor(region, shrinkCandidate);
                                if (region.NaviMesh.Contains(shrinkFloored, navRadius, walkCheck)
                                    && region.GetCellAtPosition(shrinkFloored) != null)
                                {
                                    candidate = shrinkFloored;
                                    shrunkCandidateFound = true;
                                    break;
                                }
                            }
                            if (shrunkCandidateFound) break;
                        }

                        if (shrunkCandidateFound == false)
                        {
                            float fallbackAng = (float)(rng.NextDouble() * Math.PI * 2.0);
                            candidate = origin + new Vector3((float)Math.Cos(fallbackAng) * minRadius, (float)Math.Sin(fallbackAng) * minRadius, 0f);
                        }
                    }
                }
            }
            Vector3 spawnPos = RegionLocation.ProjectToFloor(region, candidate);
            Orientation spawnOri = RegionLocation.Orientation;

            // Mark phantom Player + Avatar as IsInGame so real clients' AOI
            // GetNewInterestPolicies passes the (entity.IsInGame == false) gate
            // and actually broadcasts them. Player.EnterGame cascades into
            // contained entities (avatar + inventories) via Entity.EnterGame.
            // Any downstream init that assumes a real client is trapped; the
            // essential IsInGame flag lands on entity-level first.
            try { phantomPlayer.EnterGame(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] phantomPlayer.EnterGame() partial: {ex.Message}"); }

            // Clear the loading-screen state that Player.Initialize (line 233 �
            // QueueLoadingScreen(Invalid)) sets unconditionally on every Player.
            // Real clients ack it and it clears; the phantom has no client to ack.
            // Left set, it makes IsFullscreenObscured=true ? every power activation
            // returns PowerUseResult.FullscreenMovie via Agent.CanTriggerPower line 500.
            try { phantomPlayer.OnLoadingScreenFinished(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] OnLoadingScreenFinished failed: {ex.Message}"); }

            if (phantomAvatar.EnterWorld(region, spawnPos, spawnOri) == false)
            {
                error = "EnterWorld returned false";
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            // Force AOI broadcast to every real client so they see the phantom.
            // Without this the phantom exists server-side but no NetMessage tells
            // clients about it � invisible bot.
            try
            {
                // Broadcast phantom Player first so client can resolve avatar owner.
                phantomPlayer.UpdateInterestPolicies(true, null);
                phantomAvatar.UpdateInterestPolicies(true, null);

                // Re-broadcast the phantom's PowerCollection to every real
                // player that now has it in AOI. PowerCollection.AssignPower
                // only ships NetMessagePowerCollectionAssignPower to
                // clients when _owner.IsInGame is true (PowerCollection.cs
                // line 339) � but phantom powers get assigned during
                // CreateAvatar / InitializeLevel BEFORE phantomPlayer.
                // EnterGame() runs. That means the initial assign batch
                // never reaches anyone, and the client's PowerCollection
                // for this phantom stays empty. Empty collection ->
                // NetMessageActivatePower arrives referencing a power the
                // client doesn't know the phantom has -> the cast
                // animation never plays -> no VFX. Sending the whole
                // collection here fixes both: cast animations play and
                // VFX renders for every phantom power.
                if (phantomAvatar.PowerCollection != null)
                {
                    int collectionSize = 0;
                    foreach (var _ in phantomAvatar.PowerCollection) collectionSize++;
                    foreach (Player realPlayer in new PlayerIterator(Game))
                    {
                        if (realPlayer.PlayerConnection == null) continue;
                        var aoi = realPlayer.AOI;
                        if (aoi == null) continue;
                        bool interested = aoi.InterestedInEntity(phantomAvatar.Id, AOINetworkPolicyValues.AOIChannelProximity);
                        if (!interested)
                        {
                            PhantomLogger.Trace($"[PhantomHero:PowerSync] SKIP {realPlayer.GetName()} � not interested in phantom {phantomAvatar.Id:X} (proximity=false). collectionSize={collectionSize}");
                            continue;
                        }
                        bool sent = phantomAvatar.PowerCollection.SendEntireCollection(realPlayer);
                        PhantomLogger.Trace($"[PhantomHero:PowerSync] {realPlayer.GetName()} ? phantom {phantomAvatar.Id:X} collection ({collectionSize} powers) sent={sent}");
                    }
                }
                else
                {
                    PhantomLogger.Warn($"[PhantomHero:PowerSync] phantom {phantomAvatar.Id:X} has no PowerCollection at spawn � client can't render any VFX");
                }

                // Diagnostic: log what each real player's AOI decided for the phantom.
                foreach (Player realPlayer in new PlayerIterator(Game))
                {
                    if (realPlayer.PlayerConnection == null) continue;
                    var aoi = realPlayer.AOI;
                    if (aoi == null) { PhantomLogger.Trace($"[PhantomHero:AOI] real={realPlayer} AOI=null"); continue; }
                    bool avatarInterested = aoi.InterestedInEntity(phantomAvatar.Id);
                    bool playerInterested = aoi.InterestedInEntity(phantomPlayer.Id);
                    Vector3 phantomPos = phantomAvatar.RegionLocation.Position;
                    Vector3 realPos = realPlayer.CurrentAvatar?.RegionLocation.Position ?? Vector3.Zero;
                    float dist = Vector3.Distance2D(phantomPos, realPos);
                    PhantomLogger.Trace($"[PhantomHero:AOI] real={realPlayer.GetName()} sameRegion={realPlayer.GetRegion() == region} avatarInterested={avatarInterested} playerInterested={playerInterested} dist={dist:F0} phantomPos={phantomPos.ToStringNames()} realPos={realPos.ToStringNames()} inWorld={phantomAvatar.IsInWorld} cell={phantomAvatar.Cell?.Id.ToString() ?? "null"}");
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] AOI broadcast failed: {ex.Message}"); }

            // Hoisted above the enemy/friendly branch below so both sides
            // (and the ApplyPhantomDamageScaling call further down) can see
            // it � Deathmatch needs its own damage profile regardless of
            // which team a phantom is on.
            Player realHost = GetOwnerOfType<Player>();
            bool inDeathmatch = realHost != null && realHost.IsDeathmatchActive;

            if (enemy)
            {
                // Alliance flip: hostile both ways with the player alliance,
                // resolved from data at spawn. This is what makes the hunt
                // loop target players/friendly phantoms, mobs ignore them,
                // and players able to damage them.
                phantomAvatar.Properties[PropertyEnum.AllianceOverride] = ResolveHostileAllianceRef();

                // Same reliability fix applied to standalone curated bosses
                // (EntityHelper.ApplyStandaloneBossFixups) � Dormant can
                // leave a spawn sitting idle until a player closes the
                // native WakeRange gap, and natural AI sensing range can be
                // too short for a hostile phantom spawned well away from the
                // player it's meant to ambush, making it sit inert until hit
                // once (which force-registers the attacker through a
                // separate path) and then go idle again once that fades.
                // Widen the aggro range (read from AIController.Blackboard,
                // NOT the entity's own Properties � a separate collection)
                // rather than hard-pinning one target id: Combat.
                // GetValidTargetsInSphere still searches the whole hostile
                // pool within that radius, so this phantom can engage the
                // player's OTHER friendly phantom heroes/team-ups too, not
                // just lock onto this one real player forever.
                phantomAvatar.Properties[PropertyEnum.Dormant] = false;
                if (phantomAvatar.AIController != null)
                {
                    var phantomBlackboard = phantomAvatar.AIController.Blackboard.PropertyCollection;
                    phantomBlackboard[PropertyEnum.AIAggroRangeOverrideHostile] = PhantomStandaloneAggroRange;
                    phantomBlackboard[PropertyEnum.AIAggroRangeOverrideAlly] = PhantomStandaloneAggroRange;
                }

                // Enemy phantoms already have their own custom gear-drop
                // suppression gated on IsEndlessChallengeActive (see the
                // DropPhantomGear-style check elsewhere in this file) � that
                // covers OUR bespoke drop logic, but not the entity's native
                // WorldEntity.AwardKillLoot/AwardHitLoot paths, which key off
                // the PropertyEnum.NoLootDrop Property instead. Set it too so
                // both mechanisms are actually suppressed during an Endless
                // run, not just our own custom one.
                //
                // Deathmatch gets the same treatment plus NoExpOnDeath. Those
                // are two SEPARATE gates, not one: AwardKillLoot checks
                // NoLootDrop for the loot block (WorldEntity.cs:3973) but
                // NoExpOnDeath for the XP block (WorldEntity.cs:4011), so
                // NoLootDrop on its own still let experience orbs through �
                // exactly what was seen live 2026-08-04. AwardHitLoot
                // (WorldEntity.cs:4033) reads NoLootDrop as well, so mid-fight
                // on-hit drops are covered by the same flag.
                //
                // _deathmatchActive is set in OnAvatarEnteredRegionForDeathmatch
                // (Player.Deathmatch.cs:240) before any phantom spawns, so it is
                // reliably true here for both brackets. (realHost/inDeathmatch
                // now computed once above, before this if(enemy) block.)
                if (realHost != null && (realHost.IsEndlessChallengeActive || inDeathmatch))
                    phantomAvatar.Properties[PropertyEnum.NoLootDrop] = true;

                if (inDeathmatch)
                    phantomAvatar.Properties[PropertyEnum.NoExpOnDeath] = true;

                // Nemesis rank now maps directly to a TOTAL HealthMaxMult
                // (not a factor on top of the enemy base) � see the
                // per-rank table in Player.NemesisHealthMultForRank. Fresh
                // rogues use the enemy base (3.0�); rank N replaces it.
                //
                // These multipliers (8�-32�) were sized for a well-geared
                // level 60 � confirmed live: a level-15 rogue with the flat
                // 8� base felt like it "barely took damage," since a
                // level-15 hero's real damage output is nowhere near what
                // the multiplier assumes. ScaleHealthMultForLevel ramps the
                // multiplier down at low levels (35% of the full value at
                // level 1) up to the full intended tankiness at level 60,
                // using the same quadratic curve ApplyPhantomDamageScaling
                // already uses for the matching damage-side scaling.
                float ambushHpBase = inDeathmatch
                    ? DeathmatchPhantomHealthMult
                    : nemesisRank > 0
                        ? Player.NemesisHealthMultForRank(nemesisRank) * (1f + Player.NemesisEscapeHealthBonusPerEscape * nemesisEscapeCount)
                            * (1f + Player.NemesisGrudgeHealthBonusPerPoint * Math.Max(0, nemesisGrudgeScore))
                        : EnemyPhantomHealthMult;
                phantomAvatar.Properties[PropertyEnum.HealthMaxMult] = ScaleHealthMultForLevel(ambushHpBase, effectiveLevel);
                phantomAvatar.ResetResources(false);

                // Nameplate rank tag � the client uses PropertyEnum.Rank to
                // decide which UI treatment to render:
                //   * Nemeses get Boss rank ? top-of-screen boss bar with
                //     portrait (same UI as Sinister Six / Master of Evil).
                //   * Regular rogue phantoms get MiniBoss ? elite nameplate
                //     over their head, more visible than a stock Popcorn.
                // No effect on loot/XP � phantoms already don't credit
                // scoring or drop tables (see PurgeEnemyPhantoms path).
                //
                // Kept ON for Deathmatch too � confirmed live (2026-08-08)
                // this is where the enemy team's orange glow/elite visual
                // actually comes from; removing the tag to stop the Mod
                // bundle's stat contamination (see header) also silently
                // killed the one thing making the enemy team visually
                // distinct from your own. CorrectDeathmatchPhantomStatsAfterRankTag
                // below re-solves DamageMult/HealthMaxMult back onto the
                // deathmatch curve right after SetSimulated fires, so the
                // glow survives without the stat stacking coming back.
                {
                    try
                    {
                        var globals = GameDatabase.PopulationGlobalsPrototype;
                        if (globals != null)
                        {
                            var rankProto = nemesisRank > 0
                                ? globals.GetRankByEnum(Rank.Boss)
                                : globals.GetRankByEnum(Rank.MiniBoss);
                            if (rankProto != null)
                                phantomAvatar.Properties[PropertyEnum.Rank] = rankProto.DataRef;
                        }
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Enemy] rank tag failed: {ex.Message}"); }
                }
            }
            else if (invincible)
            {
                // Opt-in god mode from Squad Builder � hits do nothing.
                // Marked as deliberate so the stuck-invulnerable watchdog
                // (PhantomSharedMaintenance) never force-clears it.
                phantomAvatar.Properties[PropertyEnum.Invulnerable] = true;
                s_phantomDeliberatelyInvincible.Add(phantomAvatar.Id);
            }
            else
            {
                // Killable phantoms: real players dodge, kite and pop
                // defensives � phantoms eat every hit face-first. A HP
                // buff keeps them alive long enough to matter without
                // trivializing content. Refill after the mult applies.
                // Deathmatch teammates use the same symmetric HP profile the
                // enemy branch above uses � see DeathmatchPhantomHealthMult.
                phantomAvatar.Properties[PropertyEnum.HealthMaxMult] = ScaleHealthMultForLevel(inDeathmatch ? DeathmatchPhantomHealthMult : PhantomHealthMult, effectiveLevel);
                phantomAvatar.ResetResources(false);
            }

            // Damage scaling � see ApplyPhantomDamageScaling for the level
            // curve. Friendly phantoms use the "helpful teammate" anchor;
            // enemy phantoms use a higher-anchored curve so rogue encounters
            // actually threaten a geared 60. Deathmatch overrides both with
            // its own lower PvP curve, applied to every combatant regardless
            // of team � see DeathmatchPhantomDmgMultLvl60's comment.
            ApplyPhantomDamageScaling(phantomAvatar, effectiveLevel, enemy, inDeathmatch);

            // Nemesis-only damage boost � applied AFTER ApplyPhantomDamageScaling
            // so it doesn't get overwritten by the level curve. Per-rank
            // fractional boost from the softened balance table (rank 1 = +5%,
            // rank 5 = +60%). See Player.NemesisDmgBoostForRank.
            if (enemy && nemesisRank > 0)
            {
                float dmgBoost = Player.NemesisDmgBoostForRank(nemesisRank) + Player.NemesisGrudgeDmgBoostPerPoint * Math.Max(0, nemesisGrudgeScore);
                float currentDmgMult = phantomAvatar.Properties[PropertyEnum.DamageMult];
                phantomAvatar.Properties[PropertyEnum.DamageMult] = (currentDmgMult <= 0f ? 1f : currentDmgMult) * (1f + dmgBoost);
            }

            // Server-authoritative movement � real avatars have IsMovementAuthoritative=false
            // because the client drives them. Phantoms have no client, so we must
            // flip it, or Locomotor.FollowEntity produces no visible walking on
            // the real client's screen.
            phantomAvatar.IsPhantomHero = true;

            // Force simulation on. WorldEntity.SetSimulated adds the entity to
            // EntityCollection.Locomotion which is what actually steps
            // Locomotor path progress per tick AND broadcasts LocomotionState
            // changes to interested clients. Without this, the tick still
            // updates position but the client receives only raw position
            // snaps � no walk animation, hence the "sliding" look. Real
            // players get flipped simulated=true when a peer's AOI notices
            // them; phantoms may not go through that path reliably.
            try { phantomAvatar.SetSimulated(true); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] SetSimulated(true) failed: {ex.Message}"); }

            // Cap the final DamageMult AFTER SetSimulated, not before � see
            // ClampEnemyPhantomDamageMult's header for why. Confirmed live
            // (2026-07-21): the Rank tag set earlier isn't purely cosmetic
            // the way the original comment assumed � WorldEntity's Rank
            // property-change handler defers applying the Rank prototype's
            // own baked-in Mod bundle (ModChangeModEffects, real base-game
            // damage/health/passive-power bonuses tied to the Boss/MiniBoss
            // tier) until SetSimulated fires. A clamp placed before
            // SetSimulated gets silently overridden by this � confirmed by
            // rank-5 phantoms landing on a suspiciously uniform
            // dmgMult=10.00 in live combat despite the 5.0 cap.
            //
            // Unconditional on rank (not gated to nemesisRank > 0): even a
            // plain rogue's real gear roll (or the MiniBoss rank mod itself)
            // can push the aggregate past a sane ceiling on its own.
            if (enemy) ClampEnemyPhantomDamageMult(phantomAvatar);
            if (enemy) LogEnemyPhantomFinalStats(phantomAvatar, nemesisRank);

            // Deathmatch enemy phantoms keep the MiniBoss Rank tag (for the
            // orange glow) but its Mod bundle just added to both DamageMult
            // AND HealthMaxMult above, same mechanism ClampEnemyPhantomDamageMult
            // corrects for damage alone. Re-solve BOTH back onto the
            // deathmatch curve here so the visual survives without its
            // numbers coming back.
            if (enemy && inDeathmatch) CorrectDeathmatchPhantomStatsAfterRankTag(phantomAvatar, effectiveLevel);

            // Book-keeping goes on the human Player (source of truth) � not on
            // this Avatar shell � so `!phantom clear` and tick reattachment
            // still find these entries after hero swaps or region hops.
            Player host = PhantomHost;
            if (host == null)
            {
                error = "no Player host to register phantom against";
                try { if (phantomAvatar.IsInWorld) phantomAvatar.ExitWorld(); phantomAvatar.Destroy(); }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] cleanup after missing host threw: {ex.Message}"); }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }
            if (enemy)
            {
                host.RegisterEnemyPhantom(phantomAvatar.Id, phantomPlayer.Id);
                if (nemesisRank > 0)
                    s_enemyPhantomRankLevel[phantomAvatar.Id] = (nemesisRank, effectiveLevel);
                if (ambush)
                {
                    s_enemyPhantomAmbush.Add(phantomAvatar.Id);
                    s_nemesisSpawnAnchor[phantomAvatar.Id] = spawnPos;
                }
                SchedulePhantomTick();

                // Ultimate opener block � pre-seed the per-phantom ultimate
                // cooldown so the very first thing an enemy phantom does on
                // spawn ISN'T a screen-wide ultimate that one-shots you
                // before you realise they're there. The player gets 15
                // seconds to react and start fighting before an ultimate is
                // possible; the phantom's regular kit still fires normally.
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                s_phantomNextUltimateMs[phantomAvatar.Id] = nowMs + EnemyPhantomUltimateOpenerBlockMs;

                PhantomLogger.Info($"[PhantomHero:Enemy] {this} spawned HOSTILE '{avatarRef.GetName()}' (avatarId 0x{phantomAvatar.Id:X}) at {spawnPos.ToStringNames()} level {effectiveLevel}");
                return phantomAvatar.Id;
            }

            var descriptor = new MHServerEmu.DatabaseAccess.Models.PhantomIntent
            {
                AvatarRef = (ulong)avatarRef,
                Level = effectiveLevel,
                Username = username,
                LockLevel = lockLevel,
                CostumeRef = (ulong)appliedCostumeRef,
                GearRefs = appliedGearRefs,
                Invincible = invincible,
                BypassCap = bypassCap,
            };
            host.RegisterPhantom(phantomAvatar.Id, phantomPlayer.Id, descriptor);
            SchedulePhantomTick();

            PhantomLogger.Info($"[PhantomHero] {this} spawned '{avatarRef.GetName()}' (avatarId 0x{phantomAvatar.Id:X}, phantomPlayerId 0x{phantomPlayer.Id:X}) at {spawnPos.ToStringNames()} level {effectiveLevel}");
            return phantomAvatar.Id;
        }

        // ================================================================
        //  Off-thread entry point used by the WebFrontend HTTP handler.
        //  SpawnPhantomHero touches Game.Current (a thread-static) via
        //  Player.EnterGame ? CheckMapDiscoveryDataExpiration, so calling it
        //  from a ThreadPool thread NREs on the first line. Schedule a
        //  zero-delay event on the game's own scheduler so the actual spawn
        //  runs inside the game tick.
        // ================================================================

        private sealed class WebSpawnEvent : CallMethodEventParam2<Avatar, int, int>
        {
            protected override CallbackDelegate GetCallback() => static (avatar, count, level) =>
            {
                for (int i = 0; i < count; i++)
                    avatar.SpawnPhantomHero(level, null, out _);
            };
        }

        private sealed class WebClearEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => static (avatar) => avatar.DespawnAllPhantomHeroes();
        }

        private readonly EventPointer<WebSpawnEvent> _webSpawnEvent = new();
        private readonly EventPointer<WebClearEvent> _webClearEvent = new();
        private readonly EventGroup _webEvents = new();

        /// <summary>
        /// Thread-safe web-facing spawn. Enqueues one game-thread event that
        /// spawns <paramref name="count"/> phantoms at <paramref name="level"/>.
        /// Returns immediately; poll <see cref="PhantomHeroCount"/> to observe.
        /// </summary>
        public void SpawnPhantomHeroesFromWeb(int count, int level)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webSpawnEvent.IsValid) scheduler.CancelEvent(_webSpawnEvent);
            scheduler.ScheduleEvent(_webSpawnEvent, TimeSpan.Zero, _webEvents);
            _webSpawnEvent.Get().Initialize(this, count, level);
        }

        /// <summary>Thread-safe clear.</summary>
        public void DespawnAllPhantomHeroesFromWeb()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webClearEvent.IsValid) scheduler.CancelEvent(_webClearEvent);
            scheduler.ScheduleEvent(_webClearEvent, TimeSpan.Zero, _webEvents);
            _webClearEvent.Get().Initialize(this);
        }

        /// <summary>Destroys every ENEMY phantom this caller has spawned.</summary>
        public int DespawnAllEnemyPhantoms()
        {
            Player host = PhantomHost;
            if (host == null) return 0;
            var ids = new List<ulong>(host.EnemyPhantomAvatarIds);
            int removed = host.PurgeEnemyPhantoms();
            foreach (ulong id in ids)
            {
                s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); PrunePhantomAiStateFor(id);
                s_phantomNextUltimateMs.Remove(id); s_phantomActivePowerTrack.Remove(id);
                s_enemyDeadSinceMs.Remove(id); s_enemyPhantomRankLevel.Remove(id);
                s_enemyPhantomAmbush.Remove(id); s_nemesisSpawnAnchor.Remove(id); s_nemesisPatrol.Remove(id);
                PruneBlacklistFor(id); PrunePowerBlacklistFor(id);
                s_phantomInvulnerableTrack.Remove(id); s_phantomDeliberatelyInvincible.Remove(id);
            }
            return removed;
        }

        /// <summary>Despawns exactly one enemy phantom by avatar id � same per-id tracking cleanup as DespawnAllEnemyPhantoms, just scoped to one.</summary>
        public bool DespawnOneEnemyPhantom(ulong avatarId)
        {
            Player host = PhantomHost;
            if (host == null) return false;
            bool removed = host.DespawnOneEnemyPhantom(avatarId);
            if (removed)
            {
                s_phantomNextAttackMs.Remove(avatarId); s_phantomStuckTrack.Remove(avatarId); PrunePhantomAiStateFor(avatarId);
                s_phantomNextUltimateMs.Remove(avatarId); s_phantomActivePowerTrack.Remove(avatarId);
                s_enemyDeadSinceMs.Remove(avatarId); s_enemyPhantomRankLevel.Remove(avatarId);
                s_enemyPhantomAmbush.Remove(avatarId); s_nemesisSpawnAnchor.Remove(avatarId); s_nemesisPatrol.Remove(avatarId);
                PruneBlacklistFor(avatarId); PrunePowerBlacklistFor(avatarId);
                s_phantomInvulnerableTrack.Remove(avatarId); s_phantomDeliberatelyInvincible.Remove(avatarId);
            }
            return removed;
        }

        /// <summary>
        /// Rank 4/5 nemesis "escape" � called right after a nemesis kill
        /// registers at rank 4+ (see Avatar.Nemesis.cs). Destroys the
        /// phantom immediately instead of leaving it standing there for an
        /// easy revenge kill once the victim revives; the 2% HP bump for
        /// their next spawn is applied by the caller via EscapeCount.
        /// </summary>
        internal static void EscapeEnemyPhantom(Agent phantom, Player host)
        {
            if (phantom == null || host == null) return;
            ulong id = phantom.Id;

            try
            {
                if (phantom.IsInWorld) phantom.ExitWorld();
                phantom.Destroy();
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Nemesis] escape destroy failed on {id:X}: {ex.Message}"); }

            host.UnregisterEnemyPhantom(id);
            s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); PrunePhantomAiStateFor(id);
            s_phantomNextUltimateMs.Remove(id); s_phantomActivePowerTrack.Remove(id);
            s_enemyDeadSinceMs.Remove(id); s_enemyPhantomRankLevel.Remove(id);
            s_enemyPhantomAmbush.Remove(id); s_nemesisSpawnAnchor.Remove(id); s_nemesisPatrol.Remove(id);
            PruneBlacklistFor(id); PrunePowerBlacklistFor(id);
            s_phantomInvulnerableTrack.Remove(id); s_phantomDeliberatelyInvincible.Remove(id);

            PhantomLogger.Info($"[PhantomHero:Nemesis] {phantom} escaped after killing {host.GetName()}");
        }

        /// <summary>Destroys every phantom hero this caller has spawned.</summary>
        public int DespawnAllPhantomHeroes()
        {
            Player host = PhantomHost;
            if (host == null) return 0;
            // Snapshot ids for the diagnostic-cache scrub � Player.PurgePhantoms
            // clears its own list, so we need the ids before it runs.
            var ids = new List<ulong>(host.PhantomAvatarIds);
            int removed = host.PurgePhantoms();
            foreach (ulong id in ids) { s_phantomAttackLogged.Remove(id); s_phantomLocoLogged.Remove(id); s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); PrunePhantomAiStateFor(id); s_phantomNextDiagMs.Remove(id); s_phantomNextUltimateMs.Remove(id); s_phantomActivePowerTrack.Remove(id); s_phantomDownedSinceMs.Remove(id); s_phantomReattachGraceSinceMs.Remove(id); PruneBlacklistFor(id); PrunePowerBlacklistFor(id); s_phantomInvulnerableTrack.Remove(id); s_phantomDeliberatelyInvincible.Remove(id); }
            return removed;
        }

        /// <summary>
        /// Called from Avatar.OnEnteredWorld. Prunes phantoms that don't
        /// belong to this Avatar's region (they were left behind by an old
        /// Avatar in another region and can't be seen anyway) and restarts
        /// the tick on survivors so their AI resumes. This is the core of
        /// Option B: same-region avatar swaps keep phantoms alive; cross-
        /// region hops clean them up automatically.
        /// </summary>
        internal void ReattachPhantomTick()
        {
            Player host = PhantomHost;
            // Must also check EnemyPhantomCount � if every friendly phantom
            // just died (e.g. a whole team-up squad wiped in the same fight
            // that killed the caller), PhantomHeroCount hits 0 and this used
            // to bail out before ever reaching SchedulePhantomTick() at the
            // bottom, permanently freezing any surviving enemy/rogue
            // phantoms AND stalling the team-up respawn queue drain (both
            // driven by the same tick loop).
            if (host == null || (host.PhantomHeroCount == 0 && host.EnemyPhantomCount == 0 && host.TeamUpRespawnQueueCount == 0)) return;
            Region myRegion = Region;
            if (myRegion == null) return;

            var mgr = Game?.EntityManager;
            if (mgr == null) return;

            var stale = new List<ulong>();
            long nowMsReattach = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            int alive = 0;
            for (int i = 0; i < host.PhantomAvatarIds.Count; i++)
            {
                ulong id = host.PhantomAvatarIds[i];
                // Agent, not Avatar � team-up phantoms are Agent, and
                // GetEntity<Avatar> silently returns null for them, which
                // made every reattach (any world re-entry: region hops,
                // local mission-portal moves, hero swaps) treat the ENTIRE
                // team-up squad as stale and unregister them, regardless of
                // whether they were actually fine.
                Agent phantom = mgr.GetEntity<Agent>(id);
                if (phantom == null || phantom.IsDestroyed) { stale.Add(id); continue; }

                // Different region = unambiguous, can't be driven from here �
                // destroy immediately so the count is honest and !phantom
                // clear stays accurate.
                if (phantom.Region != myRegion) { stale.Add(id); continue; }

                // Same region but momentarily not in world � give it a grace
                // window instead of instant-destroying. A scripted local
                // transition (boss-arena entrance, mission-portal cutscene)
                // can leave a phantom transiently out-of-world for a moment
                // while its own leash/teleport catch-up hasn't landed yet;
                // it's still the same phantom, not actually gone.
                if (phantom.IsInWorld == false)
                {
                    if (s_phantomReattachGraceSinceMs.TryGetValue(id, out long graceSince) == false)
                    {
                        s_phantomReattachGraceSinceMs[id] = nowMsReattach;
                        alive++; // don't prune on the first sighting � give it a chance to catch up
                        continue;
                    }
                    if (nowMsReattach - graceSince < PhantomReattachGraceMs)
                    {
                        alive++;
                        continue;
                    }
                    // Genuinely stuck out-of-world past the grace window.
                    stale.Add(id);
                    continue;
                }

                s_phantomReattachGraceSinceMs.Remove(id);
                alive++;
            }

            if (stale.Count > 0)
            {
                foreach (ulong id in stale)
                {
                    int idx = -1;
                    for (int j = 0; j < host.PhantomAvatarIds.Count; j++)
                        if (host.PhantomAvatarIds[j] == id) { idx = j; break; }
                    ulong playerId = idx >= 0 && idx < host.PhantomPlayerIds.Count ? host.PhantomPlayerIds[idx] : 0;
                    try
                    {
                        Agent av = mgr.GetEntity<Agent>(id);
                        if (av != null)
                        {
                            if (av.IsInWorld) av.ExitWorld();
                            av.Destroy();
                        }
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup avatar 0x{id:X} failed: {ex.Message}"); }
                    if (playerId != 0)
                    {
                        try
                        {
                            Player p = mgr.GetEntity<Player>(playerId);
                            if (p != null) { if (p.IsInGame) p.ExitGame(); p.Destroy(); }
                        }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup phantom-player 0x{playerId:X} failed: {ex.Message}"); }
                    }
                    host.UnregisterPhantom(id);
                    s_phantomAttackLogged.Remove(id);
                    s_phantomLocoLogged.Remove(id);
                    s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); PrunePhantomAiStateFor(id);
                    s_phantomNextDiagMs.Remove(id);
                    s_phantomNextUltimateMs.Remove(id);
                    s_phantomActivePowerTrack.Remove(id);
                    s_phantomDownedSinceMs.Remove(id);
                    s_phantomReattachGraceSinceMs.Remove(id);
                    PruneBlacklistFor(id);
                    PrunePowerBlacklistFor(id);
                    s_phantomInvulnerableTrack.Remove(id);
                    s_phantomDeliberatelyInvincible.Remove(id);
                }
                PhantomLogger.Info($"[PhantomHero] {this} reattach: pruned {stale.Count} stale, {alive} alive");
            }

            if (alive > 0 || host.EnemyPhantomCount > 0)
                SchedulePhantomTick();
        }

        private void DestroyPhantomPlayer(Player p)
        {
            if (p == null) return;
            try { p.Destroy(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] DestroyPhantomPlayer({p.Id:X}) threw: {ex.Message}"); }
        }
    }
}
