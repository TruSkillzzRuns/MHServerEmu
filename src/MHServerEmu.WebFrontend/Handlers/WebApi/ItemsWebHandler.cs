// OmegaDev2 Gear Picker endpoints.
//
//   GET  /webapi/items/catalog   — every approved concrete item prototype in
//                                  the loaded client data, categorized, plus
//                                  the rarity ladder for the override dropdown.
//                                  Built once and cached.
//   POST /webapi/items/give      — body: { "playerName": "...", "playerDbId": "0x...",
//                                          "items": [ { "itemProtoRef": "0x...",
//                                                        "count": 1, "level": 0,
//                                                        "rarityProtoRef": "0x..." } ] }
//                                  Marshals onto the game thread via
//                                  Player.GiveItemsFromWeb and returns real
//                                  per-item results.
//
// Everything here is resolved from the loaded client data at runtime — no
// item / rarity identifiers live in server source.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class ItemCatalogWebHandler : WebHandler
    {
        private static ItemCatalogResponse _cached;
        private static readonly object _buildLock = new();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            await context.SendJsonAsync(GetOrBuild());
        }

        private static ItemCatalogResponse GetOrBuild()
        {
            if (_cached != null) return _cached;
            lock (_buildLock)
            {
                if (_cached != null) return _cached;

                // Avatar short-name lookup for per-hero attribution (costumes
                // declare UsableBy directly; armor is resolved by probing
                // IsUsableByAgent against every playable avatar).
                var avatarProtos = new List<AvatarPrototype>(80);
                foreach (PrototypeId avatarRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    var avatarProto = avatarRef.As<AvatarPrototype>();
                    if (avatarProto != null) avatarProtos.Add(avatarProto);
                }

                // Localized display names ("Art067" → the item's real name)
                // resolved through the server's loaded string tables.
                var locale = MHServerEmu.Games.Locales.LocaleManager.Instance.CurrentLocale;

                // Unique detection: an item is a "unique" when its loot drop
                // restrictions pin the rarity to the engine's RarityUnique ref
                // (OutputRarity with that value, or a RarityRestriction whose
                // allowed list is only that rarity).
                PrototypeId rarityUniqueRef = GameDatabase.LootGlobalsPrototype.RarityUnique;

                var items = new List<ItemCatalogEntry>(1 << 14);
                var seenItemRefs = new HashSet<ulong>(1 << 14);

                foreach (PrototypeId itemRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<ItemPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    var itemProto = itemRef.As<ItemPrototype>();
                    if (itemProto == null) continue;

                    string path = GameDatabase.GetPrototypeName(itemRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("zzzDeprecated", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (path.IndexOf("/TEST", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string category = Categorize(itemProto, path);
                    if (category == null) continue; // filtered class

                    string displayName = null;
                    if (itemProto.DisplayName != LocaleStringId.Invalid && locale != null)
                    {
                        displayName = locale.GetLocaleString(itemProto.DisplayName);
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
                    }

                    string avatar = null;
                    if (itemProto is CostumePrototype costumeProto)
                    {
                        avatar = LeafOf(GameDatabase.GetPrototypeName(costumeProto.UsableBy));
                    }
                    else if (itemProto is ArmorPrototype)
                    {
                        // Hero-specific armor: probe the playable roster and
                        // attribute only when EXACTLY ONE avatar can use it —
                        // any-hero items (e.g. the AnyHero unique tree) are
                        // usable by everyone and must not get pinned to
                        // whichever avatar happened to be probed first.
                        AvatarPrototype single = null;
                        int usableCount = 0;
                        foreach (var avatarProto in avatarProtos)
                        {
                            if (itemProto.IsUsableByAgent(avatarProto))
                            {
                                usableCount++;
                                if (usableCount > 1) { single = null; break; }
                                single = avatarProto;
                            }
                        }
                        if (single != null)
                            avatar = LeafOf(GameDatabase.GetPrototypeName(single.DataRef));
                    }

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    AssetId iconAssetId = itemProto.IconPathHiRes != 0 ? itemProto.IconPathHiRes : itemProto.IconPath;
#else
                    AssetId iconAssetId = itemProto.IconPath;
#endif

                    items.Add(new ItemCatalogEntry
                    {
                        ProtoRef = $"0x{(ulong)itemRef:X16}",
                        Name = LeafOf(path),
                        DisplayName = displayName,
                        Path = path,
                        Category = category,
                        Slot = itemProto.DefaultEquipmentSlot > 0 ? itemProto.DefaultEquipmentSlot.ToString() : null,
                        Avatar = avatar,
                        IconPath = iconAssetId != 0 ? GameDatabase.GetAssetName(iconAssetId) : null,
                        IsUnique = IsUniqueItem(itemProto, path, rarityUniqueRef),
                    });
                    seenItemRefs.Add((ulong)itemRef);
                }

                // Supplemental pass: costumes whose DesignState is below the
                // approval threshold (e.g. the Age of Apocalypse Horsemen on
                // 1.53) are real, fully-populated costumes that the ApprovedOnly
                // pass above silently excludes -- same finding as the phantom
                // costume dropdown fix. Included here too so the Gear Picker
                // (and the force-equip test button) can actually reach them.
                foreach (PrototypeId costumeRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<CostumePrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    if (seenItemRefs.Contains((ulong)costumeRef)) continue;

                    var costumeProto = costumeRef.As<CostumePrototype>();
                    if (costumeProto == null) continue;

                    string path = GameDatabase.GetPrototypeName(costumeRef);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("zzzDeprecated", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (path.IndexOf("/TEST", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string displayName = null;
                    if (costumeProto.DisplayName != LocaleStringId.Invalid && locale != null)
                    {
                        displayName = locale.GetLocaleString(costumeProto.DisplayName);
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
                    }

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    AssetId costumeIconAssetId = costumeProto.IconPathHiRes != 0 ? costumeProto.IconPathHiRes : costumeProto.IconPath;
#else
                    AssetId costumeIconAssetId = costumeProto.IconPath;
#endif

                    items.Add(new ItemCatalogEntry
                    {
                        ProtoRef = $"0x{(ulong)costumeRef:X16}",
                        Name = LeafOf(path),
                        DisplayName = displayName,
                        Path = path,
                        Category = "Costume",
                        Slot = null,
                        Avatar = LeafOf(GameDatabase.GetPrototypeName(costumeProto.UsableBy)),
                        IconPath = costumeIconAssetId != 0 ? GameDatabase.GetAssetName(costumeIconAssetId) : null,
                        IsUnique = false,
                    });
                    seenItemRefs.Add((ulong)costumeRef);
                }

                items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                // Rarity ladder for the override dropdown, straight from data.
                var rarities = new List<ItemRarityEntry>();
                foreach (PrototypeId rarityRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<RarityPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    var rarityProto = rarityRef.As<RarityPrototype>();
                    if (rarityProto == null) continue;
                    rarities.Add(new ItemRarityEntry
                    {
                        ProtoRef = $"0x{(ulong)rarityRef:X16}",
                        Name = LeafOf(GameDatabase.GetPrototypeName(rarityRef)),
                        Tier = rarityProto.Tier,
                    });
                }
                rarities.Sort((a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier) : string.CompareOrdinal(a.Name, b.Name));

                var categories = new List<string>();
                foreach (var item in items)
                    if (categories.Contains(item.Category) == false) categories.Add(item.Category);
                categories.Sort(StringComparer.Ordinal);

                _cached = new ItemCatalogResponse
                {
                    TotalItems = items.Count,
                    Categories = categories,
                    Rarities = rarities,
                    Items = items,
                };
                return _cached;
            }
        }

        // Category = the item's prototype class where distinct, otherwise the
        // path segment after Entity/Items/. Returns null for classes that are
        // not useful in a gear picker.
        private static string Categorize(ItemPrototype itemProto, string path)
        {
            switch (itemProto)
            {
                case CostumePrototype: return "Costume";
                case ArmorPrototype: return "Armor";
                case ArtifactPrototype: return "Artifact";
                case MedalPrototype: return "Medal";
                case RelicPrototype: return "Relic";
                case LegendaryPrototype: return "Legendary";
                case CharacterTokenPrototype: return "CharacterToken";
                case TeamUpGearPrototype: return "TeamUpGear";
                case BagItemPrototype: return null;
                case InventoryStashTokenPrototype: return null;
                case EmoteTokenPrototype: return null;
            }

            // Path-derived buckets for base-class items.
            const string marker = "Entity/Items/";
            int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "Other";
            int start = i + marker.Length;
            int end = path.IndexOf('/', start);
            if (end < 0) return "Other";
            string segment = path[start..end];

            return segment switch
            {
                "Rings" => "Ring",
                "Insignias" => "Insignia",
                "UruForged" => "UruForged",
                "Crafting" => "Crafting",
                "Consumables" => "Consumable",
                "CurrencyItems" => "Currency",
                "Pets" => "Pet",
                "Costumes" => "Costume",
                "Rarity" => null,       // rarity protos are not giveable items
                _ => segment,
            };
        }

        private static bool IsUniqueItem(ItemPrototype itemProto, string path, PrototypeId rarityUniqueRef)
        {
            // Primary marker in 1.52 data: uniques live in the
            // .../UniquePrototypes/... tree (verified via display-name dump —
            // e.g. the named orange gear resolves to Unique### leaves there).
            if (path != null && path.IndexOf("/UniquePrototypes/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Fallback marker: loot drop restrictions that pin rarity to the
            // engine's RarityUnique ref.
            if (rarityUniqueRef == PrototypeId.Invalid) return false;
            if (itemProto.LootDropRestrictions == null || itemProto.LootDropRestrictions.Length == 0) return false;

            foreach (DropRestrictionPrototype restriction in itemProto.LootDropRestrictions)
            {
                if (restriction is OutputRarityPrototype outputRarity && outputRarity.Value == rarityUniqueRef)
                    return true;

                if (restriction is RarityRestrictionPrototype rarityRestriction &&
                    rarityRestriction.AllowedRarities != null && rarityRestriction.AllowedRarities.Length > 0)
                {
                    bool allUnique = true;
                    foreach (PrototypeId allowed in rarityRestriction.AllowedRarities)
                        if (allowed != rarityUniqueRef) { allUnique = false; break; }
                    if (allUnique) return true;
                }
            }

            return false;
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }

    public class ItemGiveWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            string playerDbId = null;
            var entries = new List<Player.WebItemGiveEntry>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var entry = new Player.WebItemGiveEntry { Count = 1, Level = 0 };
                        if (el.TryGetProperty("itemProtoRef", out var ir)) entry.ItemProtoRef = ParseRef(ir.GetString());
                        if (el.TryGetProperty("count", out var cn)) entry.Count = cn.GetInt32();
                        if (el.TryGetProperty("level", out var lv)) entry.Level = lv.GetInt32();
                        if (el.TryGetProperty("rarityProtoRef", out var rr)) entry.RarityProtoRef = ParseRef(rr.GetString());
                        if (entry.ItemProtoRef != 0) entries.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { message = $"bad request: {ex.Message}", givenCount = 0 });
                return;
            }

            if (entries.Count == 0)
            {
                await context.SendJsonAsync(new { message = "no valid items in request", givenCount = 0 });
                return;
            }

            Player player = FindTargetPlayer(playerName, playerDbId, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { message = findError ?? "player not found", givenCount = 0 });
                return;
            }

            var tcs = new TaskCompletionSource<List<Player.WebItemGiveResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.GiveItemsFromWeb(entries, tcs);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != tcs.Task)
            {
                await context.SendJsonAsync(new { message = "timed out waiting for the game thread", givenCount = 0 });
                return;
            }

            List<Player.WebItemGiveResult> results = tcs.Task.Result;
            int given = 0;
            foreach (var r in results) given += r.GivenCount;

            Logger.Info($"[Items:Give] {player.GetName()} ← {entries.Count} entries, {given} item(s) delivered");
            await context.SendJsonAsync(new
            {
                message = $"delivered to {player.GetName()}",
                givenCount = given,
                results,
            });
        }

        private static ulong ParseRef(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong v) ? v : 0;
        }

        // Locate the target player in any running game. Uses the same
        // reflection route to GameManager the other OmegaDev handlers use
        // (WebFrontend has no direct service reference), then switches to
        // direct Games types.
        private static Player FindTargetPlayer(string playerName, string playerDbId, out string error)
        {
            error = null;

            var smType = Type.GetType("MHServerEmu.Core.Network.ServerManager, MHServerEmu.Core");
            var smInstance = smType?.GetProperty("Instance")?.GetValue(null);
            if (smInstance == null) { error = "ServerManager.Instance missing"; return null; }

            var services = smType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(smInstance) as System.Collections.IEnumerable;
            if (services == null) { error = "ServerManager._services missing"; return null; }

            object gameManager = null;
            foreach (var svc in services)
            {
                if (svc == null) continue;
                var prop = svc.GetType().GetProperty("GameManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) { gameManager = prop.GetValue(svc); if (gameManager != null) break; }
            }
            if (gameManager == null) { error = "GameManager not reachable"; return null; }

            var gameDict = gameManager.GetType().GetField("_gameDict", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gameManager) as System.Collections.IDictionary;
            if (gameDict == null || gameDict.Count == 0) { error = "no games running"; return null; }

            ulong dbId = ParseRef(playerDbId);

            foreach (var gameObj in gameDict.Values)
            {
                if (gameObj is not Game game) continue;

                if (dbId != 0)
                {
                    Player byDbId = game.EntityManager.GetEntityByDbGuid<Player>(dbId);
                    if (byDbId != null) return byDbId;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(playerName) == false && playerName != "*")
                {
                    Player byName = game.EntityManager.GetPlayerByName(playerName);
                    if (byName != null && byName.PlayerConnection != null) return byName;
                    continue;
                }

                // "*" or empty — first connected real player.
                foreach (Player candidate in new PlayerIterator(game))
                {
                    if (candidate?.PlayerConnection != null) return candidate;
                }
            }

            error = "player not found in any running game";
            return null;
        }
    }

    public class ItemCatalogResponse
    {
        public int TotalItems { get; set; }
        public List<string> Categories { get; set; }
        public List<ItemRarityEntry> Rarities { get; set; }
        public List<ItemCatalogEntry> Items { get; set; }
    }

    public class ItemRarityEntry
    {
        public string ProtoRef { get; set; }
        public string Name { get; set; }
        public int Tier { get; set; }
    }

    public class ItemCatalogEntry
    {
        public string ProtoRef { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public string Category { get; set; }
        public string Slot { get; set; }
        public string Avatar { get; set; }
        public string IconPath { get; set; }
        public bool IsUnique { get; set; }
    }
}
