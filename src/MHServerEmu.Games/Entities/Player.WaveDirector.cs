using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Entities
{
    // OmegaDev2 Wave Director — a per-player horde-mode session. Waves are
    // lists of entries (regular mobs by agent ref, or hostile phantom heroes
    // by avatar ref) spawned around the player's avatar. The session ticks
    // once a second on the game thread: when every tracked spawn of the
    // current wave is dead, an intermission runs, then the next wave spawns.
    public partial class Player
    {
        private static readonly Logger WaveLogger = LogManager.CreateLogger();

        public sealed class WaveEntryDef
        {
            public ulong AgentRef;   // regular mob (AgentPrototype)
            public ulong HeroRef;    // OR hostile phantom hero (AvatarPrototype); 0 = random hero
            public bool IsEnemyPhantom;
            public int Count = 1;
            public int Level;        // phantoms only; 0 = match player
        }

        public sealed class WaveDef
        {
            public List<WaveEntryDef> Entries = new();
        }

        private enum WaveState { Idle, WarpingToArena, SettlingArena, Fighting, Intermission, Done }

        private List<WaveDef> _waveDefs;
        private int _waveIndex = -1;
        private WaveState _waveState = WaveState.Idle;
        private readonly List<ulong> _waveAliveIds = new();
        private int _waveKills;
        private int _waveSpawnedTotal;
        private long _waveIntermissionMs = 5000;
        private long _waveNextSpawnAtMs;
        private long _waveRunStartMs;
        private PrototypeId _waveArenaRegionRef = PrototypeId.Invalid;
        private bool _waveClearArena;
        private long _waveWarpDeadlineMs;
        private long _waveSettleUntilMs;
        private const long ArenaWarpTimeoutMs = 90_000; // region gen + client load screen
        // How long to let the region's own population finish spawning before
        // we sweep it. Danger Room / scenario rooms trickle their console
        // triggers + boss markers in over the first couple of seconds after
        // the client's load screen ends; clearing too early misses them and
        // the fight starts on top of your waves.
        private const long ArenaSettleMs = 3_500;

        private readonly EventGroup _waveEvents = new();
        private readonly EventPointer<WaveTickEvent> _waveTick = new();

        private long WaveNowMs => Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Start (or restart) a wave run. Game thread only. With an arena
        /// region set, the player is warped there first (and the room
        /// optionally cleared of its native hostiles) before wave 1 spawns —
        /// turns any boss room into an empty private battleground.
        /// </summary>
        public string StartWaveRun(List<WaveDef> waves, int intermissionMs, ulong arenaRegionRef = 0, bool clearArena = false)
        {
            if (waves == null || waves.Count == 0) return "no waves defined";
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "no avatar in world";

            StopWaveRun(cleanup: true);

            _waveDefs = waves;
            _waveIndex = -1;
            _waveKills = 0;
            _waveSpawnedTotal = 0;
            _waveIntermissionMs = Math.Clamp(intermissionMs, 1000, 60000);
            _waveRunStartMs = WaveNowMs;
            _waveArenaRegionRef = (PrototypeId)arenaRegionRef;
            _waveClearArena = clearArena;

            if (_waveArenaRegionRef != PrototypeId.Invalid &&
                avatar.Region?.PrototypeDataRef != _waveArenaRegionRef)
            {
                _waveState = WaveState.WarpingToArena;
                _waveWarpDeadlineMs = WaveNowMs + ArenaWarpTimeoutMs;
                avatar.TeleportToRegionFromWeb(arenaRegionRef);
                ScheduleWaveTick();
                return $"warping to arena, then {waves.Count} wave(s)";
            }

            // Already in the arena (or no arena requested) — still settle
            // briefly if a clear was requested so late-spawning encounter
            // triggers get swept.
            if (_waveClearArena)
            {
                _waveState = WaveState.SettlingArena;
                _waveSettleUntilMs = WaveNowMs + ArenaSettleMs;
                ScheduleWaveTick();
                return $"clearing arena, then {waves.Count} wave(s)";
            }

            SpawnNextWave();
            ScheduleWaveTick();
            return $"wave run started: {waves.Count} wave(s)";
        }

        /// <summary>
        /// Sterilize the arena: destroy every non-player entity in the
        /// region so the room starts empty. Anything that could re-spawn
        /// the native encounter — spawner markers, transition consoles,
        /// mission agents, boss triggers — has to go, or the room's own
        /// content fires alongside our waves. Player Avatars and phantoms
        /// (Avatar with IsPhantomHero or PhantomCreatorId set on owner)
        /// are always preserved.
        /// </summary>
        private int ClearArena(Avatar avatar)
        {
            Regions.Region region = avatar.Region;
            if (region == null) return 0;

            var aabb = region.Aabb;
            var center = aabb.Center;
            float radius = MathF.Max(aabb.Width, aabb.Length);
            var sphere = new MHServerEmu.Core.Collisions.Sphere(center, radius);
            var ctx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);

            var doomed = new List<WorldEntity>();
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
            {
                if (we == null || we.IsInWorld == false) continue;

                // Never touch avatars: real players or friendly phantoms.
                if (we is Avatar) continue;

                // Never touch our own wave spawns.
                if (_waveAliveIds.Contains(we.Id)) continue;

                // Everything else that walks, spawns, triggers, or gates —
                // Agents (mobs, NPCs, mission actors), Spawners (markers
                // that spew mobs), Transitions (Cable-fight console, portals,
                // waypoints).
                bool doomIt = we is Agent || we is Spawner || we is Transition;
                if (doomIt == false) continue;

                doomed.Add(we);
            }

            int removed = 0;
            foreach (WorldEntity we in doomed)
            {
                try
                {
                    if (we.IsInWorld) we.ExitWorld();
                    we.Destroy();
                    removed++;
                }
                catch { /* best effort */ }
            }

            if (removed > 0)
                WaveLogger.Info($"[WaveDirector] {GetName()}: arena sterilized — {removed} native entity(ies) removed");
            return removed;
        }

        /// <summary>Stop the run; optionally despawn everything still alive.</summary>
        public string StopWaveRun(bool cleanup)
        {
            if (_waveState == WaveState.Idle && _waveDefs == null) return "no wave run active";

            int removed = 0;
            if (cleanup)
            {
                var mgr = Game?.EntityManager;
                foreach (ulong id in _waveAliveIds)
                {
                    try
                    {
                        WorldEntity we = mgr?.GetEntity<WorldEntity>(id);
                        if (we == null || we.IsDestroyed) continue;
                        if (we.IsInWorld) we.ExitWorld();
                        we.Destroy();
                        removed++;
                    }
                    catch { /* best effort */ }
                }
            }

            _waveAliveIds.Clear();
            _waveDefs = null;
            _waveIndex = -1;
            _waveState = WaveState.Idle;

            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _waveTick.IsValid) scheduler.CancelEvent(_waveTick);

            return removed > 0 ? $"wave run stopped, {removed} spawn(s) removed" : "wave run stopped";
        }

        private void ScheduleWaveTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_waveTick.IsValid) return;
            scheduler.ScheduleEvent(_waveTick, TimeSpan.FromSeconds(1), _waveEvents);
            _waveTick.Get().Initialize(this);
        }

        private void WaveTickCallback()
        {
            if (_waveDefs == null || _waveState == WaveState.Idle || _waveState == WaveState.Done)
                return;

            var mgr = Game?.EntityManager;
            if (mgr == null) return;

            // Prune the alive list — anything gone or dead counts as a kill.
            for (int i = _waveAliveIds.Count - 1; i >= 0; i--)
            {
                WorldEntity we = mgr.GetEntity<WorldEntity>(_waveAliveIds[i]);
                if (we == null || we.IsDestroyed || we.IsDead || we.IsInWorld == false)
                {
                    _waveAliveIds.RemoveAt(i);
                    _waveKills++;
                }
            }

            switch (_waveState)
            {
                case WaveState.WarpingToArena:
                {
                    Avatar avatar = CurrentAvatar;
                    if (avatar != null && avatar.IsInWorld &&
                        avatar.Region?.PrototypeDataRef == _waveArenaRegionRef)
                    {
                        // Landed. Move into settle: the region's own
                        // population is still materializing (console
                        // triggers, boss markers) — sterilizing too early
                        // misses them and the encounter starts alongside
                        // our waves. Wait a few seconds, then wipe + fight.
                        _waveState = _waveClearArena ? WaveState.SettlingArena : WaveState.Fighting;
                        _waveSettleUntilMs = WaveNowMs + ArenaSettleMs;
                        if (_waveClearArena == false)
                            SpawnNextWave();
                    }
                    else if (WaveNowMs >= _waveWarpDeadlineMs)
                    {
                        WaveLogger.Warn($"[WaveDirector] {GetName()}: arena warp timed out — run aborted");
                        StopWaveRun(cleanup: false);
                        return;
                    }
                    break;
                }

                case WaveState.SettlingArena:
                {
                    if (WaveNowMs >= _waveSettleUntilMs)
                    {
                        Avatar avatar = CurrentAvatar;
                        if (avatar != null && avatar.IsInWorld)
                        {
                            ClearArena(avatar);
                            // Second sweep on the very next tick catches
                            // anything spawned by a marker mid-clear (the
                            // Cable fight kicks a couple of Popcorn on the
                            // console's trigger).
                            _waveState = WaveState.Fighting;
                            SpawnNextWave();
                        }
                    }
                    break;
                }

                case WaveState.Fighting:
                    if (_waveAliveIds.Count == 0)
                    {
                        if (_waveIndex + 1 >= _waveDefs.Count)
                        {
                            _waveState = WaveState.Done;
                            WaveLogger.Info($"[WaveDirector] {GetName()}: run complete — {_waveKills} kill(s) over {_waveDefs.Count} wave(s)");
                            return; // no reschedule — run is over
                        }
                        _waveState = WaveState.Intermission;
                        _waveNextSpawnAtMs = WaveNowMs + _waveIntermissionMs;
                    }
                    break;

                case WaveState.Intermission:
                    if (WaveNowMs >= _waveNextSpawnAtMs)
                        SpawnNextWave();
                    break;
            }

            ScheduleWaveTick();
        }

        private void SpawnNextWave()
        {
            _waveIndex++;
            if (_waveDefs == null || _waveIndex >= _waveDefs.Count) { _waveState = WaveState.Done; return; }

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false)
            {
                // Player died/warped mid-run — hold in intermission and retry.
                _waveIndex--;
                _waveState = WaveState.Intermission;
                _waveNextSpawnAtMs = WaveNowMs + 2000;
                return;
            }

            var rng = Game.Random;
            WaveDef wave = _waveDefs[_waveIndex];
            int spawned = 0;

            foreach (WaveEntryDef entry in wave.Entries)
            {
                int count = Math.Clamp(entry.Count, 1, 30);
                for (int i = 0; i < count; i++)
                {
                    ulong spawnedId = 0;

                    if (entry.IsEnemyPhantom)
                    {
                        spawnedId = avatar.SpawnEnemyPhantomHero((PrototypeId)entry.HeroRef, entry.Level, out string err);
                        if (spawnedId == 0)
                            WaveLogger.Warn($"[WaveDirector] enemy phantom spawn failed: {err}");
                    }
                    else
                    {
                        var agentProto = ((PrototypeId)entry.AgentRef).As<AgentPrototype>();
                        if (agentProto == null) continue;

                        Vector3 pos;
                        if (EntityHelper.GetSpawnPositionNearAvatar(avatar, avatar.Region, agentProto.Bounds, 400f, out pos) == false)
                        {
                            // Fallback: random ring around the avatar, floor-projected.
                            float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                            float radius = 300f + (float)(rng.NextDouble() * 300f);
                            Vector3 candidate = avatar.RegionLocation.Position + new Vector3(MathF.Cos(ang) * radius, MathF.Sin(ang) * radius, 0f);
                            pos = RegionLocation.ProjectToFloor(avatar.Region, candidate);
                        }

                        Agent agent = EntityHelper.CreateAgent(agentProto, avatar, pos, avatar.RegionLocation.Orientation);
                        spawnedId = agent != null ? agent.Id : 0;
                    }

                    if (spawnedId != 0)
                    {
                        _waveAliveIds.Add(spawnedId);
                        spawned++;
                    }
                }
            }

            _waveSpawnedTotal += spawned;
            _waveState = WaveState.Fighting;
            WaveLogger.Info($"[WaveDirector] {GetName()}: wave {_waveIndex + 1}/{_waveDefs.Count} spawned {spawned} combatant(s)");
        }

        public sealed class WaveStatusSnapshot
        {
            public bool Active { get; set; }
            public string State { get; set; }
            public int Wave { get; set; }
            public int TotalWaves { get; set; }
            public int Alive { get; set; }
            public int Kills { get; set; }
            public int SpawnedTotal { get; set; }
            public long RunSeconds { get; set; }
            public long IntermissionRemainingMs { get; set; }
        }

        /// <summary>Game-thread status snapshot for the web endpoint.</summary>
        public WaveStatusSnapshot GetWaveStatusForWeb()
        {
            bool active = _waveDefs != null && _waveState != WaveState.Idle;
            return new WaveStatusSnapshot
            {
                Active = active && _waveState != WaveState.Done,
                State = _waveState.ToString(),
                Wave = _waveIndex + 1,
                TotalWaves = _waveDefs?.Count ?? 0,
                Alive = _waveAliveIds.Count,
                Kills = _waveKills,
                SpawnedTotal = _waveSpawnedTotal,
                RunSeconds = active ? (WaveNowMs - _waveRunStartMs) / 1000 : 0,
                IntermissionRemainingMs = _waveState == WaveState.Intermission ? Math.Max(0, _waveNextSpawnAtMs - WaveNowMs) : 0,
            };
        }

        private sealed class WaveTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.WaveTickCallback();
        }
    }
}
