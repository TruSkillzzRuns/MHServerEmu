using System.Net;
using System.Text.Json;
using System.Web;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.WebFrontend.Network;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    // GET /webapi/enemies/catalog — enumerates every AgentPrototype that has
    // a PortraitPath, with rank. Built lazily once and cached. Optional
    // ?q=substring to scope. Read-only, no auth.
    internal class EnemyCatalogWebHandler : WebHandler
    {
        private static EnemyCatalogResponse _cached;
        private static readonly object _buildLock = new();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            EnemyCatalogResponse cat = GetOrBuild();
            var qs = HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            string q = qs.Get("q");

            if (!string.IsNullOrWhiteSpace(q))
            {
                var filtered = new EnemyCatalogResponse
                {
                    TotalReturned = 0,
                    Entries = new List<EnemyCatalogEntry>(),
                };
                foreach (var e in cat.Entries)
                {
                    if (e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || e.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || (e.Rank != null && e.Rank.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        filtered.Entries.Add(e);
                    }
                }
                filtered.TotalReturned = filtered.Entries.Count;
                await context.SendJsonAsync(filtered);
                return;
            }

            await context.SendJsonAsync(cat);
        }

        private static EnemyCatalogResponse GetOrBuild()
        {
            if (_cached != null) return _cached;
            lock (_buildLock)
            {
                if (_cached != null) return _cached;

                var list = new List<EnemyCatalogEntry>(4096);
                foreach (PrototypeId agentRef in DataDirectory.Instance.IteratePrototypesInHierarchy<AgentPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (agentRef == PrototypeId.Invalid) continue;
                    var proto = agentRef.As<AgentPrototype>();
                    if (proto == null) continue;
                    // Skip avatars - they inherit AgentPrototype but aren't enemies.
                    if (proto is AvatarPrototype) continue;
                    // Resolve the best portrait asset for this agent. Prefer the HD variant
                    // when present; fall back to the standard IconPath. Agents with neither
                    // tend to be utility / internal prototypes, not real enemies — skip them.
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    AssetId iconAssetId = proto.IconPathHiRes != 0 ? proto.IconPathHiRes : proto.IconPath;
#else
                    AssetId iconAssetId = proto.IconPath;
#endif
                    if (iconAssetId == 0) continue;

                    string path = GameDatabase.GetPrototypeName(agentRef);
                    if (string.IsNullOrEmpty(path)) continue;

                    // Hide things outside the canonical /Characters/ + /Bosses/ trees by default — keeps the catalog focused.
                    if (path.IndexOf("/Characters/", StringComparison.OrdinalIgnoreCase) < 0
                        && path.IndexOf("/Bosses/", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Drop non-combat / utility agents that clog the catalog. These subtree fragments are
                    // path-stable identifiers and exclude prototypes that have icons but aren't fightable
                    // enemies (training-room dummies, throwables, cinematic actors, mob filler entries,
                    // doom-bot debugging spawners, vendor/quest NPCs that happen to live under /Characters/).
                    if (path.IndexOf("/TrainingRoom", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/TrainingDummies", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/CombatDummies", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Throwables", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Cinematic", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Test/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Debug/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Vendors/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/CivilianNPCs/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Civilians/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Ambient/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/CompanionPets/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/zzzDeprecated", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/Loot/", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("/InteractableObjects/", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // Require a BehaviorProfile with a Brain — without one, the prototype has no AI
                    // tree to engage with combat. This drops most ambient / scenery / utility agents
                    // that otherwise sneak in via the path filters above.
                    if (proto.BehaviorProfile == null || proto.BehaviorProfile.Brain == PrototypeId.Invalid)
                        continue;

                    string leaf = ExtractLeaf(path);
                    string portrait = GameDatabase.GetAssetName(iconAssetId);
                    string rankName = null;
                    if (proto.Rank != null)
                    {
                        string rankPath = GameDatabase.GetPrototypeName(proto.Rank.DataRef);
                        if (!string.IsNullOrEmpty(rankPath)) rankName = ExtractLeaf(rankPath);
                    }

                    string faction = ExtractFaction(path);
                    string category = path.IndexOf("/Bosses/", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Boss" : "Mob";

                    list.Add(new EnemyCatalogEntry
                    {
                        ProtoRef = $"0x{(ulong)agentRef:X16}",
                        Name = leaf,
                        Path = path,
                        PortraitPath = portrait,
                        Rank = rankName,
                        Faction = faction,
                        Category = category,
                    });
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _cached = new EnemyCatalogResponse { TotalReturned = list.Count, Entries = list };
                return _cached;
            }
        }

        private static string ExtractLeaf(string path)
        {
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }

        // Pull the segment AFTER /Mobs/ or /Bosses/ as the faction. Examples:
        //   Entity/Characters/Mobs/Hand/HandNinja.prototype          → "Hand"
        //   Entity/Characters/Mobs/Hydra/HydraSoldier.prototype      → "Hydra"
        //   Entity/Characters/Bosses/Sabretooth/SabretoothPVE.prot   → "Sabretooth"
        // Bosses + Mobs paths are the only two trees that pass the catalog
        // filter (line 197-199), so this maps every entry to something.
        private static string ExtractFaction(string path)
        {
            string[] markers = { "/Mobs/", "/Bosses/" };
            foreach (var marker in markers)
            {
                int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                int start = i + marker.Length;
                int end = path.IndexOf('/', start);
                if (end < 0) end = path.Length;
                return path[start..end];
            }
            return "Other";
        }
    }

    public class EnemyCatalogResponse
    {
        public int TotalReturned { get; set; }
        public List<EnemyCatalogEntry> Entries { get; set; }
    }

    public class EnemyCatalogEntry
    {
        // OmegaDev pages read this as "ref" (a JS-friendly short name);
        // camelCase on its own would give us "protoRef". Pin it explicitly.
        [System.Text.Json.Serialization.JsonPropertyName("ref")]
        public string ProtoRef { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string PortraitPath { get; set; }   // UE3 texture asset path
        public string Rank { get; set; }            // Minion / Elite / Boss / etc.
        // Faction = path segment AFTER /Mobs/ or /Bosses/, e.g. "Hand", "Hydra",
        // "Sabretooth". Lets the UI cluster the catalog by enemy group.
        public string Faction { get; set; }
        // Category = "Boss" if the prototype lives under /Bosses/, else "Mob".
        // Cheap top-level split for the UI's chip filter.
        public string Category { get; set; }
    }
}
