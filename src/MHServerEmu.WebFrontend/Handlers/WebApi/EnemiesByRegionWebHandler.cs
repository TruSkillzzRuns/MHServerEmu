using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    // GET /webapi/enemies/byregion
    //
    // Builds a (region → enemy AgentPrototype) affiliation map by walking the
    // canonical generator chain:
    //   RegionPrototype.RegionGenerator.GetAreasInGenerator(set)
    //     → for each Area: AreaPrototype.Population
    //       → PopulationPrototype.Themes / .GlobalEncounters
    //         → PopulationObjectListPrototype.GetContainedEntities(set)
    //
    // That recursive descent (PopulationObjectPrototype + subclasses) yields
    // every concrete AgentPrototype any populator could ever pick under that
    // region. We then invert the map so the Region Builder picker can show
    // "enemies that appear in this region" alongside the existing faction
    // grouping.
    //
    // Response:
    //   {
    //     "regions": [
    //       { "regionRef": "0x…", "regionPath": "…", "leaf": "AIMLabRegion",
    //         "agentRefs": ["0xa…", "0xb…"], "agentCount": 12 },
    //       …
    //     ],
    //     "enemies": [
    //       { "agentRef": "0xa…", "leaf": "HandNinja",
    //         "regions": ["AIMLabRegion", "BrooklynBridgeRegion"] },
    //       …
    //     ]
    //   }
    //
    // Cached for server lifetime (the inputs are baked prototypes).
    internal class EnemiesByRegionWebHandler : WebHandler
    {
        public override WebApiAccessType Access { get => WebApiAccessType.None; }

        private static object _cached;
        private static readonly object _gate = new();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            object payload = _cached;
            if (payload == null)
            {
                lock (_gate)
                {
                    if (_cached == null)
                    {
                        _cached = Build();
                    }
                    payload = _cached;
                }
            }
            await context.SendJsonAsync(payload);
        }

        private static object Build()
        {
            // Per-region affiliation: regionLeaf → set of agent refs.
            // Use leaf name (not ref) as the key surface for the client so the
            // sidebar reads naturally ("AIMLabRegion"), not as hex.
            var regionToAgents = new Dictionary<string, HashSet<PrototypeId>>(StringComparer.OrdinalIgnoreCase);
            var regionMeta     = new Dictionary<string, (PrototypeId Ref, string Path)>(StringComparer.OrdinalIgnoreCase);
            var agentToRegions = new Dictionary<PrototypeId, HashSet<string>>();

            foreach (PrototypeId regionPid in DataDirectory.Instance.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                if (regionPid == PrototypeId.Invalid) continue;
                var region = regionPid.As<RegionPrototype>();
                if (region == null) continue;

                string path = GameDatabase.GetPrototypeName(regionPid);
                if (string.IsNullOrEmpty(path)) continue;
                string leaf = LeafOf(path);

                var areas = new HashSet<PrototypeId>();
                try { region.RegionGenerator?.GetAreasInGenerator(areas); }
                catch { continue; }
                if (areas.Count == 0) continue;

                var agents = new HashSet<PrototypeId>();
                foreach (PrototypeId areaPid in areas)
                {
                    var area = areaPid.As<AreaPrototype>();
                    if (area == null) continue;
                    // Primary population on the area.
                    if (area.Population != PrototypeId.Invalid)
                    {
                        var pop = area.Population.As<PopulationPrototype>();
                        CollectPopulationAgents(pop, agents);
                    }
                    // Style-driven alternate populations (StyleEntryPrototype
                    // can swap in a different PopulationPrototype per visual
                    // style — common in seasonal/event variants).
                    if (area.Styles != null)
                        foreach (var s in area.Styles)
                            if (s?.Population != PrototypeId.Invalid)
                                CollectPopulationAgents(s.Population.As<PopulationPrototype>(), agents);
                }
                // TR19 — also walk RegionPrototype.PopulationOverrides.
                // Wave-battle / patrol / event regions (EGWB01Juggernaut,
                // PatrolMidtownMODOK, etc.) inject their boss + miniboss
                // populations through this list rather than through the
                // base area Population. Without this we'd say those bosses
                // belong to no region, which is the opposite of the truth.
                if (region.PopulationOverrides != null)
                    foreach (var popOverrideRef in region.PopulationOverrides)
                        CollectPopulationAgents(popOverrideRef.As<PopulationPrototype>(), agents);
                if (agents.Count == 0) continue;

                regionToAgents[leaf] = agents;
                regionMeta[leaf] = (regionPid, path);
                foreach (var ag in agents)
                {
                    if (!agentToRegions.TryGetValue(ag, out var set))
                        agentToRegions[ag] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(leaf);
                }
            }

            // Materialize the response — JSON-friendly, sorted for stable output.
            var regions = new List<object>(regionToAgents.Count);
            foreach (var kv in regionToAgents.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var meta = regionMeta[kv.Key];
                regions.Add(new
                {
                    regionRef  = $"0x{(ulong)meta.Ref:X16}",
                    regionPath = meta.Path,
                    leaf       = kv.Key,
                    agentRefs  = kv.Value
                        .Select(a => $"0x{(ulong)a:X16}")
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    agentCount = kv.Value.Count,
                });
            }

            var enemies = new List<object>(agentToRegions.Count);
            foreach (var kv in agentToRegions.OrderBy(p => GameDatabase.GetPrototypeName(p.Key) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                string agentPath = GameDatabase.GetPrototypeName(kv.Key) ?? "";
                enemies.Add(new
                {
                    agentRef  = $"0x{(ulong)kv.Key:X16}",
                    agentPath = agentPath,
                    leaf      = LeafOf(agentPath),
                    regions   = kv.Value
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                });
            }

            return new
            {
                regionCount = regions.Count,
                enemyCount  = enemies.Count,
                regions,
                enemies,
            };
        }

        private static void CollectPopulationAgents(PopulationPrototype pop, HashSet<PrototypeId> agents)
        {
            if (pop == null) return;
            try { pop.Themes?.GetContainedEntities(agents); }
            catch { /* one bad theme shouldn't break the whole walk */ }
            try { pop.GlobalEncounters?.GetContainedEntities(agents); }
            catch { /* ditto */ }
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            string leaf = slash < 0 ? path : path.Substring(slash + 1);
            int dot = leaf.IndexOf('.');
            return dot < 0 ? leaf : leaf.Substring(0, dot);
        }
    }
}
