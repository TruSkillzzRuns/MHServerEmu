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
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
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

        // 2026-07-26 redesign — no more pre-rolled/queued loot at all. Every
        // earlier attempt at this (spread rolls across ticks, anchor to a
        // fixed position) was still fighting the same root cause: rolling
        // via LootManager.SpawnLootFromTable always drops real ground Item
        // entities, which always pays LootSpawnGrid's per-call region scan
        // (LootSpawnGrid.cs:45) and always risks landing loot at the
        // avatar's live, moving position. The player explicitly asked for
        // loot to ONLY ever come from opening a chest — so now nothing rolls
        // until a chest is actually interacted with, and even then it uses
        // LootManager.GiveLootFromTable (straight to inventory, no ground
        // entity, no LootSpawnGrid call at all) instead of SpawnLootFromTable.
        // See SpawnOneEndlessChest / OnEndlessChestInteract.
        private readonly Dictionary<ulong, (int rolls, List<PrototypeId> rarities)> _pendingChestLoot = new();
        private Region _endlessChestRegion;
        private Event<PlayerInteractGameEvent>.Action _endlessChestInteractAction;

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
        private int _endlessLastBossWaveSpawned = -1; // scaleIndex a boss was already spawned for — guards against double-spawning if SpawnNextWave ever re-runs the same wave

        // Random hazard events — an extra ambush phantom that can drop in
        // mid-wave, independent of the wave's own spawn count. Ticks on a
        // random interval (not tied to wave clears) and rolls a chance each
        // time so it's genuinely occasional, not "every wave" — same shape
        // as Player.TrialOfImpossible.cs's finale hazard tick, generalized
        // to run for the WHOLE Endless Challenge instead of only its finale.
        // 2026-07-26: was 20-45s delay @ 35% chance — averages out to roughly
        // one hazard burst every 60-130s, which in a short test session
        // reads as "only fired once." Player explicitly asked for hazards to
        // fire constantly throughout the whole wave, not just occasionally —
        // shortened the delay and raised the chance so bursts land every
        // ~6-12s instead.
        private const int EndlessHazardMinDelayMs = 6_000;
        private const int EndlessHazardMaxDelayMs = 12_000;
        private const double EndlessHazardChance = 0.85; // rolled each time the tick fires — occasionally skips so it's not perfectly metronomic
        private readonly EventPointer<EndlessHazardTickEvent> _endlessHazardTick = new();

        // Periodic REAL boss — every EndlessBossEveryNWaves cleared waves,
        // one genuine boss-tier enemy (not a hero-phantom) spawns ON TOP of
        // the normal wave. Pool is EVERY real boss the game ships — same
        // /Bosses/ path + Brain-required filter EnemyCatalogWebHandler.cs
        // uses to classify its "Boss" category — built lazily once and
        // cached, not a hardcoded handful. These are purpose-built enemy
        // content, not reused player-hero prototypes, so no phantom
        // pipeline or manual stat buff is needed; they carry their own
        // native hostile alliance/stats already.
        private const int EndlessBossEveryNWaves = 7;

        // Loot break — every EndlessLootBreakEveryNWaves cleared waves,
        // pause and let the player bank loot at a stash box before
        // continuing (Danger Room Endless Terminal-specific; no-op
        // elsewhere). See TriggerEndlessLootBreak in
        // Player.DangerRoomEndlessTerminal.cs.
        private const int EndlessLootBreakEveryNWaves = 10;

        /// <summary>True while the wave run is paused (manual pause OR a loot break) — used to tell "not started" apart from "paused mid-run" at the terminal.</summary>
        public bool IsWaveRunPaused => _wavePaused;

        // AI think-rate tuning for every SpawnCuratedBoss spawn (2026-07-26)
        // — per-instance only, see the comment at the Properties assignment
        // in SpawnCuratedBoss for why this can't leak into original content.
        // Native default is 100ms with a target / 500ms without one; this
        // tightens it so bosses react/reconsider noticeably faster.
        private const int CuratedBossThinkRateMs = 50;

        private static List<PrototypeId> s_endlessBossPool;
        private static readonly object s_endlessBossPoolLock = new();

        /// <summary>
        /// The curated real-boss pool (CuratedBossRoster-matched, one entry
        /// per villain) — shared beyond just Endless Wave: also used by
        /// Avatar.Nemesis.cs (recognize a real boss as a valid nemesis
        /// killer) and Player.RogueEncounter.cs (boss nemesis revenge spawns
        /// + real-boss ambush slots).
        /// </summary>
        internal static bool IsCuratedBossRef(PrototypeId protoRef) => GetEndlessBossPool().Contains(protoRef);

        internal static List<PrototypeId> GetEndlessBossPool()
        {
            if (s_endlessBossPool != null) return s_endlessBossPool;
            lock (s_endlessBossPoolLock)
            {
                if (s_endlessBossPool != null) return s_endlessBossPool;

                var pool = new List<PrototypeId>(256);
                foreach (PrototypeId agentRef in DataDirectory.Instance.IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (agentRef == PrototypeId.Invalid) continue;
                    var proto = agentRef.As<AgentPrototype>();
                    if (proto == null || proto is AvatarPrototype) continue;

                    AssetId iconAssetId = proto.IconPathHiRes != 0 ? proto.IconPathHiRes : proto.IconPath;
                    if (iconAssetId == 0) continue;

                    string path = GameDatabase.GetPrototypeName(agentRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("/Bosses/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (path.IndexOf("/Test/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Debug/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/zzzDeprecated", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Cinematic", StringComparison.OrdinalIgnoreCase) >= 0
                        // Raid-exclusive bosses excluded per user request 2026-07-26.
                        || path.IndexOf("/SurturRaid/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/OnslaughtRaid/", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (proto.BehaviorProfile == null || proto.BehaviorProfile.Brain == PrototypeId.Invalid) continue;

                    pool.Add(agentRef);
                }

                // Curated whitelist (itembase.mhbugle.com villains list minus
                // raids/dummies, 2026-07-26) — one entry per recognizable
                // named villain instead of every chapter/difficulty variant.
                // See CuratedBossRoster.cs.
                pool = CuratedBossRoster.SelectCanonical(pool, LeafHeroName);

                s_endlessBossPool = pool;
                WaveLogger.Info($"[WaveDirector] Endless boss pool built: {pool.Count} boss(es)");
                return pool;
            }
        }

        /// <summary>
        /// Spawn a real curated boss AgentPrototype as a plain hostile Agent
        /// near the avatar — the pipeline SpawnNemesisPhantomHero can't use
        /// (it hard-requires an AvatarPrototype; a boss ref fails immediately
        /// with "not an AvatarPrototype"). Shared by the Endless Wave
        /// periodic boss, Rogue Encounter boss ambushes/revenge spawns, and
        /// the "Fight Now" nemesis trigger. extraHealthMult/extraDamageMult
        /// let a boss NEMESIS entry's Rank still matter even though bosses
        /// don't go through the phantom rank-buff pipeline — applied on top
        /// of whatever the boss's own native stats already are.
        /// </summary>
        public ulong SpawnCuratedBoss(Avatar avatar, PrototypeId bossRef, out string error, float extraHealthMult = 1f, float extraDamageMult = 1f)
        {
            error = null;
            var bossProto = bossRef.As<AgentPrototype>();
            if (bossProto == null) { error = "bossRef did not resolve to an AgentPrototype"; return 0; }

            if (EntityHelper.GetSpawnPositionNearAvatar(avatar, avatar.Region, bossProto.Bounds, 250f, out Vector3 position) == false)
            {
                error = "no space found to spawn the entity";
                return 0;
            }

            Orientation orientation = Orientation.FromDeltaVector(avatar.RegionLocation.Position - position);
            Agent agent = EntityHelper.CreateAgent(bossProto, avatar, position, orientation);
            if (agent == null) { error = "CreateAgent returned null"; return 0; }

            // Confirmed live 2026-07-26 via code trace — any AgentPrototype
            // with a nonzero WakeRange (Agent.cs:126) spawns Dormant and
            // only wakes once a player closes to within that native range
            // (BehaviorSensorySystem.cs's UpdateAvatarSensory). That's
            // correct for a real population encounter the player walks up
            // to, but our spawn drops bosses 250-400u from the player
            // directly — if the boss's own WakeRange is smaller than that,
            // it just sits there until the player closes the remaining gap.
            // Force-clear it per-instance so a directly-spawned wave boss
            // engages immediately; real population-triggered spawns
            // elsewhere in the game are untouched.
            agent.Properties[PropertyEnum.Dormant] = false;

            // A curated boss keeps its own native AgentPrototype alliance by
            // default — confirmed live 2026-07-26 that this can be (and was)
            // genuinely mutually hostile with the phantom-hero enemy
            // alliance per the real game's own alliance table, so phantoms
            // and the boss fought each other instead of both only fighting
            // the player. Override to the exact same alliance phantom heroes
            // use — alliances are auto-friendly to themselves, so this makes
            // all enemy categories mutually friendly while staying hostile
            // to the player. Per-instance Properties write only, no
            // prototype/global alliance data touched.
            PrototypeId enemyAllianceRef = Avatars.Avatar.GetEnemyPhantomAllianceRef();
            if (enemyAllianceRef != PrototypeId.Invalid)
                agent.Properties[PropertyEnum.AllianceOverride] = enemyAllianceRef;

            if (extraHealthMult != 1f) agent.Properties[PropertyEnum.HealthMaxMult] = (float)agent.Properties[PropertyEnum.HealthMaxMult] * extraHealthMult;
            if (extraDamageMult != 1f) agent.Properties[PropertyEnum.DamageMult] = (float)agent.Properties[PropertyEnum.DamageMult] * extraDamageMult;

            // Real bosses spawned via the game's own population/spawner
            // system get one of LootCooldownByChannel/LootCooldownTimeHours/
            // LootCooldownRolloverWallTime set automatically — that's what
            // ItemResolverContext.SetInternal (Loot\ItemResolverContext.cs:
            // 369-394) uses to resolve a valid CooldownData.OriginProtoRef
            // for any cooldown-gated loot table entry (common for unique
            // boss drops). A bare EntityHelper.CreateAgent spawn has none of
            // these, so OriginProtoRef stays PrototypeId.Invalid and
            // GetDropChance's Verify check (line 65) fails and silently
            // returns 0 (no drop) for those entries — confirmed live
            // 2026-07-26 via repeated "Verify failed" log spam right after
            // a curated boss spawn. LootCooldownTimeHours=0 is enough to
            // make SetInternal resolve OriginProtoRef to this boss's own
            // PrototypeDataRef (always valid) without imposing any real
            // farm-cooldown restriction.
            if (agent.Properties.HasProperty(PropertyEnum.LootCooldownByChannel) == false
                && agent.Properties.HasProperty(PropertyEnum.LootCooldownTimeHours) == false
                && agent.Properties.HasProperty(PropertyEnum.LootCooldownRolloverWallTime) == false)
            {
                agent.Properties[PropertyEnum.LootCooldownTimeHours] = 0;
            }

            // Faster AI reaction time — per-instance only (Properties on
            // THIS spawned entity), zero effect on any other spawn of the
            // same boss prototype elsewhere in the game (story campaign,
            // terminals, endgame use the native population/mission spawner,
            // a completely separate code path). Default think interval is
            // 100ms with a target / 500ms without one (AIController.cs) —
            // this tightens the decision loop so the boss reconsiders/reacts
            // noticeably faster without touching its actual attack-pacing
            // or power-choice data (that's baked into the profile prototype
            // itself and would be a shared, global change — not done here).
            agent.Properties[PropertyEnum.AICustomThinkRateMS] = CuratedBossThinkRateMs;

            // Real bosses are plain Agents — their on-death loot comes from
            // WorldEntity.AwardKillLoot (WorldEntity.cs:4027), a completely
            // separate path from the phantom-hero kill-loot gate below this
            // method (IsEndlessChallengeActive). That gate has no effect on
            // a plain Agent, so without this, a curated boss killed during
            // an Endless run would still drop its own native loot table on
            // top of the chest reward. NoLootDrop is per-instance-only
            // (Properties on THIS spawned agent) — same pattern Agent.cs
            // already uses for resurrected/controlled agents — so it never
            // touches the same boss prototype spawned by the real
            // population/mission spawner elsewhere.
            if (_isEndlessMode)
                agent.Properties[PropertyEnum.NoLootDrop] = true;

            return agent.Id;
        }

        /// <summary>
        /// True while an Endless Challenge run is active. Used by
        /// Avatar.PhantomHero.cs's kill-loot path to suppress per-kill gear
        /// drops from enemy phantoms in this mode — the only reward is the
        /// tiered chests SpawnEndlessChests() drops (see EndlessChestTier1/2/3Waves).
        /// </summary>
        internal bool IsEndlessChallengeActive => _isEndlessMode;
        // Every N cleared waves, enemy rank climbs by 1 (capped at 5) — the
        // same rank->HP/damage curves the manual Rank field already uses
        // (NemesisHealthMultForRank/NemesisDmgBoostForRank in Avatar.PhantomHero.cs),
        // just driven by a synthetic "effective rank" instead of a fixed value.
        private const int EndlessRankBumpEveryNWaves = 3;

        // Endless Challenge has its own fixed reward/pacing cadence instead
        // of the manual run's configurable reward mode + intermission slider:
        // 5s between ordinary waves, and on any chest-tier milestone (every
        // 5th/10th/20th wave) reward chest(s) spawn with a longer 15s
        // intermission so there's actually time to walk over and loot them.
        private const int EndlessNormalIntermissionMs = 5_000;
        private const int EndlessChestIntermissionMs = 15_000;
        // 2026-07-26 redesign — every kill-based loot drop (native
        // AwardKillLoot/AwardHitLoot, phantom gear drops) is now fully
        // suppressed during an Endless run (see NoLootDrop usages across
        // SpawnCuratedBoss/wave-mob spawn/SpawnPhantomHeroCore, plus the
        // AwardHitLoot NoLootDrop check in WorldEntity.cs) — these chests are
        // the ONLY loot source left. Tiered by wave milestone instead of a
        // single flat cadence: every 5th wave = 1 chest, every 10th = 2
        // chests, every 20th = 3 chests (each tier's condition is checked in
        // priority order so a wave that's a multiple of 20 gets exactly 3,
        // not 1+2+3).
        private const int EndlessChestTier1Waves = 5;  // 1 chest
        private const int EndlessChestTier2Waves = 10; // 2 chests
        private const int EndlessChestTier3Waves = 20; // 3 chests
        private const string EndlessChestProtoPath = "Entity/Props/Chests/DangerRoomChestTutorialRewardEntity.prototype";
        // On a tier-3 (every 20 waves) milestone, one of the 3 chests has a
        // CHANCE (not guaranteed) of being a loot-splosion: far more rolls,
        // rarity floor bumped one band ahead of the smooth curve (see
        // GetEndlessChestAllowedRarities's bumpOneBand param).
        private const double EndlessLootsplosionChance = 0.5;
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
            _endlessLastBossWaveSpawned = -1;

            var wave = new WaveDef();
            wave.Entries.Add(baseEntry);

            // intermissionMs is ignored — Endless Challenge always runs on
            // its own fixed 5s/15s cadence (see EndlessNormalIntermissionMs).
            // rewardMode is None: the generic per-wave/on-complete reward
            // system doesn't apply here either — rewards come from the
            // tiered chest(s) AdvanceAfterWaveCleared spawns on milestone
            // waves (SpawnEndlessChests), still backed by rewardLootTableRef.
            string result = StartWaveRun(new List<WaveDef> { wave }, EndlessNormalIntermissionMs, arenaRegionRef, clearArena,
                loop: true, countScalePerWave, levelBumpPerWave, WaveRewardMode.None, rewardLootTableRef);

            ScheduleEndlessHazardTick();
            return result;
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
            CancelEndlessHazardTick();

            // Clear BEFORE StopWaveRun so its own safety-net commit (for a
            // manual Stop Run while endless is active) doesn't double-fire.
            _isEndlessMode = false;
            StopWaveRun(cleanup: true);

            // Bring the Danger Room Endless Terminal back so another run can
            // be started — see Player.DangerRoomEndlessTerminal.cs (same
            // partial class). No-op if the player isn't in that arena.
            try { RespawnDrTerminalAfterEndlessChallenge(); }
            catch (Exception ex) { WaveLogger.Warn($"[WaveDirector] {GetName()}: RespawnDrTerminalAfterEndlessChallenge threw: {ex.Message}"); }
        }

        // ---------------- Endless Challenge: random hazard events ----------------

        private void ScheduleEndlessHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null || _isEndlessMode == false) return;
            if (_endlessHazardTick.IsValid) return;
            int delayMs = Game.Random.Next(EndlessHazardMinDelayMs, EndlessHazardMaxDelayMs);
            scheduler.ScheduleEvent(_endlessHazardTick, TimeSpan.FromMilliseconds(delayMs), _waveEvents);
            _endlessHazardTick.Get().Initialize(this);
        }

        private void CancelEndlessHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _endlessHazardTick.IsValid) scheduler.CancelEvent(_endlessHazardTick);
        }

        /// <summary>Rolls a chance each time it fires — most ticks do nothing, so hazards land at irregular, unpredictable moments rather than every wave.</summary>
        // Real Danger Room ground-effect hazards — the actual tutorial
        // teaches players to dodge these (fire/ice/poison patches), NOT
        // extra enemies. Confirmed live 2026-07-26 via the game's own data:
        // these are HotspotPrototype entities under Powers/
        // DangerRoomModifierPowers/HazardPowers/ — a standalone area effect,
        // no owning caster needed (same EntitySettings+Lifespan pattern
        // Player.TrialOfImpossible.cs's SpawnTrialOrb already uses for a
        // casterless entity). The original mechanic here mistakenly spawned
        // another ambush phantom and just labeled it "hazard" — replaced.
        // Full HazardPowers/ folder (protoeditor discover confirmed live
        // 2026-07-26) — was only using 3 of the 7 real standalone hazard
        // hotspots the game actually has. Excludes the two "SummonArea"
        // variants (SawBladeTrapSummonArea/FlamethrowerTrapSummonArea) —
        // those are the trigger zone that summons the trap entity in the
        // native tutorial's own spawner setup, not a standalone
        // spawnable hazard themselves.
        private static readonly ulong[] s_endlessHazardHotspots =
        {
            0x43CDB6C3569C1E35, // FirePatchesHotspot
            0x05BFE7F9378C1DC0, // IcePatchesHotspot
            0xA3BB784678F91EC9, // PoisonCloudsHotspot
            0x19BABF90FF691D06, // SpikeTrapEntity
            0x82247BCF56281E2D, // SawBladeTrapEntity
            0xDF8D92D4D5271FFA, // FlamethrowerTrapEntity
            0xF55E590271D21ADC, // MineEntity
        };
        private const int EndlessHazardLifespanSec = 10;
        // Each hazard tick now drops several hazards at once, scattered
        // across the whole arena floor instead of one at a time clustered
        // right next to the avatar — confirmed live 2026-07-26 the player
        // wanted hazards spread "throughout the whole region," not a single
        // spot.
        private const int EndlessHazardMinCount = 2;
        private const int EndlessHazardMaxCount = 4;
        // How far ChoosePositionAtOrNearPoint may nudge a scattered point to
        // find a valid navmesh position nearby.
        private const float EndlessHazardPlacementSlack = 300f;
        // Shrink the region's bounding box inward by this fraction on each
        // side so scattered points don't land flush against outer walls.
        private const float EndlessHazardBoundsInset = 0.15f;

        private void OnEndlessHazardTick()
        {
            try
            {
                if (_isEndlessMode == false) return; // run ended between scheduling and firing

                if (Game.Random.NextDouble() < EndlessHazardChance)
                {
                    Avatar avatar = CurrentAvatar;
                    if (avatar != null && avatar.IsInWorld)
                    {
                        var region = avatar.Region;
                        var rng = Game.Random;

                        var regionAabb = region.Aabb;
                        float insetX = regionAabb.Width * EndlessHazardBoundsInset;
                        float insetY = regionAabb.Length * EndlessHazardBoundsInset;
                        float minX = regionAabb.Min.X + insetX, maxX = regionAabb.Max.X - insetX;
                        float minY = regionAabb.Min.Y + insetY, maxY = regionAabb.Max.Y - insetY;

                        int spawnCount = rng.Next(EndlessHazardMinCount, EndlessHazardMaxCount + 1);
                        var spawnedNames = new List<string>();

                        // TEMP PERF INSTRUMENTATION (2026-07-26) — this does
                        // up to EndlessHazardMaxCount navmesh position
                        // queries (ChoosePositionAtOrNearPoint) back-to-back
                        // in one tick, another candidate for the still-
                        // reported stutter that wasn't covered by the
                        // loot-grid fix — measuring instead of assuming.
                        var hazardPerfSw = System.Diagnostics.Stopwatch.StartNew();

                        for (int i = 0; i < spawnCount; i++)
                        {
                            ulong hotspotRef = s_endlessHazardHotspots[rng.Next(s_endlessHazardHotspots.Length)];
                            var hotspotProto = ((PrototypeId)hotspotRef).As<WorldEntityPrototype>();
                            if (hotspotProto == null) continue;

                            float x = minX + (float)(rng.NextDouble() * Math.Max(0f, maxX - minX));
                            float y = minY + (float)(rng.NextDouble() * Math.Max(0f, maxY - minY));
                            Vector3 candidateCenter = new(x, y, avatar.RegionLocation.Position.Z);

                            MHServerEmu.Games.Entities.Bounds entityBounds = new();
                            entityBounds.InitializeFromPrototype(hotspotProto.Bounds);
                            entityBounds.Center = candidateCenter;

                            if (region.ChoosePositionAtOrNearPoint(ref entityBounds, avatar.Locomotor.PathFlags,
                                MHServerEmu.Games.Regions.PositionCheckFlags.CanBeBlockedEntity, BlockingCheckFlags.None,
                                EndlessHazardPlacementSlack, out Vector3 pos, maxPositionTests: 32) == false)
                            {
                                continue;
                            }

                            pos = RegionLocation.ProjectToFloor(region, pos);

                            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                            settings.EntityRef = (PrototypeId)hotspotRef;
                            settings.Position = pos;
                            settings.Orientation = Orientation.Zero;
                            settings.RegionId = region.Id;
                            settings.Lifespan = TimeSpan.FromSeconds(EndlessHazardLifespanSec);

                            WorldEntity hazard = Game.EntityManager.CreateEntity(settings) as WorldEntity;
                            if (hazard != null)
                            {
                                string name = LeafHeroName((PrototypeId)hotspotRef).Replace("Hotspot", "").Replace("Entity", "");
                                spawnedNames.Add(name);
                                WaveLogger.Info($"[WaveDirector] {GetName()}: Endless hazard spawned ({name})");
                            }
                            else
                            {
                                WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless hazard spawn failed (CreateEntity returned null)");
                            }
                        }

                        hazardPerfSw.Stop();
                        WaveLogger.Info($"[WaveDirector:Perf] OnEndlessHazardTick spawnCount={spawnCount} spawned={spawnedNames.Count} took {hazardPerfSw.Elapsed.TotalMilliseconds:F1}ms");

                        if (spawnedNames.Count > 0)
                        {
                            try { SendBannerLines($"⚠ HAZARDS — {string.Join(", ", spawnedNames)} across the room, move!"); } catch { }
                        }
                    }
                }
            }
            finally
            {
                ScheduleEndlessHazardTick();
            }
        }

        private sealed class EndlessHazardTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnEndlessHazardTick();
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
                CancelEndlessHazardTick();
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

                // Some curated bosses use a native AIDefeatedAtHealthPct
                // "scripted defeat" mechanic — confirmed live 2026-07-26 via
                // code trace (Malekith stayed up as a clickable NPC after
                // "dying") that once AIDefeated is set, WorldEntity.
                // AdjustHealth's Kill() gate (health<=0 && AIDefeated==false)
                // can NEVER fire again, so the entity never actually dies,
                // never gets destroyed, and sits there fully interactable.
                // That mechanic exists for a mission-scripted cutscene beat
                // we don't have — treat AIDefeated as "this boss is done"
                // ourselves and force it out, since nothing native ever will.
                if (we != null && we.IsDestroyed == false && we.IsDead == false && we.IsInWorld
                    && we.Properties[PropertyEnum.AIDefeated])
                {
                    try
                    {
                        we.ExitWorld();
                        we.Destroy();
                        WaveLogger.Info($"[WaveDirector] {GetName()}: force-removed AIDefeated boss {we} (native death path never fires for this mechanic)");
                    }
                    catch (Exception ex) { WaveLogger.Warn($"[WaveDirector] {GetName()}: AIDefeated cleanup failed: {ex.Message}"); }
                }

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

                    int chestCount;
                    if (_endlessCycle % EndlessChestTier3Waves == 0) chestCount = 3;
                    else if (_endlessCycle % EndlessChestTier2Waves == 0) chestCount = 2;
                    else if (_endlessCycle % EndlessChestTier1Waves == 0) chestCount = 1;
                    else chestCount = 0;

                    chestWave = chestCount > 0;
                    if (chestWave)
                        SpawnEndlessChests(chestCount);

                    // Loot break — every EndlessLootBreakEveryNWaves cleared
                    // waves, pause the run and let Player.
                    // DangerRoomEndlessTerminal.cs respawn the terminal +
                    // stash box so the player can safely bank loot before
                    // continuing. No-op outside that arena (region-gated
                    // inside TriggerEndlessLootBreak, same pattern
                    // RespawnDrTerminalAfterEndlessChallenge already uses).
                    if (_endlessCycle % EndlessLootBreakEveryNWaves == 0)
                    {
                        try { TriggerEndlessLootBreak(); }
                        catch (Exception ex) { WaveLogger.Warn($"[WaveDirector] {GetName()}: TriggerEndlessLootBreak threw: {ex.Message}"); }
                    }
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
        /// Endless Challenge's reward hook: spawn <paramref name="count"/>
        /// (1-3, per the tier check in AdvanceAfterWaveCleared) real,
        /// interactable reward chests near the player. This is now the ONLY
        /// loot source during an Endless run — every native kill-based drop
        /// (AwardKillLoot/AwardHitLoot, phantom gear drops) is suppressed via
        /// NoLootDrop elsewhere, and (2026-07-26 redesign) loot no longer
        /// pre-rolls/queues at all — nothing is rolled/spawned until the
        /// player actually walks up and interacts with a specific chest (see
        /// OnEndlessChestInteract), which then spawns the loot on the ground
        /// AT the chest — same as any other lootable chest in the game. This
        /// also fixes the stutter at its root: the ground-drop position is
        /// now always the chest's own static spot, never the avatar's live
        /// moving position, and the roll only ever happens once, at a
        /// deliberate stationary interaction moment — not repeatedly during
        /// active combat. On a tier-3 (every 20 waves) milestone, one of the
        /// 3 chests has a chance to be a loot-splosion instead of a normal
        /// chest.
        /// </summary>
        private void SpawnEndlessChests(int count)
        {
            bool tier3 = _endlessCycle % EndlessChestTier3Waves == 0;
            int lootsplosionIndex = tier3 && Game.Random.NextDouble() < EndlessLootsplosionChance
                ? Game.Random.Next(count)
                : -1;

            for (int i = 0; i < count; i++)
                SpawnOneEndlessChest(i, count, isLootsplosion: i == lootsplosionIndex);
        }

        private void SpawnOneEndlessChest(int index, int count, bool isLootsplosion)
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            List<PrototypeId> allowedRarities = Avatar.GetEndlessChestAllowedRarities(_endlessCycle, bumpOneBand: isLootsplosion);
            int rolls = isLootsplosion
                ? EndlessLootsplosionRolls
                : Math.Min(EndlessChestMaxRolls, EndlessChestBaseRolls + _endlessCycle / EndlessChestRollsPerWaves);

            try
            {
                PrototypeId chestRef = GameDatabase.GetPrototypeRefByName(EndlessChestProtoPath);
                if (chestRef == PrototypeId.Invalid)
                {
                    WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless chest prototype not found ({EndlessChestProtoPath}) — no chest, no loot this milestone");
                    return;
                }

                var chestProto = chestRef.As<WorldEntityPrototype>();
                if (chestProto == null) return;

                // Spread multiple simultaneous chests 120° apart around the
                // player instead of stacking on top of each other (max is 3,
                // at tier-3 milestones).
                float angle = index * (MathF.PI * 2f / 3f);
                Vector3 fallback = avatar.RegionLocation.Position + avatar.Forward * 150f
                    + new Vector3(MathF.Cos(angle) * 80f, MathF.Sin(angle) * 80f, 0f);

                Vector3 pos;
                if (EntityHelper.GetSpawnPositionNearAvatar(avatar, avatar.Region, chestProto.Bounds, 250f, out pos) == false)
                    pos = fallback;

                using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                settings.EntityRef = chestRef;
                settings.Position = pos;
                settings.Orientation = avatar.RegionLocation.Orientation;
                settings.RegionId = avatar.Region.Id;

                WorldEntity chest = Game.EntityManager.CreateEntity(settings) as WorldEntity;
                if (chest == null)
                {
                    WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless chest CreateEntity failed — no chest, no loot this milestone");
                    return;
                }

                chest.Properties[PropertyEnum.Interactable] = true;
                _pendingChestLoot[chest.Id] = (rolls, allowedRarities);

                Region region = avatar.Region;
                if (_endlessChestRegion != region)
                {
                    if (_endlessChestRegion != null && _endlessChestInteractAction != null)
                        _endlessChestRegion.PlayerInteractEvent.RemoveAction(_endlessChestInteractAction);
                    _endlessChestRegion = region;
                    _endlessChestInteractAction ??= OnEndlessChestInteract;
                    region.PlayerInteractEvent.AddActionBack(_endlessChestInteractAction);
                }

                WaveLogger.Info($"[WaveDirector] {GetName()}: Endless Challenge chest {index + 1}/{count} spawned (id={chest.Id:X}) — {_endlessCycle} wave(s) survived, " +
                    $"{rolls} loot roll(s) waiting to be opened{(isLootsplosion ? " (LOOT-SPLOSION)" : "")}, rarity band: {string.Join(",", allowedRarities.Select(r => r.GetName()))}");
            }
            catch (Exception ex)
            {
                WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless chest spawn failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires on ANY player interaction in the arena region — filters down
        /// to interactions with one of THIS player's own pending reward
        /// chests. Rolls and gives the loot straight to the player's
        /// inventory (LootManager.GiveLootFromTable — never touches the
        /// world/ground at all) then removes the chest so it can't be
        /// re-opened.
        /// </summary>
        private void OnEndlessChestInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null) return;
            if (_pendingChestLoot.TryGetValue(evt.InteractableObject.Id, out var spec) == false) return;

            _pendingChestLoot.Remove(evt.InteractableObject.Id);
            WorldEntity chest = evt.InteractableObject;
            Avatar avatar = CurrentAvatar;
            if (avatar == null) return;

            // The chest itself is a static prop — anchor every roll to ITS
            // position (captured before it's destroyed below), not the
            // avatar's. The player has to be standing still at the chest to
            // interact with it in the first place, so this can never chase a
            // moving position the way the old per-kill/per-tick ground drops
            // did — that's what made those stutter, not ground-dropping
            // itself. Loot physically appears at the chest, like every other
            // lootable chest in the game, then the player walks over and
            // picks it up normally.
            Vector3 chestPos = chest.RegionLocation.Position;

            try
            {
                if (_waveRewardLootTableRef != PrototypeId.Invalid)
                {
                    using LootInputSettings inputSettings = ObjectPoolManager.Instance.Get<LootInputSettings>();
                    inputSettings.Initialize(LootContext.Drop, this, avatar, chestPos);

                    if (spec.rarities != null && spec.rarities.Count > 0)
                    {
                        inputSettings.LootRollSettings.Rarities.Clear();
                        foreach (PrototypeId r in spec.rarities)
                            inputSettings.LootRollSettings.Rarities.Add(r);
                    }

                    for (int i = 0; i < Math.Max(1, spec.rolls); i++)
                        Game.LootManager.SpawnLootFromTable(_waveRewardLootTableRef, inputSettings, 1);
                }

                try { SendBannerLines("💰 Chest opened!"); } catch { }
                WaveLogger.Info($"[WaveDirector] {GetName()}: {GetName()} opened Endless chest {chest.Id:X} — {spec.rolls} roll(s) spawned at the chest");
            }
            catch (Exception ex)
            {
                WaveLogger.Warn($"[WaveDirector] {GetName()}: chest loot spawn failed: {ex.Message}");
            }
            finally
            {
                if (chest.IsInWorld) chest.ExitWorld();
                chest.Destroy();
            }
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
        /// Only used by the non-Endless manual wave-run reward system —
        /// Endless Challenge's own reward is the click-to-open chest system
        /// (SpawnEndlessChests/OnEndlessChestInteract), which gives loot
        /// straight to inventory instead of dropping it on the ground.
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

        /// <summary>
        /// Destroys every dropped Item entity anywhere in the arena region —
        /// called when a new wave starts so loot left over from a previous
        /// wave (or ignored during a loot break) doesn't pile up across the
        /// run. Sweeps the whole region (region.Entities) rather than a
        /// radius around the avatar: a radius-based sweep left loot behind
        /// whenever it landed/scattered farther from the avatar's CURRENT
        /// position than the sweep radius reached (confirmed live 2026-07-26
        /// — a wave-20 lootsplosion left a huge field of ground items still
        /// present several waves later). Items normally expire on their own
        /// (per-rarity Eval-driven Lifespan, ItemPrototype.GetExpirationTime),
        /// but that can be minutes away or, for some rarities, never — this
        /// makes the cleanup deterministic and tied to actual wave progress
        /// instead.
        /// </summary>
        private void DespawnLeftoverGroundLoot(Avatar avatar)
        {
            var region = avatar?.Region;
            if (region == null) return;

            // TEMP PERF INSTRUMENTATION (2026-07-26) — this iterates EVERY
            // entity in the region (region.Entities), once per new wave.
            // That's a real candidate for the still-reported stutter since
            // it wasn't part of the earlier loot-grid investigation at all —
            // measuring instead of assuming.
            var sweepPerfSw = System.Diagnostics.Stopwatch.StartNew();
            int scanned = 0;

            var staleLoot = new List<WorldEntity>();
            foreach (Entity existing in region.Entities)
            {
                scanned++;
                if (existing is not MHServerEmu.Games.Entities.Items.Item item) continue;
                if (item.IsDestroyed) continue;
                staleLoot.Add(item);
            }
            foreach (WorldEntity existing in staleLoot)
            {
                if (existing.IsInWorld) existing.ExitWorld();
                existing.Destroy();
            }

            sweepPerfSw.Stop();
            WaveLogger.Info($"[WaveDirector:Perf] DespawnLeftoverGroundLoot scanned={scanned} destroyed={staleLoot.Count} took {sweepPerfSw.Elapsed.TotalMilliseconds:F1}ms");
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

            if (_isEndlessMode)
                DespawnLeftoverGroundLoot(avatar);

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
                        // Force-clear native WakeRange dormancy so a directly-
                        // spawned wave mob engages immediately instead of
                        // waiting for the player to close within its native
                        // wake range — see SpawnCuratedBoss's Dormant comment
                        // for the full mechanism. Applies to every wave run,
                        // not just Endless.
                        if (agent != null)
                            agent.Properties[PropertyEnum.Dormant] = false;

                        // Plain wave mobs (not phantoms, not curated bosses)
                        // have their own native on-death loot resolution too
                        // (WorldEntity.AwardKillLoot) — confirmed live
                        // 2026-07-26 this was the main source of the ground
                        // loot flood at wave 20 in Endless mode, not just
                        // occasional boss drops. Same per-instance NoLootDrop
                        // override SpawnCuratedBoss already uses.
                        if (agent != null && _isEndlessMode)
                        {
                            agent.Properties[PropertyEnum.NoLootDrop] = true;

                            // Same cross-enemy-hostility fix as SpawnCuratedBoss
                            // — a plain wave mob keeps its own native alliance
                            // otherwise, which can be mutually hostile with the
                            // phantom-hero alliance per the real alliance
                            // table, causing enemies to fight each other.
                            PrototypeId mobAllianceRef = Avatars.Avatar.GetEnemyPhantomAllianceRef();
                            if (mobAllianceRef != PrototypeId.Invalid)
                                agent.Properties[PropertyEnum.AllianceOverride] = mobAllianceRef;
                        }
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

            // Periodic REAL boss — every EndlessBossEveryNWaves cleared
            // waves, one genuine boss-tier AgentPrototype (Doom/Kraven/
            // Green Goblin-class content, not a buffed hero-phantom) spawns
            // on top of the normal wave. Uses the same plain-Agent spawn
            // path the "regular mob" (non-phantom) wave-entry branch above
            // already uses — these boss prototypes carry their own native
            // hostile alliance/stats, no phantom pipeline or manual buff
            // needed. _endlessLastBossWaveSpawned guards against a
            // double-spawn if SpawnNextWave ever re-runs for the same
            // scaleIndex (e.g. the "player died mid-spawn" retry path).
            if (_isEndlessMode && scaleIndex > 0 && scaleIndex % EndlessBossEveryNWaves == 0 && _endlessLastBossWaveSpawned != scaleIndex)
            {
                _endlessLastBossWaveSpawned = scaleIndex;
                var bossPool = GetEndlessBossPool();
                PrototypeId bossRef = bossPool.Count > 0 ? bossPool[rng.Next(bossPool.Count)] : PrototypeId.Invalid;
                if (bossRef != PrototypeId.Invalid)
                {
                    // Routed through SpawnCuratedBoss (was a duplicated raw
                    // CreateAgent call before 2026-07-26) so this periodic
                    // boss gets the same treatment every other curated-boss
                    // spawn already does: the LootCooldownTimeHours fix (was
                    // spamming "Verify failed" log lines otherwise),
                    // NoLootDrop while Endless is active, AICustomThinkRateMS
                    // tuning, and the AllianceOverride fix (this boss keeping
                    // its own native alliance was confirmed live to be
                    // mutually hostile with the phantom-hero alliance,
                    // causing enemies to fight each other instead of both
                    // only fighting the player).
                    ulong bossId = SpawnCuratedBoss(avatar, bossRef, out string bossSpawnErr);
                    if (bossId != 0)
                    {
                        _waveAliveIds.Add(bossId);
                        string bossName = LeafHeroName(bossRef);
                        try { SendBannerLines($"☠ A BOSS HAS ARRIVED — {bossName}!"); } catch { }
                        WaveLogger.Info($"[WaveDirector] {GetName()}: Endless boss spawned at wave {scaleIndex} ({bossName})");
                    }
                    else
                    {
                        WaveLogger.Warn($"[WaveDirector] {GetName()}: Endless boss spawn failed ({bossSpawnErr})");
                    }
                }
            }

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

        // Endless Challenge's own dedicated wave-count widget — kept
        // separate from PreferredGenericFractionWidgetRef because that one
        // is ALSO shared by Player.TrialOfImpossible.cs's kill counter
        // (which genuinely wants an "X/Y" fraction), so its baked Descriptor
        // can't be repointed to "Wave N" wording without breaking Trial's
        // display. UI/MetaGame/GenericRescueIcon.prototype was an otherwise
        // unused blank-Descriptor UIWidgetGenericFractionPrototype (found
        // via /webapi/debug/uiwidgets, 2026-07-25) repurposed here — its
        // Descriptor is patched once, in-memory only (RuntimePrototypeEditor
        // pattern, no .sip/client file touched), to a custom LocaleStringId
        // "Wave $CurrentCount$" (see AchievementStringMap_99_
        // DangerRoomEndless.json) so the HUD reads "Wave 1" instead of the
        // "1/2"-style fraction the shared widget was showing.
        // UIWidgetGenericFractionPrototype's Descriptor/icon fields are
        // resolved CLIENT-SIDE from the client's own local data by
        // PrototypeId — confirmed live 2026-07-25/26: patching Descriptor
        // (and even blanking icon fields) server-side via reflection had
        // zero visible effect on the client, even though the same widget's
        // SetCount() numeric updates DO work live. So there is no way to
        // show custom free text ("Wave N") through that widget type.
        //
        // UIWidgetMissionText is different: UIWidgetMissionText.SetText(
        // LocaleStringId, LocaleStringId) (src\MHServerEmu.Games\UI\Widgets\
        // UIWidgetMissionText.cs) transfers the two LocaleStringIds over the
        // network on every call (see its Serialize override) — the CLIENT
        // renders whatever string ID it's told, live, every time. The
        // catch: it's still an ID lookup, not free text, so there's no
        // single template that formats a live number. Fix: pre-bake one
        // custom LocaleStringId per wave number ("Wave 1".."Wave 300", see
        // AchievementStringMap_99_DangerRoomEndlessWaveNumbers.json) and
        // pick among them by simple offset — same technique a real
        // Mythic-Rift-style mod uses for its own per-level HUD text
        // (confirmed via independent research into how that problem is
        // solved elsewhere: pre-baked string-per-value + SetText, not a
        // formatted template).
        private const ulong EndlessWaveOnlyWidgetRef = 0x636EA5AADD1D0D53; // UI/MetaGame/MissionName.prototype (repurposed, empty prototype subclass — SetText overrides whatever it'd natively show)
        private const ulong EndlessWaveTextBaseStringId = 1234567890123460000; // + waveNumber = "Wave {waveNumber}"
        private const int EndlessWaveTextMaxBaked = 300; // AchievementStringMap_99_DangerRoomEndlessWaveNumbers.json bakes 1..300

        private void UpdateEndlessWaveWidget(Avatar avatar)
        {
            try
            {
                // _endlessCycle counts waves already CLEARED (0 before the
                // first fight) — +1 so the widget reads "Wave 1" while
                // fighting the first wave, "Wave 2" for the second, etc.
                int waveNumber = _endlessCycle + 1;
                int cappedWaveNumber = Math.Min(waveNumber, EndlessWaveTextMaxBaked);
                var widget = avatar.Region?.UIDataProvider?.GetWidget<UIWidgetMissionText>((PrototypeId)EndlessWaveOnlyWidgetRef);
                widget?.SetText((LocaleStringId)(EndlessWaveTextBaseStringId + (ulong)cappedWaveNumber), LocaleStringId.Blank);
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
                PrototypeId widgetRef = (PrototypeId)EndlessWaveOnlyWidgetRef;
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
