using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;

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
            // Phantoms only; 0 = plain hostile (SpawnEnemyPhantomHero), 1-5 =
            // spawn as a ranked nemesis (SpawnNemesisPhantomHero) — same rank
            // ladder as the Enemy Phantoms tool's Unleash panel.
            public int Rank;
        }

        public sealed class WaveDef
        {
            public List<WaveEntryDef> Entries = new();
            // Null = use the run's global intermission (_waveIntermissionMs).
            public int? IntermissionMsOverride;
        }

        public enum WaveRewardMode { None, EveryWave, OnComplete }

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
        private bool _wavePaused;
        private bool _waveLoop;
        private float _waveCountScalePerWave;
        private int _waveLevelBumpPerWave;
        private WaveRewardMode _waveRewardMode = WaveRewardMode.None;
        private PrototypeId _waveRewardLootTableRef = PrototypeId.Invalid;
        private bool _waveHistoryLogged;
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
        /// Snapshot a pending wave-run start onto MigrationData so it
        /// survives a cross-region arena warp, the same way phantom heroes
        /// ride via PhantomIntents (see Player.PhantomHero.cs's
        /// SnapshotPhantomsForTransfer). Confirmed live: a cross-region
        /// transfer destroys the ENTIRE Game instance this WaveDirector
        /// state lives on — the polling loop watching for arrival never
        /// gets the chance to fire again on the new Player object, so
        /// "Start Run" would successfully warp the player but never clear
        /// the arena or spawn wave 1. Only snapshots while still in
        /// WarpingToArena — that's the only state a cross-region transfer
        /// is actually expected mid-run (the very first step, before
        /// anything has spawned); a manual warp away mid-fight just lets
        /// the run drop, same as before this fix. Called from
        /// PlayerConnection.BeginRegionTransfer, right next to the phantom
        /// snapshot call.
        /// </summary>
        internal void SnapshotWaveRunForTransfer()
        {
            if (_waveState != WaveState.WarpingToArena || _waveDefs == null) return;

            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;

            var intent = new WaveRunIntent
            {
                IntermissionMs = (int)_waveIntermissionMs,
                ArenaRegionRef = (ulong)_waveArenaRegionRef,
                ClearArena = _waveClearArena,
                Loop = _waveLoop,
                CountScalePerWave = _waveCountScalePerWave,
                LevelBumpPerWave = _waveLevelBumpPerWave,
                RewardMode = (int)_waveRewardMode,
                RewardLootTableRef = (ulong)_waveRewardLootTableRef,
            };
            foreach (WaveDef wave in _waveDefs)
            {
                var waveIntent = new WaveDefIntent { IntermissionMsOverride = wave.IntermissionMsOverride };
                foreach (WaveEntryDef entry in wave.Entries)
                {
                    waveIntent.Entries.Add(new WaveEntryIntent
                    {
                        AgentRef = entry.AgentRef,
                        HeroRef = entry.HeroRef,
                        IsEnemyPhantom = entry.IsEnemyPhantom,
                        Count = entry.Count,
                        Level = entry.Level,
                        Rank = entry.Rank,
                    });
                }
                intent.Waves.Add(waveIntent);
            }
            mig.PendingWaveRun = intent;

            // This Game instance is going away — clear local state so
            // nothing tries to keep ticking against a Player that's about
            // to be destroyed.
            StopWaveRun(cleanup: false);
            WaveLogger.Info($"[WaveDirector] {GetName()}: snapshotted pending run ({intent.Waves.Count} wave(s)) for cross-region transfer");
        }

        /// <summary>
        /// Read MigrationData.PendingWaveRun (populated by the previous
        /// Game's SnapshotWaveRunForTransfer) and resume the run on this new
        /// Player/Avatar. Called from Avatar.OnEnteredWorld, right next to
        /// RestorePhantomsFromMigration — by the time OnEnteredWorld fires,
        /// the avatar is already standing in the arena region, so
        /// StartWaveRun's "already in the arena" branch takes over
        /// immediately (clears if requested, then spawns wave 1) instead of
        /// re-entering WarpingToArena.
        /// </summary>
        internal void RestoreWaveRunFromMigration(Avatar caller)
        {
            var mig = PlayerConnection?.MigrationData;
            WaveRunIntent intent = mig?.PendingWaveRun;
            if (intent == null || caller == null) return;
            mig.PendingWaveRun = null;

            var waves = new List<WaveDef>();
            foreach (WaveDefIntent waveIntent in intent.Waves)
            {
                var wave = new WaveDef { IntermissionMsOverride = waveIntent.IntermissionMsOverride };
                foreach (WaveEntryIntent entryIntent in waveIntent.Entries)
                {
                    wave.Entries.Add(new WaveEntryDef
                    {
                        AgentRef = entryIntent.AgentRef,
                        HeroRef = entryIntent.HeroRef,
                        IsEnemyPhantom = entryIntent.IsEnemyPhantom,
                        Count = entryIntent.Count,
                        Level = entryIntent.Level,
                        Rank = entryIntent.Rank,
                    });
                }
                waves.Add(wave);
            }
            if (waves.Count == 0) return;

            string result = StartWaveRun(waves, intent.IntermissionMs, intent.ArenaRegionRef, intent.ClearArena,
                intent.Loop, intent.CountScalePerWave, intent.LevelBumpPerWave,
                (WaveRewardMode)intent.RewardMode, intent.RewardLootTableRef, caller);
            WaveLogger.Info($"[WaveDirector] {GetName()}: resumed run after cross-region transfer: {result}");
        }

        /// <summary>
        /// Start (or restart) a wave run. Game thread only. With an arena
        /// region set, the player is warped there first (and the room
        /// optionally cleared of its native hostiles) before wave 1 spawns —
        /// turns any boss room into an empty private battleground.
        /// </summary>
        public string StartWaveRun(List<WaveDef> waves, int intermissionMs, ulong arenaRegionRef = 0, bool clearArena = false,
            bool loop = false, float countScalePerWave = 0f, int levelBumpPerWave = 0,
            WaveRewardMode rewardMode = WaveRewardMode.None, ulong rewardLootTableRef = 0, Avatar avatarOverride = null)
        {
            if (waves == null || waves.Count == 0) return "no waves defined";
            // avatarOverride is used by RestoreWaveRunFromMigration: at that
            // call site (Avatar.OnEnteredWorld, mirroring
            // RestorePhantomsFromMigration's identical need) CurrentAvatar
            // isn't reliably set to the arriving avatar yet, so the caller
            // is passed explicitly instead of relying on it.
            Avatar avatar = avatarOverride ?? CurrentAvatar;
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
            _wavePaused = false;
            _waveLoop = loop;
            _waveCountScalePerWave = Math.Max(0f, countScalePerWave);
            _waveLevelBumpPerWave = levelBumpPerWave;
            _waveRewardMode = rewardMode;
            _waveRewardLootTableRef = (PrototypeId)rewardLootTableRef;
            _waveHistoryLogged = false;

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

            // Log an incomplete-run history entry before we wipe the state
            // below — but only if a run was genuinely in flight (not already
            // Done/logged, e.g. this call is just clearing leftover state).
            bool wasActiveRun = _waveDefs != null && _waveState != WaveState.Idle && _waveState != WaveState.Done;
            if (wasActiveRun && _waveHistoryLogged == false)
                LogWaveRunHistory(completed: false);

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

            if (_wavePaused)
            {
                // Keep the tick armed every second so a subsequent Resume
                // picks back up immediately, but skip everything that would
                // otherwise let the run progress while paused.
                ScheduleWaveTick();
                return;
            }

            var mgr = Game?.EntityManager;
            if (mgr == null)
            {
                // Every other exit path below reschedules before returning
                // (or is the deliberate no-reschedule at Done). This one
                // didn't — confirmed live: EntityManager can read transiently
                // null during the region-transfer/warp window, which killed
                // the self-rescheduling tick permanently and left the run
                // stuck in WarpingToArena forever. The user's own workaround
                // (pressing Start again) "worked" only because StartWaveRun
                // re-arms the tick from scratch, and by then the warp had
                // already landed — i.e. Start Run needing two presses was
                // this bug, not a separate one. Self-heal instead: keep
                // retrying once a second rather than dying silently.
                ScheduleWaveTick();
                return;
            }

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
                        AdvanceAfterWaveCleared();
                        if (_waveState == WaveState.Done)
                            return; // no reschedule — run is over
                    }
                    break;

                case WaveState.Intermission:
                    if (WaveNowMs >= _waveNextSpawnAtMs)
                        SpawnNextWave();
                    break;
            }

            ScheduleWaveTick();
        }

        /// <summary>
        /// Called once the current wave's alive list is empty (naturally, or
        /// via SkipWaveRun). Decides Done-vs-Intermission (honoring Loop and
        /// per-wave intermission overrides) and fires the reward hook.
        /// </summary>
        private void AdvanceAfterWaveCleared()
        {
            if (_waveRewardMode == WaveRewardMode.EveryWave)
                SpawnWaveReward();

            bool isLastWave = _waveIndex + 1 >= _waveDefs.Count;
            if (isLastWave && _waveLoop)
            {
                _waveIndex = -1; // SpawnNextWave's own increment lands back on wave 0
                isLastWave = false;
            }

            if (isLastWave)
            {
                _waveState = WaveState.Done;
                if (_waveRewardMode == WaveRewardMode.OnComplete)
                    SpawnWaveReward();
                if (_waveHistoryLogged == false)
                    LogWaveRunHistory(completed: true);
                WaveLogger.Info($"[WaveDirector] {GetName()}: run complete — {_waveKills} kill(s) over {_waveDefs.Count} wave(s)");
                return;
            }

            int nextIndex = _waveIndex + 1;
            long intermissionMs = _waveDefs[nextIndex].IntermissionMsOverride ?? _waveIntermissionMs;
            _waveState = WaveState.Intermission;
            _waveNextSpawnAtMs = WaveNowMs + intermissionMs;
        }

        /// <summary>Pause or resume the tick's state-machine progress. Game thread only.</summary>
        public string PauseWaveRun(bool paused)
        {
            if (_waveDefs == null || _waveState == WaveState.Idle || _waveState == WaveState.Done)
                return "no wave run active";
            if (_wavePaused == paused)
                return paused ? "already paused" : "already running";
            _wavePaused = paused;
            return paused ? "wave run paused" : "wave run resumed";
        }

        /// <summary>
        /// Force-clear the current wave (Fighting) or jump straight to the
        /// next spawn (Intermission). Game thread only.
        /// </summary>
        public string SkipWaveRun()
        {
            if (_waveDefs == null || _waveState == WaveState.Idle || _waveState == WaveState.Done)
                return "no wave run active";

            switch (_waveState)
            {
                case WaveState.Fighting:
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
                        }
                        catch { /* best effort */ }
                    }
                    _waveAliveIds.Clear();
                    AdvanceAfterWaveCleared();
                    return _waveState == WaveState.Done ? "wave run complete" : "wave skipped";
                }

                case WaveState.Intermission:
                    _waveNextSpawnAtMs = WaveNowMs;
                    return "intermission skipped";

                default:
                    return "can't skip during warp/settle";
            }
        }

        /// <summary>Drop the configured reward loot table at the player's position, if any.</summary>
        private void SpawnWaveReward()
        {
            if (_waveRewardLootTableRef == PrototypeId.Invalid) return;
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            try
            {
                using LootInputSettings inputSettings = ObjectPoolManager.Instance.Get<LootInputSettings>();
                inputSettings.Initialize(LootContext.Drop, this, avatar);
                Game.LootManager.SpawnLootFromTable(_waveRewardLootTableRef, inputSettings, 1);
            }
            catch (Exception ex)
            {
                WaveLogger.Warn($"[WaveDirector] {GetName()}: reward drop failed: {ex.Message}");
            }
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
                // Difficulty scaling: both default to 0, so a run that never
                // opts in behaves identically to before this feature existed.
                int count = Math.Clamp((int)MathF.Round(entry.Count * (1f + _waveCountScalePerWave * _waveIndex)), 1, 30);
                int level = entry.Level;
                if (entry.IsEnemyPhantom && level != 0 && _waveLevelBumpPerWave != 0)
                    level = Math.Clamp(level + _waveLevelBumpPerWave * _waveIndex, 1, 60);

                for (int i = 0; i < count; i++)
                {
                    ulong spawnedId = 0;

                    if (entry.IsEnemyPhantom)
                    {
                        string err;
                        if (entry.Rank > 0)
                        {
                            PrototypeId heroRef = (PrototypeId)entry.HeroRef;
                            string heroName = heroRef != PrototypeId.Invalid ? LeafHeroName(heroRef) : "Phantom";
                            string display = $"★{Math.Clamp(entry.Rank, 1, 5)} {heroName}";
                            spawnedId = avatar.SpawnNemesisPhantomHero(heroRef, level, display, entry.Rank, out err);
                        }
                        else
                        {
                            spawnedId = avatar.SpawnEnemyPhantomHero((PrototypeId)entry.HeroRef, level, out err);
                        }
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

        /// <summary>Leaf hero name for a nemesis display name — "Powers/Player/Thor/Thor.prototype" -> "Thor".</summary>
        private static string LeafHeroName(PrototypeId avatarRef)
        {
            string path = GameDatabase.GetPrototypeName(avatarRef);
            if (string.IsNullOrEmpty(path)) return "Phantom";
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
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
            public bool Paused { get; set; }
            public bool Loop { get; set; }
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
                Paused = _wavePaused,
                Loop = _waveLoop,
            };
        }

        private sealed class WaveTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.WaveTickCallback();
        }
    }
}
