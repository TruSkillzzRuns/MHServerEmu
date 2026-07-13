// OmegaDev2 Stash Manager endpoints.
//
//   GET  /webapi/inventory?player=*        — every item across the player's
//                                            general + stash inventories,
//                                            grouped by container
//   POST /webapi/inventory/delete          — { playerName, entityId } destroy
//                                            one item (game-thread marshaled,
//                                            ownership verified)
//
// Read-side runs on the game thread too — inventories and item entities are
// game state.

using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class InventoryListWebHandler : WebHandler
    {
        private const InventoryIterationFlags ContainerFlags =
            InventoryIterationFlags.PlayerGeneral |
            InventoryIterationFlags.PlayerGeneralExtra |
            InventoryIterationFlags.PlayerStashGeneral |
            InventoryIterationFlags.PlayerStashAvatarSpecific |
            InventoryIterationFlags.DeliveryBoxAndErrorRecovery;

        protected override async Task Get(WebRequestContext context)
        {
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var locale = MHServerEmu.Games.Locales.LocaleManager.Instance.CurrentLocale;
                var mgr = p.Game.EntityManager;
                var containers = new List<object>();
                int totalItems = 0;

                foreach (Inventory inventory in new InventoryIterator(p, ContainerFlags))
                {
                    var items = new List<object>();
                    foreach (var entry in inventory)
                    {
                        if (mgr.GetEntity<Item>(entry.Id) is not Item item) continue;

                        var itemProto = item.Prototype as ItemPrototype;
                        string path = GameDatabase.GetPrototypeName(item.PrototypeDataRef);

                        string displayName = null;
                        if (itemProto != null && itemProto.DisplayName != LocaleStringId.Invalid && locale != null)
                        {
                            displayName = locale.GetLocaleString(itemProto.DisplayName);
                            if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
                        }

                        PrototypeId rarityRef = item.ItemSpec?.RarityProtoRef ?? PrototypeId.Invalid;
                        int rarityTier = 0;
                        string rarityName = null;
                        if (rarityRef != PrototypeId.Invalid)
                        {
                            rarityName = LeafOf(GameDatabase.GetPrototypeName(rarityRef));
                            rarityTier = rarityRef.As<RarityPrototype>()?.Tier ?? 0;
                        }

                        AssetId iconAssetId = itemProto != null
                            ? (itemProto.IconPathHiRes != 0 ? itemProto.IconPathHiRes : itemProto.IconPath)
                            : 0;

                        items.Add(new
                        {
                            EntityId = $"0x{item.Id:X}",
                            ProtoRef = $"0x{(ulong)item.PrototypeDataRef:X16}",
                            Name = displayName ?? LeafOf(path),
                            Path = path,
                            Rarity = rarityName,
                            RarityTier = rarityTier,
                            Stack = Math.Max(1, item.CurrentStackSize),
                            Level = item.ItemSpec?.ItemLevel ?? 0,
                            Slot = entry.Slot,
                            IconPath = iconAssetId != 0 ? GameDatabase.GetAssetName(iconAssetId) : null,
                        });
                        totalItems++;
                    }

                    // Show every stash tab (even empty ones) but skip empty
                    // internal recovery containers to keep the UI clean.
                    var invProto = GameDatabase.GetPrototype<InventoryPrototype>(inventory.PrototypeDataRef);
                    bool isRecovery = invProto?.Category == InventoryCategory.None;
                    if (items.Count == 0 && isRecovery) continue;

                    containers.Add(new
                    {
                        Name = LeafOf(GameDatabase.GetPrototypeName(inventory.PrototypeDataRef)),
                        Category = invProto?.Category.ToString(),
                        Capacity = inventory.GetCapacity(),
                        Count = items.Count,
                        Items = items,
                    });
                }

                return new { Ok = true, Player = p.GetName(), TotalItems = totalItems, Containers = containers };
            });

            await context.SendJsonAsync(result);
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

    public class InventoryDeleteWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            ulong entityId = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("entityId", out var ei)) entityId = PhantomsWebUtil.ParseRef(ei.GetString());
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (entityId == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "entityId required" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var item = p.Game.EntityManager.GetEntity<Item>(entityId);
                if (item == null)
                    return new { Ok = false, Message = "item not found (already gone?)" };

                // Ownership check — never delete something that isn't in this
                // player's containment chain.
                if (item.GetOwnerOfType<Player>() != p)
                    return new { Ok = false, Message = "item does not belong to this player" };

                string name = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
                item.Destroy();
                Logger.Info($"[Inventory:Delete] {p.GetName()} deleted {name} (0x{entityId:X})");
                return new { Ok = true, Message = "item deleted" };
            });

            await context.SendJsonAsync(result);
        }
    }
}
