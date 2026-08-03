using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    // Themed bounties — BOUNTY BOARD MODE ONLY.
    //
    // A board roll picks ONE theme (BountyThemes.All) and fills all six slots
    // from that theme's roster instead of the global hero+boss pool. The theme
    // then also drives, for that hunt only:
    //   * which arena the warp picks (theme.Regions instead of all 57),
    //   * which costume an avatar-type target wears,
    //   * which Phantom Requiem projectile powers can fire (theme.PowerGroups),
    //   * what the sterilized arena is repopulated with (theme.MobFactions).
    //
    // Everything in this file is gated behind _bountyHuntBoardSlot >= 0 or is
    // only reachable from Player.BountyBoard.cs. Personal-nemesis Bounty Hunt,
    // Endless Challenge, Trial of the Impossible, the wave director and all
    // story/population content are untouched — they never read _bountyThemeIndex
    // and never call anything here.
    //
    // Resolution is lazy + cached: the generated table stores names (avatar
    // short names, costume leaf names, curated boss display names, region enum
    // names, path segments) rather than PrototypeIds, so the same table works
    // across 1.48/1.52/1.53 where a given entry may or may not exist. Anything
    // that doesn't resolve on THIS server is dropped at resolve time and logged,
    // exactly like GetValidTrialArenaPool already does for arenas.
    public partial class Player
    {
        private static readonly Logger BountyThemeLogger = LogManager.CreateLogger();

        /// <summary>Index into <see cref="BountyThemes.All"/> for the current board roll, or -1 when the board is untheme(d) / legacy.</summary>
        private int _bountyThemeIndex = -1;

        /// <summary>Theme the in-flight hunt was launched under, captured at StartBountyHuntInternal so a re-roll mid-hunt can't change it under us.</summary>
        private int _bountyHuntThemeIndex = -1;

        public int BountyBoardThemeIndex => _bountyThemeIndex;

        internal static BountyThemeDef GetBountyTheme(int index)
        {
            if (index < 0 || index >= BountyThemes.All.Length) return null;
            return BountyThemes.All[index];
        }

        public static string BountyThemeName(int index) => GetBountyTheme(index)?.Name;
        public static string BountyThemeFlavor(int index) => GetBountyTheme(index)?.Flavor;

        // ---------------- Resolved-per-theme caches ----------------
        // Keyed by theme index. Built once per server lifetime; the underlying
        // prototype data never changes at runtime.

        private static readonly object s_themeResolveLock = new();
        private static List<(ulong HeroRef, bool IsBoss, string DisplayName, ulong CostumeRef, string ThemedAlias)>[] s_themeRosters;
        private static List<RegionPrototypeId>[] s_themeRegions;
        private static List<PrototypeId>[] s_themePowerPools;
        private static List<PrototypeId>[] s_themeMobPools;

        /// <summary>
        /// Every target this theme can put on the board — avatar-type phantom
        /// nemeses (with their themed costume resolved) plus curated bosses.
        /// Entries that don't resolve on this server version are skipped.
        /// </summary>
        private static List<(ulong HeroRef, bool IsBoss, string DisplayName, ulong CostumeRef, string ThemedAlias)> GetThemeRoster(int themeIndex)
        {
            var theme = GetBountyTheme(themeIndex);
            if (theme == null) return new();

            lock (s_themeResolveLock)
            {
                s_themeRosters ??= new List<(ulong, bool, string, ulong, string)>[BountyThemes.All.Length];
                if (s_themeRosters[themeIndex] != null) return s_themeRosters[themeIndex];

                var roster = new List<(ulong, bool, string, ulong, string)>(theme.HeroCostumes.Length + theme.Bosses.Length);

                // --- avatar-type targets, with themed costume ---
                var heroesByShortName = new Dictionary<string, PrototypeId>(StringComparer.OrdinalIgnoreCase);
                foreach (var (avatarRef, shortName) in Avatar.GetAllPhantomHeroRefs())
                    heroesByShortName[shortName] = avatarRef;

                foreach (string pair in theme.HeroCostumes)
                {
                    // "AvatarShortName|CostumeLeaf|ThemedAlias"
                    string[] parts = pair.Split('|');
                    if (parts.Length < 2 || parts[0].Length == 0) continue;
                    string heroName = parts[0];
                    string costumeName = parts[1];
                    string themedAlias = parts.Length > 2 ? parts[2] : string.Empty;

                    if (heroesByShortName.TryGetValue(heroName, out PrototypeId avatarRef) == false)
                        continue;   // not on this version

                    ulong costumeRef = 0;
                    if (string.IsNullOrEmpty(costumeName) == false)
                    {
                        string folder = BountyThemes.CostumeFolderFor(heroName);
                        PrototypeId cRef = GameDatabase.GetPrototypeRefByName(
                            $"Entity/Items/Costumes/Prototypes/{folder}/{costumeName}.prototype");
                        // A missing costume is NOT fatal — the target still
                        // spawns, just in its default random costume.
                        if (cRef != PrototypeId.Invalid && cRef.As<CostumePrototype>() != null)
                            costumeRef = (ulong)cRef;
                    }

                    roster.Add(((ulong)avatarRef, false, FriendlyAvatarDisplayName(avatarRef, heroName), costumeRef, themedAlias));
                }

                // --- boss-type targets (no costume slot) ---
                if (theme.Bosses.Length > 0)
                {
                    var bossesByName = new Dictionary<string, PrototypeId>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (bossRef, displayName) in GetCuratedBossPoolWithNames())
                        bossesByName[displayName] = bossRef;

                    foreach (string bossName in theme.Bosses)
                    {
                        if (bossesByName.TryGetValue(bossName, out PrototypeId bossRef) == false)
                            continue;   // not on this version
                        roster.Add(((ulong)bossRef, true, bossName, 0UL, string.Empty));
                    }
                }

                s_themeRosters[themeIndex] = roster;
                BountyThemeLogger.Info($"[BountyTheme] '{theme.Name}' roster resolved: {roster.Count} target(s)");
                return roster;
            }
        }

        /// <summary>
        /// The themed costume for this target, or 0 for "no themed costume"
        /// (which keeps the existing random-costume behaviour). Returns 0 for
        /// bosses (no costume slot) and for untheme(d) hunts.
        /// </summary>
        private static ulong ThemedCostumeForTarget(int themeIndex, ulong heroRef)
        {
            if (themeIndex < 0 || heroRef == 0) return 0;
            foreach (var entry in GetThemeRoster(themeIndex))
            {
                if (entry.HeroRef == heroRef && entry.IsBoss == false)
                    return entry.CostumeRef;
            }
            return 0;
        }

        /// <summary>
        /// Themed presentation for a board target, for the Bounty Board UI:
        /// the costume it will actually wear (so the card can show the right
        /// art instead of the avatar's default look) and its themed alias
        /// (e.g. Thing wearing FearItself is "Angrir, Breaker of Souls").
        /// Both are empty/0 for bosses and untheme(d) boards.
        /// </summary>
        public (ulong CostumeRef, string ThemedAlias) GetBountyBoardThemedPresentation(ulong heroRef, bool isBoss)
        {
            if (_bountyThemeIndex < 0 || isBoss || heroRef == 0) return (0UL, null);
            foreach (var entry in GetThemeRoster(_bountyThemeIndex))
            {
                if (entry.HeroRef == heroRef && entry.IsBoss == false)
                    return (entry.CostumeRef, string.IsNullOrEmpty(entry.ThemedAlias) ? null : entry.ThemedAlias);
            }
            return (0UL, null);
        }

        /// <summary>Arenas this theme can warp into — theme.Regions filtered to what resolves on this server.</summary>
        private static List<RegionPrototypeId> GetThemeRegions(int themeIndex)
        {
            var theme = GetBountyTheme(themeIndex);
            if (theme == null) return new();

            lock (s_themeResolveLock)
            {
                s_themeRegions ??= new List<RegionPrototypeId>[BountyThemes.All.Length];
                if (s_themeRegions[themeIndex] != null) return s_themeRegions[themeIndex];

                var list = new List<RegionPrototypeId>(theme.Regions.Length);
                foreach (string regionName in theme.Regions)
                {
                    if (Enum.TryParse(regionName, out RegionPrototypeId id) == false) continue;
                    if (GameDatabase.GetPrototype<RegionPrototype>((PrototypeId)(ulong)id) == null) continue;
                    list.Add(id);
                }

                s_themeRegions[themeIndex] = list;
                if (list.Count == 0)
                    BountyThemeLogger.Warn($"[BountyTheme] '{theme.Name}' has no arenas on this version — will fall back to the full arena pool");
                return list;
            }
        }

        /// <summary>
        /// Phantom Requiem projectile powers restricted to this theme's factions.
        /// Falls back to the untheme(d) full pool if the theme's groups resolve
        /// to nothing, so a themed hunt never silently loses its hazard layer.
        /// </summary>
        private static List<PrototypeId> GetThemePowerPool(int themeIndex)
        {
            var theme = GetBountyTheme(themeIndex);
            if (theme == null) return GetPhantomRequiemPowerPool();

            lock (s_themeResolveLock)
            {
                s_themePowerPools ??= new List<PrototypeId>[BountyThemes.All.Length];
                if (s_themePowerPools[themeIndex] != null) return s_themePowerPools[themeIndex];

                var full = GetPhantomRequiemPowerPool();
                var list = new List<PrototypeId>(64);
                foreach (PrototypeId powerRef in full)
                {
                    string path = GameDatabase.GetPrototypeName(powerRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    foreach (string group in theme.PowerGroups)
                    {
                        if (path.IndexOf($"Powers/EnemyPowers/{group}/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            list.Add(powerRef);
                            break;
                        }
                    }
                }

                if (list.Count == 0)
                {
                    BountyThemeLogger.Warn($"[BountyTheme] '{theme.Name}' matched no Requiem powers — falling back to the full pool");
                    list = full;
                }

                s_themePowerPools[themeIndex] = list;
                BountyThemeLogger.Info($"[BountyTheme] '{theme.Name}' Requiem pool: {list.Count} power(s)");
                return list;
            }
        }

        // Arena repopulation ------------------------------------------------
        //
        // A Bounty Hunt warp already sterilizes the arena (ClearArena strips
        // every native entity). Themed board hunts refill it afterwards with
        // faction-matched mobs instead of leaving it empty. Spawning reuses the
        // exact same path Player.WaveDirector.cs's SpawnNextWave already uses
        // for plain wave mobs — EntityHelper.CreateAgent, Dormant cleared, and
        // AllianceOverride forced to the shared enemy alliance so the repopulated
        // mobs don't end up mutually hostile with the bounty target and fight
        // each other instead of the player.

        private const int BountyThemeRepopMinRank = 1;

        /// <summary>
        /// Clear bubble kept around the player's arrival point. 350f was far
        /// too small - mobs were landing effectively on top of the player the
        /// instant the warp completed. Scaled down automatically in a small
        /// region (see BountyThemeRepopSafeRadiusFor) so a cramped boss room
        /// doesn't end up with nowhere legal to spawn at all.
        /// </summary>
        private const float BountyThemeRepopSafeRadiusMax = 1400f;
        private const float BountyThemeRepopSafeRadiusMin = 400f;

        /// <summary>Fraction of the region AABB trimmed off each edge when sampling spawn points.</summary>
        private const float BountyThemeRepopBoundsInset = 0.10f;
        /// <summary>Slack passed to ChoosePositionAtOrNearPoint when snapping a sampled point onto walkable ground.</summary>
        private const float BountyThemeRepopPlacementSlack = 600f;

        /// <summary>
        /// Roughly one mob per this much region area. The arena pool spans
        /// everything from single-room boss arenas to large multi-cell
        /// regions, so a flat per-rank count massively over-populates the
        /// small ones (180 in a boss room). Rank now sets the CEILING and
        /// actual region size sets the real number.
        /// </summary>
        private const float BountyThemeRepopAreaPerMob = 200_000f;

        /// <summary>Never fewer than this, even in a tiny arena.</summary>
        private const int BountyThemeRepopMinCount = 25;

        /// <summary>Keep the safe bubble proportional to the arena so a small room still has legal spawn space.</summary>
        private static float BountyThemeRepopSafeRadiusFor(float spanX, float spanY)
        {
            float smaller = Math.Min(spanX, spanY);
            float scaled = smaller * 0.25f;
            if (scaled < BountyThemeRepopSafeRadiusMin) return BountyThemeRepopSafeRadiusMin;
            if (scaled > BountyThemeRepopSafeRadiusMax) return BountyThemeRepopSafeRadiusMax;
            return scaled;
        }

        /// <summary>
        /// Mob families never used for themed arena repopulation, whatever
        /// faction they nominally belong to. Bounty Board only — the native
        /// population spawner and every other mode are unaffected.
        ///
        /// Doop: removed on request 2026-08-03 — a joke/event enemy that reads
        /// as noise in a bounty arena rather than a threat.
        /// </summary>
        private static readonly string[] BountyThemeExcludedMobPaths =
        {
            "/Doop",
        };

        /// <summary>Mob prototypes for this theme's factions (non-abstract, real combat brain, excluding test/deprecated trees).</summary>
        private static List<PrototypeId> GetThemeMobPool(int themeIndex)
        {
            var theme = GetBountyTheme(themeIndex);
            if (theme == null) return new();

            lock (s_themeResolveLock)
            {
                s_themeMobPools ??= new List<PrototypeId>[BountyThemes.All.Length];
                if (s_themeMobPools[themeIndex] != null) return s_themeMobPools[themeIndex];

                var list = new List<PrototypeId>(256);
                foreach (PrototypeId agentRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (agentRef == PrototypeId.Invalid) continue;
                    var proto = agentRef.As<AgentPrototype>();
                    if (proto == null || proto is AvatarPrototype) continue;

                    // Needs a real combat brain, same gate the boss candidate
                    // pool uses — otherwise inert props/NPCs get spawned.
                    if (proto.BehaviorProfile == null || proto.BehaviorProfile.Brain == PrototypeId.Invalid) continue;

                    string path = GameDatabase.GetPrototypeName(agentRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("zzz", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (path.IndexOf("/Test/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    // Excluded mob families — kept as a list so this stays a
                    // Bounty-Board-only filter and never touches the native
                    // population spawner or any other mode.
                    bool excluded = false;
                    foreach (string bad in BountyThemeExcludedMobPaths)
                    {
                        if (path.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) { excluded = true; break; }
                    }
                    if (excluded) continue;

                    foreach (string faction in theme.MobFactions)
                    {
                        if (path.IndexOf($"Entity/Characters/Mobs/{faction}/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            list.Add(agentRef);
                            break;
                        }
                    }
                }

                s_themeMobPools[themeIndex] = list;
                BountyThemeLogger.Info($"[BountyTheme] '{theme.Name}' mob pool: {list.Count} prototype(s) from {theme.MobFactions.Length} faction(s)");
                return list;
            }
        }

        /// <summary>How many themed mobs to seed the arena with at the given bounty rank.</summary>
        /// <summary>
        /// How many themed mobs to seed the arena with. Sterilizing an arena
        /// strips ~800 native entities (816 measured live in
        /// CH0801AIMWeaponFacilityRegion), so these are deliberately in the
        /// hundreds — the arena should read as genuinely repopulated by the
        /// theme's faction, not lightly sprinkled.
        /// </summary>
        /// <summary>
        /// UPPER BOUND on themed mobs for this rank. The number actually
        /// spawned is min(this, region area / BountyThemeRepopAreaPerMob), so
        /// a big arena fills out and a small one does not get swamped.
        /// </summary>
        private static int BountyThemeRepopCountForRank(int rank)
        {
            if (rank >= 9) return 220;
            if (rank >= 7) return 180;
            if (rank >= 5) return 150;
            if (rank >= 3) return 120;
            return 90;
        }

        /// <summary>
        /// Refill the just-sterilized arena with this theme's faction mobs.
        /// Board hunts only - the caller is gated on _bountyHuntBoardSlot >= 0.
        ///
        /// Spawn points are sampled across the WHOLE region AABB rather than a
        /// ring around the player, so the arena repopulates end to end instead
        /// of piling up next to the arrival point. Each sample is snapped onto
        /// walkable ground with ChoosePositionAtOrNearPoint; a rejected sample
        /// is retried elsewhere rather than abandoning that mob, which is what
        /// previously left only 1 of 10 actually spawning (confirmed live
        /// 2026-08-03: a ring 400-1200u out lands off-navmesh most of the time
        /// in a boxed-in arena, and the old code gave up on each failure).
        ///
        /// Best-effort throughout - every spawn is individually wrapped and the
        /// whole pass is wrapped, so nothing here can break the warp.
        /// </summary>
        private void RepopulateThemedArena(Avatar avatar, Region region, int themeIndex, int rank)
        {
            if (avatar == null || region == null) return;
            if (rank < BountyThemeRepopMinRank) return;

            var theme = GetBountyTheme(themeIndex);
            if (theme == null) return;

            try
            {
                var pool = GetThemeMobPool(themeIndex);
                if (pool.Count == 0)
                {
                    BountyThemeLogger.Warn($"[BountyTheme] '{theme.Name}' has no mobs on this version - arena left sterile");
                    return;
                }

                var rng = Game.Random;
                PrototypeId enemyAllianceRef = Avatar.GetEnemyPhantomAllianceRef();
                int spawned = 0;

                var aabb = region.Aabb;
                float insetX = aabb.Width * BountyThemeRepopBoundsInset;
                float insetY = aabb.Length * BountyThemeRepopBoundsInset;
                float minX = aabb.Min.X + insetX, maxX = aabb.Max.X - insetX;
                float minY = aabb.Min.Y + insetY, maxY = aabb.Max.Y - insetY;
                float spanX = Math.Max(0f, maxX - minX);
                float spanY = Math.Max(0f, maxY - minY);
                Vector3 avatarPos = avatar.RegionLocation.Position;

                // Region-size-aware count: rank is the ceiling, area decides
                // the real number. Area is logged so this stays tunable off
                // real measurements rather than guesswork.
                float area = spanX * spanY;
                int rankCap = BountyThemeRepopCountForRank(rank);
                int byArea = (int)(area / BountyThemeRepopAreaPerMob);
                int want = Math.Clamp(byArea, BountyThemeRepopMinCount, rankCap);

                float safeRadius = BountyThemeRepopSafeRadiusFor(spanX, spanY);
                float safeSq = safeRadius * safeRadius;

                // Several placement attempts per mob - a rejected sample means
                // "that spot isn't walkable", not "stop spawning".
                int attemptBudget = want * 6;
                int attempts = 0;

                while (spawned < want && attempts < attemptBudget)
                {
                    attempts++;
                    try
                    {
                        var mobProto = pool[rng.Next(pool.Count)].As<AgentPrototype>();
                        if (mobProto == null) continue;

                        Vector3 candidate = new(
                            minX + (float)(rng.NextDouble() * spanX),
                            minY + (float)(rng.NextDouble() * spanY),
                            avatarPos.Z);

                        float dx = candidate.X - avatarPos.X;
                        float dy = candidate.Y - avatarPos.Y;
                        if ((dx * dx + dy * dy) < safeSq) continue;

                        Bounds bounds = new();
                        bounds.InitializeFromPrototype(mobProto.Bounds);
                        bounds.Center = candidate;

                        if (region.ChoosePositionAtOrNearPoint(ref bounds, avatar.Locomotor.PathFlags,
                                PositionCheckFlags.CanBeBlockedEntity, BlockingCheckFlags.None,
                                BountyThemeRepopPlacementSlack, out Vector3 pos, maxPositionTests: 16) == false)
                        {
                            continue;   // unwalkable sample - try somewhere else
                        }
                        pos = RegionLocation.ProjectToFloor(region, pos);

                        Agent mob = EntityHelper.CreateAgent(mobProto, avatar, pos, avatar.RegionLocation.Orientation);
                        if (mob == null) continue;

                        // Same two fixups SpawnNextWave applies to plain wave
                        // mobs: clear native WakeRange dormancy so they engage,
                        // and force the shared enemy alliance so they fight the
                        // player rather than the bounty target.
                        mob.Properties[PropertyEnum.Dormant] = false;
                        if (enemyAllianceRef != PrototypeId.Invalid)
                            mob.Properties[PropertyEnum.AllianceOverride] = enemyAllianceRef;

                        spawned++;
                    }
                    catch (Exception ex)
                    {
                        BountyThemeLogger.Warn($"[BountyTheme] {GetName()}: repop mob spawn failed: {ex.Message}");
                    }
                }

                BountyThemeLogger.Info(
                    $"[BountyTheme] {GetName()}: '{theme.Name}' repopulated arena with {spawned}/{want} mob(s) " +
                    $"in {attempts} attempt(s) (rank {rank}, cap {rankCap}, byArea {byArea}, " +
                    $"span {spanX:F0}x{spanY:F0}, safeRadius {safeRadius:F0})");
            }
            catch (Exception ex)
            {
                BountyThemeLogger.Warn($"[BountyTheme] {GetName()}: arena repopulation failed: {ex.Message}");
            }
        }
    }
}
