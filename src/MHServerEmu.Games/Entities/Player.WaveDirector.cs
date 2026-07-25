using System;
using System.Collections.Generic;
using System.Linq;
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
using MHServerEmu.Games.UI.Widgets;

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

        // Endless Challenge (2026-07-22) — a thin layer on top of the manual
        // wave engine above: a single WaveDef that loops forever (_waveLoop
        // stays true), with a SEPARATE escalation counter that keeps climbing
        // even though _waveIndex itself resets to 0 every loop. Ends on the
        // real avatar's death (wipe) or an explicit Extract call (safe bail),
        // committing waves-survived to the "EndlessChallenge" Leaderboard kind.
        private bool _isEndlessMode;
        private int _endlessCycle;
        private int _endlessPeakRank;
        private string _endlessHeroName;

        /// <summary>
        /// True while an Endless Challenge run is active. Used by
        /// Avatar.PhantomHero.cs's kill-loot path to suppress per-kill gear
        /// drops from enemy phantoms in this mode — the only reward is the
        /// chest SpawnEndlessChest() drops every EndlessChestEveryNWaves.
        /// </summary>
        internal bool IsEndlessChallengeActive => _isEndlessMode;
        // Every N cleared waves, enemy rank climbs by 1 (capped at 5) — the
        // same rank->HP/damage curves the manual Rank field already uses
        // (NemesisHealthMultForRank/NemesisDmgBoostForRank in Avatar.PhantomHero.cs),
        // just driven by a synthetic "effective rank" instead of a fixed value.
        private const int EndlessRankBumpEveryNWaves = 3;

        // Endless Challenge has its own fixed reward/pacing cadence instead
        // of the manual run's configurable reward mode + intermission slider:
        // 5s between ordinary waves, and every EndlessChestEveryNWaves
        // cleared waves a reward chest spawns with a longer 15s intermission
        // so there's actually time to walk over and loot it.
        private const int EndlessNormalIntermissionMs = 5_000;
        private const int EndlessChestIntermissionMs = 15_000;
        private const int EndlessChestEveryNWaves = 5;
        private const string EndlessChestProtoPath = "Entity/Props/Chests/ShieldCrateBlue.prototype";
        // Every 4th chest (wave 20, 40, 60, ...) is a loot-splosion: far more
        // rolls, and the rarity floor is bumped one band ahead of the smooth
        // curve (see GetEndlessChestAllowedRarities's bumpOneBand param).
        private const int EndlessLootsplosionEveryNWaves = 20;
        private const int EndlessChestBaseRolls = 3;
        private const int EndlessChestRollsPerWaves = 10; // +1 roll every this many waves survived
        private const int EndlessChestMaxRolls = 12;
        private const int EndlessLootsplosionRolls = 20;

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
                IsEndlessMode = _isEndlessMode,
                EndlessCycle = _endlessCycle,
                EndlessPeakRank = _endlessPeakRank,
                EndlessHeroName = _endlessHeroName,
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
            // to be destroyed. Already snapshotted Endless state above, so
            // clear _isEndlessMode BEFORE stopping: StopWaveRun's safety net
            // (for a genuinely external stop) would otherwise treat this
            // transfer-only teardown as a real Extract and bank a bogus
            // "0 waves survived" leaderboard entry.
            _isEndlessMode = false;
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

            // Restore Endless Challenge state AFTER StartWaveRun — it calls
            // StopWaveRun(cleanup: true) internally as its first step, which
            // would misfire the "stopped externally" safety net if these
            // were already set beforehand.
            if (intent.IsEndlessMode)
            {
                _isEndlessMode = true;
                _endlessCycle = intent.EndlessCycle;
                _endlessPeakRank = intent.EndlessPeakRank;
                _endlessHeroName = intent.EndlessHeroName;
            }

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
        /// Start an Endless Challenge run: a single wave entry that repeats
        /// forever, escalating rank (every EndlessRankBumpEveryNWaves clears,
        /// capped at 5) and optionally count/level via the same scaling knobs
        /// manual runs already use. Ends on the real avatar's death (wipe) or
        /// an explicit ExtractEndlessChallenge call (safe bail) — either way,
        /// waves survived commits to the "EndlessChallenge" Leaderboard kind.
        /// </summary>
        public string StartEndlessChallenge(WaveEntryDef baseEntry, int intermissionMs, ulong arenaRegionRef, bool clearArena,
            float countScalePerWave, int levelBumpPerWave, ulong rewardLootTableRef)
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "no avatar in world";
            if (baseEntry == null) return "no wave entry defined";

            _isEndlessMode = true;
            _endlessCycle = 0;
            _endlessPeakRank = baseEntry.Rank;
            _endlessHeroName = GetFriendlyHeroName(avatar);

            var wave = new WaveDef();
            wave.Entries.Add(baseEntry);

            // intermissionMs is ignored — Endless Challenge always runs on
            // its own fixed 5s/15s cadence (see EndlessNormalIntermissionMs).
            // rewardMode is None: the generic per-wave/on-complete reward
            // system doesn't apply here either — rewards come from the
            // chest AdvanceAfterWaveCleared spawns every EndlessChestEveryNWaves
            // (SpawnEndlessChest), still backed by rewardLootTableRef.
            return StartWaveRun(new List<WaveDef> { wave }, EndlessNormalIntermissionMs, arenaRegionRef, clearArena,
                loop: true, countScalePerWave, levelBumpPerWave, WaveRewardMode.None, rewardLootTableRef);
        }

        /// <summary>Bail out of an active Endless Challenge run safely, banking the current wave count to the leaderboard.</summary>
        public string ExtractEndlessChallenge()
        {
            if (_isEndlessMode == false) return "no Endless Challenge run active";
            int wavesSurvived = _endlessCycle;
            EndEndlessChallenge(died: false);
            return $"Extracted — {wavesSurvived} wave(s) survived.";
        }

        /// <summary>Commit the run to the leaderboard and tear it down. died=false is an explicit Extract; died=true is a wipe.</summary>
        private void EndEndlessChallenge(bool died)
        {
            string heroName = _endlessHeroName ?? "Unknown";
            int wavesSurvived = _endlessCycle;
            CommitEndlessChallengeToLeaderboard(heroName, wavesSurvived, completed: !died);
            WaveLogger.Info($"[WaveDirector] {GetName()}: Endless Challenge ended ({(died ? "wiped" : "extracted")}) — {wavesSurvived} wave(s) survived, {_waveKills} kill(s), peak rank {_endlessPeakRank}");
            ClearEndlessWaveWidget(CurrentAvatar);

            // Clear BEFORE StopWaveRun so its own safety-net commit (for a
            // manual Stop Run while endless is active) doesn't double-fire.
            _isEndlessMode = false;
            StopWaveRun(cleanup: true);
        }

        /// <summary>
        /// Sterilize the arena: destroy every non-player entity in the
        /// region so the room starts empty. Anything that could re-spawn
        /// the native encounter — spawner markers, transition consoles,
        /// mission agents, boss triggers — has to go, or the room's own
        /// content fires alongside our waves. Player Avatars and phantoms
        /// (Avatar with IsPhantomHero or PhantomCreatorId set on owner) are
        /// always preserved, and so are active team-ups.
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

                // Never touch team-ups either — they're Agents, not Avatars,
                // so without this check the sweep below (which treats Agents
                // as fair game) destroyed the player's own active team-up the
                // instant an arena warp cleared the room. They come back on
                // their own 45s respawn timer, but that's a bug, not a
                // feature — this stops them from ever being touched.
                if (we is Agent agentCheck && agentCheck.IsTeamUpAgent) continue;

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

            // Safety net: any OTHER path that stops an active Endless run
            // (e.g. the ordinary "Stop Run" button, not ExtractEndlessChallenge)
            // still counts as an extract — better to bank the score than
            // silently lose it. EndEndlessChallenge itself clears
            // _isEndlessMode before calling here, so this never double-fires
            // for a real Extract/death.
            if (_isEndlessMode)
            {
                CommitEndlessChallengeToLeaderboard(_endlessHeroName ?? "Unknown", _endlessCycle, completed: true);
                WaveLogger.Info($"[WaveDirector] {GetName()}: Endless Challenge stopped externally — {_endlessCycle} wave(s) survived (counted as extracted)");
                _isEndlessMode = false;
            }

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

            // Endless Challenge wipe check — only meaningful once the run is
            // actually settled into Fighting/Intermission (avatar state
            // during Warp/Settle is transient and not a real "died" signal).
            if (_isEndlessMode && (_waveState == WaveState.Fighting || _waveState == WaveState.Intermission))
            {
                Avatar endlessAvatar = CurrentAvatar;
                if (endlessAvatar != null && endlessAvatar.IsDead)
                {
                    EndEndlessChallenge(died: true);
                    return; // no reschedule — run is over
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
            // Endless Challenge doesn't use the generic reward system at all
            // (see StartEndlessChallenge) — its reward is the chest spawned
            // below on the milestone check instead.
            if (_isEndlessMode == false && _waveRewardMode == WaveRewardMode.EveryWave)
                SpawnWaveReward();

            bool isLastWave = _waveIndex + 1 >= _waveDefs.Count;
            bool chestWave = false;
            if (isLastWave && _waveLoop)
            {
                _waveIndex = -1; // SpawnNextWave's own increment lands back on wave 0
                isLastWave = false;
                if (_isEndlessMode)
                {
                    _endlessCycle++;
                    chestWave = _endlessCycle % EndlessChestEveryNWaves == 0;
                    if (chestWave)
                        SpawnEndlessChest();
                }
            }

            if (isLastWave)
            {
                _waveState = WaveState.Done;
                if (_isEndlessMode == false && _waveRewardMode == WaveRewardMode.OnComplete)
                    SpawnWaveReward();
                if (_waveHistoryLogged == false)
                    LogWaveRunHistory(completed: true);
                WaveLogger.Info($"[WaveDirector] {GetName()}: run complete — {_waveKills} kill(s) over {_waveDefs.Count} wave(s)");
                return;
            }

            long intermissionMs;
            if (_isEndlessMode)
            {
                intermissionMs = chestWave ? EndlessChestIntermissionMs : EndlessNormalIntermissionMs;
            }
            else
            {
                int nextIndex = _waveIndex + 1;
                intermissionMs = _waveDefs[nextIndex].IntermissionMsOverride ?? _waveIntermissionMs;
            }
            _waveState = WaveState.Intermission;
            _waveNextSpawnAtMs = WaveNowMs + intermissionMs;
        }

        /// <summary>
        /// Endless Challenge's reward hook: spawn a real, interactable reward
        /// chest (the same ShieldCrateBlue prop used for tiered reward crates
        /// elsewhere, e.g. Danger Room) near the player every
        /// EndlessChestEveryNWaves cleared waves, and drop the configured
        /// reward loot table (if any) at the same time via the same
        /// LootManager path SpawnWaveReward already uses — the physical chest
        /// is the visual/flavor payoff, the loot table call is what
        /// guarantees the player actually gets something even if the chest
        /// prototype's own baked-in loot needs region-level wiring we don't
        /// have here.
        /// </summary>
        private void SpawnEndlessChest()
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            try
            {
                PrototypeId chestRef = GameDatabase.GetPrototypeRefByName(EndlessChestProtoPath);
                if (chestRef != PrototypeId.Invalid)
                {
                    var chestProto = chestRef.As<WorldEntityPrototype>();
                    if (chestProto != null)
                    {
                        Vector3 pos;
                        if (EntityHelper.GetSpawnPositionNearAvatar(avatar, avatar.Region, chestProto.Bounds, 250f, out pos) == false)
                            pos = avatar.RegionLocation.Position + avatar.Forward * 150f;

                        using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                        settings.EntityRef = chestRef;
                        settings.Position = pos;
                        settings.Orientation = avatar.RegionLocation.Orientation;
                        settings.RegionId = avatar.Region.Id;
                        Game.EntityManager.CreateEntity(settings);
                    }
                }
                else
                {
                    WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless chest prototype not found ({EndlessChestProtoPath}) — skipping visual chest, loot table still drops");
                }
            }
            catch (Exception ex)
            {
                WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless chest spawn failed: {ex.Message}");
            }

            bool lootsplosion = _endlessCycle % EndlessLootsplosionEveryNWaves == 0;
            List<PrototypeId> allowedRarities = Avatar.GetEndlessChestAllowedRarities(_endlessCycle, bumpOneBand: lootsplosion);

            int rolls = lootsplosion
                ? EndlessLootsplosionRolls
                : Math.Min(EndlessChestMaxRolls, EndlessChestBaseRolls + _endlessCycle / EndlessChestRollsPerWaves);

            SpawnWaveReward(rolls, allowedRarities);
            WaveLogger.Info($"[WaveDirector] {GetName()}: Endless Challenge chest wave — {_endlessCycle} wave(s) survived, " +
                $"{rolls} loot roll(s){(lootsplosion ? " (LOOT-SPLOSION)" : "")}, rarity band: {string.Join(",", allowedRarities.Select(r => r.GetName()))}");
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
        private void SpawnWaveReward() => SpawnWaveReward(1, null);

        /// <summary>
        /// Drop the configured reward loot table <paramref name="rolls"/>
        /// times, optionally restricted to <paramref name="allowedRarities"/>
        /// (empty/null = every rarity, the game's normal weighted spread).
        /// Used by SpawnEndlessChest to scale both quantity and rarity floor
        /// with wave count — see GetEndlessChestAllowedRarities.
        /// </summary>
        private void SpawnWaveReward(int rolls, List<PrototypeId> allowedRarities)
        {
            if (_waveRewardLootTableRef == PrototypeId.Invalid) return;
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            try
            {
                using LootInputSettings inputSettings = ObjectPoolManager.Instance.Get<LootInputSettings>();
                inputSettings.Initialize(LootContext.Drop, this, avatar);

                if (allowedRarities != null && allowedRarities.Count > 0)
                {
                    inputSettings.LootRollSettings.Rarities.Clear();
                    foreach (PrototypeId r in allowedRarities)
                        inputSettings.LootRollSettings.Rarities.Add(r);
                }

                for (int i = 0; i < Math.Max(1, rolls); i++)
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

            // Endless mode drives scaling off _endlessCycle (keeps climbing
            // every loop) instead of _waveIndex (resets to 0 every loop).
            int scaleIndex = _isEndlessMode ? _endlessCycle : _waveIndex;

            foreach (WaveEntryDef entry in wave.Entries)
            {
                // Difficulty scaling: both default to 0, so a run that never
                // opts in behaves identically to before this feature existed.
                int count = Math.Clamp((int)MathF.Round(entry.Count * (1f + _waveCountScalePerWave * scaleIndex)), 1, 30);
                int level = entry.Level;
                if (entry.IsEnemyPhantom && level != 0 && _waveLevelBumpPerWave != 0)
                    level = Math.Clamp(level + _waveLevelBumpPerWave * scaleIndex, 1, 60);

                int rank = entry.Rank;
                if (_isEndlessMode && entry.IsEnemyPhantom)
                {
                    rank = Math.Clamp(entry.Rank + scaleIndex / EndlessRankBumpEveryNWaves, 0, 5);
                    if (rank > _endlessPeakRank)
                    {
                        _endlessPeakRank = rank;
                        // Same chat-broadcast call + locale string real HoloSim
                        // uses for its "Threat Increased" difficulty-up message
                        // (see TuningTable.BroadcastChange) — reused verbatim
                        // here on every genuine rank escalation, not per-wave.
                        Game.ChatManager.SendChatFromGameSystem(GameDatabase.PopulationGlobalsPrototype.MessageEnemiesGrowStronger, this);
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    ulong spawnedId = 0;

                    if (entry.IsEnemyPhantom)
                    {
                        string err;
                        if (rank > 0)
                        {
                            PrototypeId heroRef = (PrototypeId)entry.HeroRef;
                            string heroName = heroRef != PrototypeId.Invalid ? LeafHeroName(heroRef) : "Phantom";
                            string display = $"★{Math.Clamp(rank, 1, 5)} {heroName}";
                            spawnedId = avatar.SpawnNemesisPhantomHero(heroRef, level, display, rank, out err);
                        }
                        else
                        {
                            // ambush: true — same wider 900-1600u spawn ring
                            // SpawnNemesisPhantomHero already uses unconditionally
                            // for ranked phantoms below. Without this, plain
                            // (rank 0) phantoms used the farm-tool's close
                            // 150-320u "spawn with you" radius, which reads as
                            // spawning right on top of the player every wave.
                            spawnedId = avatar.SpawnEnemyPhantomHero((PrototypeId)entry.HeroRef, level, out err, ambush: true);
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

            if (_isEndlessMode)
                UpdateEndlessWaveWidget(avatar);
        }

        // Cached once per process: a UIWidgetGenericFractionPrototype the
        // loaded client data ships. What the fraction NUMBERS show is driven
        // entirely by SetCount()/SetTimeRemaining() below, but each instance
        // also carries its own baked, unrenamable Descriptor label — blindly
        // grabbing "the first one found" picked UI/MetaGame/MindlessTitan
        // .prototype, whose native label is "Defeat Mindless Titan N/M"
        // (confirmed live via /webapi/debug/uiwidgets — misleading even
        // though the fraction itself tracked correctly).
        //
        // TimeRemainingNoText (one of only 5/245 instances with a blank
        // Descriptor) was tried next and confirmed live to render as a
        // completely empty widget — "no text" apparently means "no display
        // at all" for this prototype, not just "no label", so it's unusable.
        //
        // UI/MetaGame/CurrentCountTotalCountOnly.prototype's own native
        // Descriptor is literally just "$CurrentCount$/$TotalCount$" — bare
        // numbers, no label text — confirmed via the same scan to be the
        // only real match for "just show the fraction." The old "take the
        // first one found" logic is kept only as a fallback in case this
        // specific ref doesn't resolve on some client data version.
        private const ulong PreferredGenericFractionWidgetRef = 0xA447BFC6DD2313EB; // UI/MetaGame/CurrentCountTotalCountOnly.prototype

        private static PrototypeId? s_endlessWaveWidgetRef;
        private static bool s_endlessWaveWidgetSearched;

        private static PrototypeId GetEndlessWaveWidgetRef()
        {
            if (s_endlessWaveWidgetSearched) return s_endlessWaveWidgetRef ?? PrototypeId.Invalid;
            s_endlessWaveWidgetSearched = true;

            if (((PrototypeId)PreferredGenericFractionWidgetRef).As<UIWidgetGenericFractionPrototype>() != null)
            {
                s_endlessWaveWidgetRef = (PrototypeId)PreferredGenericFractionWidgetRef;
                return s_endlessWaveWidgetRef.Value;
            }

            foreach (PrototypeId protoRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<UIWidgetGenericFractionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                s_endlessWaveWidgetRef = protoRef;
                break;
            }
            return s_endlessWaveWidgetRef ?? PrototypeId.Invalid;
        }

        /// <summary>
        /// Drive a native HUD fraction widget with the current wave count —
        /// the same widget system HoloSim's wave state uses
        /// (MetaGame.SetUIWidgetGenericFraction), but reached directly:
        /// MetaGame.UIDataProvider is just a passthrough to
        /// Region.UIDataProvider, so no MetaGame instance is actually needed.
        /// Experimental — untested live as of 2026-07-23; if the widget
        /// shows unexpected text/icon from whatever prototype instance
        /// GetEndlessWaveWidgetRef happened to find, that's the tradeoff of
        /// reusing arbitrary existing game data instead of authoring our own.
        /// </summary>
        private void UpdateEndlessWaveWidget(Avatar avatar)
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;

                var widget = avatar.Region?.UIDataProvider?.GetWidget<UIWidgetGenericFraction>(widgetRef);
                widget?.SetCount(_endlessCycle, _endlessCycle + 1);
            }
            catch (Exception ex)
            {
                WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless wave widget update failed: {ex.Message}");
            }
        }

        /// <summary>Tear down the wave-count widget when an Endless Challenge run ends.</summary>
        private void ClearEndlessWaveWidget(Avatar avatar)
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;
                avatar?.Region?.UIDataProvider?.DeleteWidget(widgetRef);
            }
            catch { /* best effort */ }
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

        public sealed class EndlessStatusSnapshot
        {
            public bool Active { get; set; }
            public string State { get; set; }
            public int WavesSurvived { get; set; }
            public int Kills { get; set; }
            public int Alive { get; set; }
            public int PeakRank { get; set; }
            public long RunSeconds { get; set; }
            public long IntermissionRemainingMs { get; set; }
            public bool Paused { get; set; }
        }

        /// <summary>Game-thread status snapshot for the Endless Challenge web endpoint.</summary>
        public EndlessStatusSnapshot GetEndlessStatusForWeb()
        {
            bool active = _isEndlessMode && _waveDefs != null && _waveState != WaveState.Idle && _waveState != WaveState.Done;
            return new EndlessStatusSnapshot
            {
                Active = active,
                State = _isEndlessMode ? _waveState.ToString() : "Idle",
                WavesSurvived = _endlessCycle,
                Kills = _waveKills,
                Alive = _waveAliveIds.Count,
                PeakRank = _endlessPeakRank,
                RunSeconds = active ? (WaveNowMs - _waveRunStartMs) / 1000 : 0,
                IntermissionRemainingMs = active && _waveState == WaveState.Intermission ? Math.Max(0, _waveNextSpawnAtMs - WaveNowMs) : 0,
                Paused = _wavePaused,
            };
        }

        private sealed class WaveTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.WaveTickCallback();
        }
    }
}
