using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// A helper class for managing hardcoded entities. TODO: Gradually get rid of stuff in here.
    /// </summary>
    public static class EntityHelper
    {
        public static readonly bool DebugOrb = false;

        public enum TestOrb : ulong
        {
            Red = 925659119519994384, // HealOrbItem = 925659119519994384, 
            BigRed = 18107188791044543532, // LimboBuffOrbItem = 18107188791044543532,
            Greeen = 16852724980331648695, // ExperienceOrbSmallItem = 16852724980331648695,
            BigGreen = 3442167663578518146, // LegendaryOrb = 3442167663578518146,
            Blue = 9607833165236212779, // EnduranceOrbItem = 9607833165236212779
            Orange = 8905675869072986929, // TestOnlyXPOrb = 8905675869072986929,
            XRay = 5358798066155328438, // Radioactive31Orb = 5358798066155328438,
            Pink = 14631580738344719410, // ManhattanOrbItem = 14631580738344719410,
            Hyde = 1644714682932532551, // Art252HydeFormulaOrb = 1644714682932532551,
            Violet = 18337403507337860830, // MagnetoMetalOrb = 18337403507337860830,
        }

        public static Agent CreateOrb(TestOrb orbProto, Vector3 position, Region region)
        {
            if (DebugOrb == false) return null;

            var game = region.Game;

            using var settingsHandle = EntitySettingsPool.Get(out EntitySettings settings);
            settings.EntityRef = (PrototypeId)orbProto;
            settings.Position = position;
            settings.Orientation = new(3.14f, 0.0f, 0.0f);
            settings.RegionId = region.Id;
            settings.Lifespan = TimeSpan.FromSeconds(3);

            using var propertiesHandle = PropertyCollectionPool.Get(out PropertyCollection properties);
            properties[PropertyEnum.AIStartsEnabled] = false;
            properties[PropertyEnum.NoEntityCollide] = true;
            settings.Properties = properties;

            Agent orb = (Agent)game.EntityManager.CreateEntity(settings);
            return orb;
        }

        public static Agent CreateAgent(AgentPrototype agentProto, Avatar avatar, Vector3 spawnPosition, Orientation orientation)
        {
            var region = avatar.Region;

            using var entitySettingsHandle = EntitySettingsPool.Get(out EntitySettings entitySettings);
            entitySettings.EntityRef = agentProto.DataRef;
            entitySettings.Position = spawnPosition;
            entitySettings.Orientation = orientation;
            entitySettings.RegionId = region.Id;
            entitySettings.IsPopulation = true;

            using var settingsPropertiesHandle = PropertyCollectionPool.Get(out PropertyCollection settingsProperties);
            settingsProperties[PropertyEnum.DifficultyTier] = region.DifficultyTierRef;
            settingsProperties[PropertyEnum.Rank] = agentProto.Rank.DataRef;
            settingsProperties[PropertyEnum.CharacterLevel] = avatar.CharacterLevel;
            settingsProperties[PropertyEnum.CombatLevel] = avatar.CharacterLevel;
            entitySettings.Properties = settingsProperties;

            var agent = avatar.Game.EntityManager.CreateEntity(entitySettings) as Agent;

            if (agentProto.ModifiersGuaranteed.HasValue())
                foreach (var boost in agentProto.ModifiersGuaranteed)
                    agent.Properties[PropertyEnum.EnemyBoost, boost] = true;

            return agent;
        }

        public static bool GetSpawnPositionNearAvatar(Avatar avatar, Region region, BoundsPrototype entityBoundsPrototype, float maxDistance, out Vector3 spawnPositionResult)
        {
            Bounds entityBounds = new();
            entityBounds.InitializeFromPrototype(entityBoundsPrototype);
            entityBounds.Center = avatar.RegionLocation.Position + avatar.Forward * 120;
            return region.ChoosePositionAtOrNearPoint(ref entityBounds, avatar.Locomotor.PathFlags, PositionCheckFlags.CanBeBlockedEntity, BlockingCheckFlags.None, maxDistance, out spawnPositionResult, maxPositionTests: 32);
        }

        private static readonly Logger StandaloneBossLogger = LogManager.CreateLogger();
        private const int StandaloneBossThinkRateMs = 50;
        private const float StandaloneBossAggroRange = 3000f;

        /// <summary>
        /// Entity IDs of agents spawned through <see cref="ApplyStandaloneBossFixups"/>
        /// — a real campaign/mission-spawned boss (e.g. the story MODOK
        /// encounter) never gets added here. AI profile code (e.g.
        /// ProceduralProfileMODOKPrototype's movement-fallback fix) checks
        /// this before applying any standalone-spawn-only compensation, so
        /// none of it can ever affect the native campaign encounter — only
        /// our own Endless Wave / Boss Roster test spawns. Never cleared;
        /// a handful of ulong entries per session is negligible, matching
        /// BossRosterWebHandler.SpawnedByPlayer's existing convention.
        /// </summary>
        public static readonly HashSet<ulong> StandaloneBossIds = new();

        /// <summary>
        /// Shared post-spawn fixups for a real boss-tier AgentPrototype
        /// dropped in standalone via <see cref="CreateAgent"/> (not through
        /// the game's own population/mission spawner) — every caller that
        /// spawns a curated boss this way (Player.WaveDirector.cs's
        /// SpawnCuratedBoss, BossRosterWebHandler.cs's manual test spawn)
        /// needs the exact same fixes, so this lives in one place instead of
        /// being duplicated (and silently drifting) across both.
        /// </summary>
        /// <param name="applyEnemyAlliance">
        /// When false, skips the hostile-alliance override below. Friendly
        /// boss phantoms (Avatar.SpawnBossPhantomHero) need every other fixup
        /// here but must NOT be flipped to the enemy alliance — they belong to
        /// the caller's own alliance. That call site always intended to
        /// exclude it (its comment says so) but previously could not: this
        /// method applied it unconditionally and the caller just overwrote it
        /// a moment later, leaving a window where an in-world, already-
        /// replicated boss phantom was genuinely hostile to its own summoner.
        /// Confirmed live 2026-08-09 in the [BossDiag] trace as
        /// "AllianceOverride &lt;unset&gt; -> Enemies -> Players" on every spawn.
        /// </param>
        public static void ApplyStandaloneBossFixups(Agent agent, AgentPrototype bossProto, bool applyEnemyAlliance = true)
        {
            if (agent == null || bossProto == null) return;

            StandaloneBossIds.Add(agent.Id);
            agent.StartStandaloneBossDormantWatchdog();

            // Any AgentPrototype with a nonzero WakeRange spawns Dormant and
            // only wakes once a player closes to within that native range —
            // correct for a population encounter the player walks up to, but
            // a boss dropped 250-400u from the player directly just sits
            // there if its own WakeRange is smaller than that gap.
            agent.Properties[PropertyEnum.Dormant] = false;

            // A curated boss keeps its own native AgentPrototype alliance by
            // default, which can be (and was, confirmed live) genuinely
            // mutually hostile with the phantom-hero enemy alliance per the
            // real game's own alliance table — override to the same
            // alliance phantom heroes use so all enemy categories stay
            // mutually friendly while hostile to the player.
            if (applyEnemyAlliance)
            {
                PrototypeId enemyAllianceRef = Avatars.Avatar.GetEnemyPhantomAllianceRef();
                if (enemyAllianceRef != PrototypeId.Invalid)
                    agent.Properties[PropertyEnum.AllianceOverride] = enemyAllianceRef;
            }

            // A bare CreateAgent spawn has none of LootCooldownByChannel/
            // LootCooldownTimeHours/LootCooldownRolloverWallTime, so
            // ItemResolverContext.SetInternal can't resolve a valid
            // CooldownData.OriginProtoRef for cooldown-gated loot table
            // entries (common for unique boss drops) — GetDropChance's
            // Verify check fails and silently returns 0 (no drop).
            // LootCooldownTimeHours=0 is enough to make SetInternal resolve
            // OriginProtoRef to this boss's own PrototypeDataRef without
            // imposing any real farm-cooldown restriction.
            if (agent.Properties.HasProperty(PropertyEnum.LootCooldownByChannel) == false
                && agent.Properties.HasProperty(PropertyEnum.LootCooldownTimeHours) == false
                && agent.Properties.HasProperty(PropertyEnum.LootCooldownRolloverWallTime) == false)
            {
                agent.Properties[PropertyEnum.LootCooldownTimeHours] = 0;
            }

            // Faster AI reaction time — per-instance only, zero effect on
            // any other spawn of the same boss prototype elsewhere in the
            // game (story campaign, terminals, endgame use the native
            // population/mission spawner, a completely separate path).
            agent.Properties[PropertyEnum.AICustomThinkRateMS] = StandaloneBossThinkRateMs;

            // A real population encounter usually has its boss already
            // aggroed by mission scripting; a standalone spawn 250-400u from
            // the player instead has to rely on the profile's own native
            // AggroRangeHostile, which can be too short for that gap
            // (confirmed live 2026-07-26 on MODOK — he'd only "wake up" for a
            // swing or two right after being hit, since taking damage
            // force-registers the attacker as a target through a separate
            // path that bypasses this range check, then go idle again once
            // that faded). AIAggroRangeOverrideHostile/Ally
            // (AIController.cs:146-160) take priority over the profile's own
            // AggroRangeHostile/Ally whenever present, and — critically,
            // unlike a hard target pin — Combat.GetValidTargetsInSphere
            // (BehaviorSensorySystem.cs:298/322) still searches the WHOLE
            // hostile/ally pool within that radius, so this boss still
            // engages whichever hostile is nearest/best (the real player OR
            // a friendly phantom hero/team-up fighting alongside them) — it
            // just is never left unable to sense any of them at all. This
            // has to go on AIController.Blackboard.PropertyCollection, NOT
            // agent.Properties — they're separate PropertyCollection
            // instances (BehaviorBlackboard.cs:33), and AIController.cs's
            // AggroRangeHostile/Ally getters only ever read the blackboard's
            // copy.
            if (agent.AIController != null)
            {
                var blackboardProps = agent.AIController.Blackboard.PropertyCollection;
                blackboardProps[PropertyEnum.AIAggroRangeOverrideHostile] = StandaloneBossAggroRange;
                blackboardProps[PropertyEnum.AIAggroRangeOverrideAlly] = StandaloneBossAggroRange;
            }

            // MODOK (ProceduralProfileMODOKPrototype) boots into a
            // TeleportToEntity state that only exists to find a scripted
            // ally/kismet actor a real population encounter normally places
            // alongside him — a standalone spawn has no such entity, so
            // SelectTeleportTarget never resolves, Think() returns early
            // every tick, and the state machine never advances into
            // SummonProcedural/GenericProcedural (the states that actually
            // sense hostiles and attack/move). Confirmed live 2026-07-26 —
            // MODOK spawned and stood completely still. Force straight into
            // GenericProcedural so he behaves like a normal hostile
            // immediately; no effect on any other boss's profile type.
            bool isModokProfile = bossProto.BehaviorProfile?.Brain.As<Prototype>() is ProceduralProfileMODOKPrototype;
            if (isModokProfile)
            {
                if (agent.AIController != null)
                {
                    // Confirmed live 2026-07-26 (2nd pass) — forcing
                    // AICustomStateVal1 alone wasn't enough: GenericProcedural
                    // itself reverts straight back to TeleportToEntity the
                    // instant HandleProceduralPower isn't Running, gated on
                    // `currentTime > blackboardProps[AICustomTimeVal1]`. That
                    // timer defaults to 0 for a fresh spawn (we never set
                    // it), so `currentTime > 0` was true on literally the
                    // very first non-Running tick — MODOK bounced right back
                    // into the broken TeleportToEntity loop within a fraction
                    // of a second of the override "succeeding", which is why
                    // the log showed it applying but he still never moved.
                    // Push the revert timer far into the future so that
                    // branch never fires.
                    var blackboard = agent.AIController.Blackboard.PropertyCollection;
                    blackboard[PropertyEnum.AICustomStateVal1] = 2; // State.GenericProcedural
                    long farFutureMs = (long)agent.Game.CurrentTime.TotalMilliseconds + 3_600_000_000L; // effectively never
                    blackboard[PropertyEnum.AICustomTimeVal1] = farFutureMs;

                    // NOTE: the sensing-range gap itself (why he'd only
                    // "wake up" for a swing or two after being hit) is fixed
                    // generically above via AIAggroRangeOverrideHostile/Ally
                    // — deliberately NOT via a hard AIPendingTargetId pin,
                    // since that would lock him onto ONE fixed entity forever
                    // and stop him from ever engaging friendly phantom
                    // heroes/team-ups fighting alongside the player.
                    StandaloneBossLogger.Info($"[EntityHelper] MODOK override applied to {agent.Id:X} (state=GenericProcedural, revert timer pushed to {farFutureMs})");
                }
                else
                {
                    StandaloneBossLogger.Warn($"[EntityHelper] MODOK profile detected on {agent.Id:X} but AIController was null — override NOT applied");
                }
            }
        }
    }
}
