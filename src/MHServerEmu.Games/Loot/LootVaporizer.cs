using Gazillion;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Tables;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Loot
{
    /// <summary>
    /// Helper class for loot vaporization. Vaporization converts loot to credits or PetTech experience before it drops.
    /// </summary>
    public static class LootVaporizer
    {
        /// <summary>
        /// Returns <see langword="true"/> if the provided <see cref="LootResult"/> should be vaporized.
        /// </summary>
        public static bool ShouldVaporizeLootResult(Player player, in LootResult lootResult, PrototypeId avatarProtoRef)
        {
            if (player == null)
                return false;

            if (LiveTuningManager.GetLiveGlobalTuningVar(GlobalTuningVar.eGTV_LootVaporizationEnabled) == 0f)
                return false;

            switch (lootResult.Type)
            {
                case LootType.Item:
                    // Med Kits have their own "Enable Auto-Looting of Med Kits" option
                    // (GameplayOptionSetting.EnableAutoLootMedKits, confirmed live
                    // 2026-07-30) -- checked before the armor-only path below, since
                    // a Med Kit is never an ArmorPrototype and would otherwise just
                    // fall through to "not vaporized".
                    if (IsMedKitItem(lootResult.ItemSpec))
                    {
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                        return player.GameplayOptions.GetOptionSetting(GameplayOptionSetting.EnableAutoLootMedKits) == 1;
#else
                        return player.GameplayOptions.GetOptionSetting(GameplayOptionSetting.EnableAutoLootMedKits);
#endif
                    }

                    // Only armor slots should be vaporized
                    ArmorPrototype armorProto = lootResult.ItemSpec?.ItemProtoRef.As<ArmorPrototype>();
                    if (armorProto == null)
                        return false;

                    AvatarPrototype avatarProto = avatarProtoRef.As<AvatarPrototype>();
                    if (!Verify.IsNotNull(avatarProto)) return false;

                    EquipmentInvUISlot slot = GameDataTables.Instance.EquipmentSlotTable.EquipmentUISlotForAvatar(armorProto, avatarProto);
                    PrototypeId vaporizeThresholdRarityProtoRef = player.GameplayOptions.GetArmorRarityVaporizeThreshold(slot);
                    if (vaporizeThresholdRarityProtoRef == PrototypeId.Invalid)
                        return false;

                    RarityPrototype rarityProto = lootResult.ItemSpec.RarityProtoRef.As<RarityPrototype>();
                    if (!Verify.IsNotNull(rarityProto)) return false;

                    RarityPrototype vaporizeThresholdRarityProto = vaporizeThresholdRarityProtoRef.As<RarityPrototype>();
                    if (!Verify.IsNotNull(vaporizeThresholdRarityProto)) return false;

                    return rarityProto.Tier <= vaporizeThresholdRarityProto.Tier;

                case LootType.Credits:
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                    return player.GameplayOptions.GetOptionSetting(GameplayOptionSetting.EnableVaporizeCredits) == 1;
#else
                    return player.GameplayOptions.GetOptionSetting(GameplayOptionSetting.EnableVaporizeCredits);
#endif

                default:
                    return false;
            }
        }

        /// <summary>
        /// Finalizes vaporization of <see cref="LootResult"/> instances contained in the provided <see cref="LootResultSummary"/>.
        /// </summary>
        public static bool VaporizeLootResultSummary(Player player, LootResultSummary lootResultSummary, ulong sourceEntityId)
        {
            if (player == null)
                return false;

            List<ItemSpec> vaporizedItemSpecs = lootResultSummary.VaporizedItemSpecs;
            List<int> vaporizedCredits = lootResultSummary.VaporizedCredits;

            if (vaporizedItemSpecs.Count > 0 || vaporizedCredits.Count > 0)
            {
                NetMessageVaporizedLootResult.Builder resultMessageBuilder = NetMessageVaporizedLootResult.CreateBuilder();
                
                foreach (ItemSpec itemSpec in vaporizedItemSpecs)
                {
                    VaporizeItemSpec(player, itemSpec);
                    resultMessageBuilder.AddItems(NetStructVaporizedItem.CreateBuilder()
                        .SetItemProtoId((ulong)itemSpec.ItemProtoRef)
                        .SetRarityProtoId((ulong)itemSpec.RarityProtoRef));
                }

                foreach (int credits in vaporizedCredits)
                {
                    player.AcquireCredits(credits);
                    resultMessageBuilder.AddItems(NetStructVaporizedItem.CreateBuilder()
                        .SetCredits(credits));
                }

                resultMessageBuilder.SetSourceEntityId(sourceEntityId);
                player.SendMessage(resultMessageBuilder.Build());
            }

            return lootResultSummary.ItemSpecs.Count > 0 || lootResultSummary.AgentSpecs.Count > 0 || lootResultSummary.Credits.Count > 0 || lootResultSummary.Currencies.Count > 0;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided <see cref="ItemSpec"/> refers to a Med Kit
        /// (tagged via <see cref="GlobalsPrototype.MedKitKeyword"/> on the client's own item data).
        /// </summary>
        private static bool IsMedKitItem(ItemSpec itemSpec)
        {
            ItemPrototype itemProto = itemSpec?.ItemProtoRef.As<ItemPrototype>();
            if (itemProto == null) return false;

            KeywordPrototype medKitKeyword = GameDatabase.KeywordGlobalsPrototype.MedKitKeyword;
            return medKitKeyword != null && itemProto.HasKeyword(medKitKeyword);
        }

        /// <summary>
        /// Creates a real <see cref="Item"/> entity from the provided <see cref="ItemSpec"/> and adds it
        /// directly to the player's general inventory -- used for Med Kit auto-loot, which is meant to
        /// keep the usable item (unlike armor vaporization, which converts to credits/Pet Tech XP).
        /// </summary>
        private static bool GiveItemDirectly(Player player, ItemSpec itemSpec)
        {
            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
            settings.EntityRef = itemSpec.ItemProtoRef;
            settings.ItemSpec = itemSpec;

            Item item = player.Game.EntityManager.CreateEntity(settings) as Item;
            if (!Verify.IsNotNull(item, $"Failed to create auto-looted Med Kit item, aborting\nItemSpec: {itemSpec}"))
                return false;

            item.Properties[PropertyEnum.InventoryStackCount] = itemSpec.StackCount;

            InventoryResult result = player.AcquireItem(item, PrototypeId.Invalid);
            if (result != InventoryResult.Success)
            {
                item.Destroy();
                return false;
            }

            return true;
        }

        private static bool VaporizeItemSpec(Player player, ItemSpec itemSpec)
        {
            Avatar avatar = player.CurrentAvatar;
            if (!Verify.IsNotNull(avatar)) return false;

            ItemPrototype itemProto = itemSpec.ItemProtoRef.As<ItemPrototype>();
            if (!Verify.IsNotNull(itemProto)) return false;

            // Med Kits get added straight to the player's inventory instead of being
            // donated to Pet Tech or sold -- "Enable Auto-Looting of Med Kits" is
            // meant to keep the usable item, not liquidate it like armor vaporization does.
            if (IsMedKitItem(itemSpec))
                return GiveItemDirectly(player, itemSpec);

            // Donate to PetTech if possible
            Inventory petItemInv = avatar.GetInventory(InventoryConvenienceLabel.PetItem);
            if (!Verify.IsNotNull(petItemInv)) return false;

            Item petTechItem = player.Game.EntityManager.GetEntity<Item>(petItemInv.GetEntityInSlot(0));
            if (petTechItem != null)
                return ItemPrototype.DonateItemToPetTech(player, petTechItem, itemSpec);

            // Fall back to credits
            int sellPrice = itemProto.Cost.GetNoStackSellPriceInCredits(player, itemSpec, null) * itemSpec.StackCount;
            int vaporizeCredits = MathHelper.RoundUpToInt(sellPrice * (float)avatar.Properties[PropertyEnum.VaporizeSellPriceMultiplier]);

            // Vaporization appears to be giving more credits than vacuuming, is this intended? To compensate for the lack of affixes?
            vaporizeCredits += Math.Max(MathHelper.RoundUpToInt(sellPrice * (float)avatar.Properties[PropertyEnum.PetTechDonationMultiplier]), 1);

            player.AcquireCredits(vaporizeCredits);
            player.OnScoringEvent(new(ScoringEventType.ItemCollected, itemSpec.ItemProtoRef.As<Prototype>(), itemSpec.RarityProtoRef.As<Prototype>(), itemSpec.StackCount));
            return true;
        }
    }
}
