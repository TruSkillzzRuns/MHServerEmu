using System;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Achievements;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities.Persistence
{
    // Version-agnostic export/import snapshot DTOs. Every prototype/asset
    // reference is stored as a hex PrototypeGuid/AssetId.Guid string (stable
    // across game client versions, baked into the Calligraphy data itself —
    // see DataDirectory.GetPrototypeDataRefByGuid) instead of the raw
    // PrototypeId/AssetId numeric value (which is only stable within a
    // single loaded dataset and can differ between 1.48/1.52/1.53 builds).
    // Property NAMES are used instead of raw PropertyEnum ordinals for the
    // same reason -- PropertyEnum is a plain auto-incrementing enum with
    // dozens of #if GAME_VERSION_x blocks, so the same property can sit at
    // a different ordinal in each build. Parsing by name lets the .NET
    // runtime resolve the correct ordinal for whichever build is running.

    public class AccountMigrationSnapshot
    {
        public string SourceHeroName { get; set; }
        public List<EntitySnapshot> Avatars { get; set; } = new();
        public List<EntitySnapshot> TeamUps { get; set; } = new();
        public List<EntitySnapshot> Items { get; set; } = new();

        // The PLAYER entity's own Properties -- currency (Currency
        // property, one entry per currency type), and anything else that
        // lives on the account rather than any individual avatar/item.
        // Exported/imported the same way as any other entity's Properties.
        public EntitySnapshot Player { get; set; }

        // Hex PrototypeGuids of every stash-tab/general-extra inventory the
        // source player had actually purchased/unlocked (Player.UnlockInventory).
        // Items tagged with EntitySnapshot.StashInventoryGuid can only land
        // back in the matching tab on import if that tab has been unlocked
        // there first -- otherwise it doesn't exist as a runtime Inventory
        // at all yet, and the item falls back to base General.
        public List<string> UnlockedStashInventoryGuids { get; set; } = new();

        // Player.AvatarProperties -- a SEPARATE PropertyCollection from the
        // main Player.Properties (currency etc.), used for avatar-level
        // overrides (e.g. PropertyEnum.AllianceOverride) that get copied
        // onto whichever avatar is currently active. PropertyEnum-keyed,
        // same translation as everything else -- easy to miss since it's
        // not part of `Player` either the way Properties is exported.
        public List<PropertySnapshot> AvatarSharedProperties { get; set; } = new();

        // Player.AchievementState.AchievementProgressMap, keyed by the
        // achievement's own uint Id. Verified live 2026-07-28: the
        // AchievementInfoMap*.json data driving these ids is byte-identical
        // across all three deployed builds (1.48/1.52/1.53) -- it's server-
        // side data shared by every config, not per-version client content
        // -- so raw ids ARE safe to copy directly, unlike e.g. PropertyEnum
        // ordinals which really do shift per build.
        public List<AchievementProgressSnapshot> Achievements { get; set; } = new();

        // Player._badges (AvailableBadges) -- MHServerEmu-internal admin/CSR
        // flags, not client/Calligraphy content, so raw ints are stable
        // across builds (same enum declaration compiled into all three
        // configs). Stored as ints rather than the enum type so this DTO
        // doesn't need a reference to Player's namespace.
        public List<int> Badges { get; set; } = new();

        // Player._stashTabOptionsDict -- display name/color/sort order per
        // stash tab, keyed by that tab's own inventory PrototypeId (portable
        // via PrototypeGuid same as everything else). Only meaningful for
        // tabs that also appear in UnlockedStashInventoryGuids.
        public List<StashTabOptionsSnapshot> StashTabOptions { get; set; } = new();
    }

    public class AchievementProgressSnapshot
    {
        public uint AchievementId { get; set; }
        public uint Count { get; set; }
        public long CompletedDateTicks { get; set; }
    }

    public class StashTabOptionsSnapshot
    {
        public string InventoryGuid { get; set; }
        public string DisplayName { get; set; }
        public string IconPathAssetGuid { get; set; }
        public int SortOrder { get; set; }
        public int Color { get; set; }
    }

    public class EntitySnapshot
    {
        public string EntityTypeGuid { get; set; }    // hex PrototypeGuid of this entity's own type
        public string EntityTypeName { get; set; }     // human-readable only, never read back on import
        public List<PropertySnapshot> Properties { get; set; } = new();
        public ItemSpecSnapshot ItemSpec { get; set; }  // null for non-items

        // Items only -- which avatar this item was actually equipped on and
        // which of that avatar's equip-slot inventories (Weapon, Costume,
        // etc.) it sat in. Both null/empty means the item was loose in the
        // player's general/stash inventory, not equipped. Needed to put
        // equipped gear back on the hero on import instead of dumping it in
        // the stash -- see AccountMigrationImportWebHandler.
        public string EquippedOnAvatarGuid { get; set; }
        public string EquipInventoryGuid { get; set; }

        // Items only, non-equipped case -- which of the PLAYER's own
        // inventories (base General, General-extra bag, or a specific
        // numbered stash tab) this item actually sat in, so import can put
        // it back in the same tab instead of flattening every stashed item
        // into the base General inventory. Null/empty falls back to base
        // General on import (e.g. for items with no recorded origin).
        public string StashInventoryGuid { get; set; }
    }

    public class PropertySnapshot
    {
        public string PropertyName { get; set; }
        // Each entry is either a plain integer string (Integer-typed param)
        // or "guid:<hex>" (Prototype/Asset-typed param).
        public List<string> Params { get; set; } = new();
        // Plain number/bool string for scalar value types, or "guid:<hex>"
        // for Prototype/Asset-typed values.
        public string Value { get; set; }
    }

    public class ItemSpecSnapshot
    {
        public string ItemProtoGuid { get; set; }
        public string RarityProtoGuid { get; set; }
        public int ItemLevel { get; set; }
        public int CreditsAmount { get; set; }
        public int Seed { get; set; }
        public string EquippableByGuid { get; set; }
        public int StackCount { get; set; }
        public List<AffixSpecSnapshot> Affixes { get; set; } = new();
    }

    public class AffixSpecSnapshot
    {
        public string AffixProtoGuid { get; set; }
        public string ScopeProtoGuid { get; set; }
        public int Seed { get; set; }
    }

    /// <summary>
    /// Translates live entities to/from a version-agnostic snapshot, for
    /// moving an account's avatars/team-ups/items between servers built for
    /// different client versions (1.48/1.52/1.53). Never touches raw
    /// ArchiveData directly -- always goes through the entity's already-
    /// live, already-deserialized Properties/ItemSpec, and always resolves
    /// references by PrototypeGuid/property-name rather than raw numeric
    /// IDs, which is what makes this safe across builds where those numeric
    /// IDs can differ.
    /// </summary>
    public static class AccountMigrationUtility
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private const string GuidPrefix = "guid:";

        #region Export

        public static EntitySnapshot ExportEntity(Entity entity)
        {
            if (entity == null) return null;

            var snapshot = new EntitySnapshot
            {
                EntityTypeGuid = GuidToString(GameDatabase.GetPrototypeGuid(entity.PrototypeDataRef)),
                EntityTypeName = GameDatabase.GetPrototypeName(entity.PrototypeDataRef),
                Properties = ExportProperties(entity.Properties)
            };

            if (entity is Item item)
                snapshot.ItemSpec = ExportItemSpec(item.ItemSpec);

            return snapshot;
        }

        /// <summary>Exports every entry of an arbitrary PropertyCollection -- used both for entities (ExportEntity) and standalone collections that aren't tied to one, e.g. Player.AvatarProperties.</summary>
        public static List<PropertySnapshot> ExportProperties(PropertyCollection properties)
        {
            var result = new List<PropertySnapshot>();
            if (properties == null) return result;

            foreach (var kvp in properties)
            {
                PropertySnapshot propSnapshot;
                try
                {
                    propSnapshot = ExportProperty(kvp.Key, kvp.Value);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ExportProperties(): failed to export property '{kvp.Key.Enum}' - {ex.Message}");
                    continue;
                }

                if (propSnapshot != null)
                    result.Add(propSnapshot);
            }

            return result;
        }

        private static PropertySnapshot ExportProperty(PropertyId propertyId, PropertyValue value)
        {
            PropertyEnum propertyEnum = propertyId.Enum;
            PropertyInfo info = GameDatabase.PropertyInfoTable.LookupPropertyInfo(propertyEnum);
            if (info == null) return null;

            // Runtime-only reference types (live entity/region IDs) mean
            // nothing on a different server/session -- skip rather than
            // export a value that would point at garbage on import.
            if (info.DataType == PropertyDataType.EntityId || info.DataType == PropertyDataType.RegionId)
                return null;

            var snapshot = new PropertySnapshot { PropertyName = propertyEnum.ToString() };

            // ParamCount is trusted from PropertyInfo, but the underlying
            // storage (_paramTypes / PropertyId param slots) is hard-capped
            // at Property.MaxParamCount -- a PropertyInfo that hasn't been
            // fully loaded yet (e.g. a property that's simply never been
            // touched on this particular server instance) can report a
            // ParamCount inconsistent with that cap, which would throw
            // IndexOutOfRangeException below. Clamp defensively.
            int paramCount = Math.Min(info.ParamCount, Property.MaxParamCount);
            for (int i = 0; i < paramCount; i++)
            {
                PropertyParam param = propertyId.GetParam(i);
                switch (info.GetParamType(i))
                {
                    case PropertyParamType.Prototype:
                        Property.FromParam(propertyId, i, param, out PrototypeId protoParam);
                        snapshot.Params.Add(GuidPrefix + GuidToString(GameDatabase.GetPrototypeGuid(protoParam)));
                        break;
                    case PropertyParamType.Asset:
                        Property.FromParam(propertyId, i, param, out AssetId assetParam);
                        snapshot.Params.Add(GuidPrefix + AssetIdToGuidString(assetParam));
                        break;
                    default:
                        snapshot.Params.Add(((int)param).ToString());
                        break;
                }
            }

            switch (info.DataType)
            {
                case PropertyDataType.Prototype:
                    PrototypeId protoValue = value;
                    snapshot.Value = GuidPrefix + GuidToString(GameDatabase.GetPrototypeGuid(protoValue));
                    break;
                case PropertyDataType.Asset:
                    AssetId assetValue = value;
                    snapshot.Value = GuidPrefix + AssetIdToGuidString(assetValue);
                    break;
                case PropertyDataType.Boolean:
                    snapshot.Value = ((bool)value).ToString();
                    break;
                case PropertyDataType.Real:
                    snapshot.Value = ((float)value).ToString("R");
                    break;
                default:
                    // Integer, Curve, Time, Guid, Int21Vector3 -- stored raw.
                    snapshot.Value = ((long)value).ToString();
                    break;
            }

            return snapshot;
        }

        private static ItemSpecSnapshot ExportItemSpec(ItemSpec spec)
        {
            if (spec == null) return null;

            var snapshot = new ItemSpecSnapshot
            {
                ItemProtoGuid = GuidToString(GameDatabase.GetPrototypeGuid(spec.ItemProtoRef)),
                RarityProtoGuid = GuidToString(GameDatabase.GetPrototypeGuid(spec.RarityProtoRef)),
                ItemLevel = spec.ItemLevel,
                CreditsAmount = spec.CreditsAmount,
                Seed = spec.Seed,
                EquippableByGuid = GuidToString(GameDatabase.GetPrototypeGuid(spec.EquippableBy)),
                StackCount = spec.StackCount
            };

            foreach (AffixSpec affix in spec.AffixSpecs)
            {
                if (affix.AffixProto == null) continue;
                snapshot.Affixes.Add(new AffixSpecSnapshot
                {
                    AffixProtoGuid = GuidToString(GameDatabase.GetPrototypeGuid(affix.AffixProto.DataRef)),
                    ScopeProtoGuid = GuidToString(GameDatabase.GetPrototypeGuid(affix.ScopeProtoRef)),
                    Seed = affix.Seed
                });
            }

            return snapshot;
        }

        /// <summary>
        /// Achievement ids are raw uints assigned in AchievementInfoMap*.json,
        /// not PrototypeGuid-resolvable -- but that data file is server-side
        /// and shared by every build config (verified byte-identical across
        /// the deployed 1.48/1.52/1.53 output on 2026-07-28), so raw ids ARE
        /// safe to copy directly here, unlike PropertyEnum ordinals which
        /// really do shift per build.
        /// </summary>
        public static List<AchievementProgressSnapshot> ExportAchievements(AchievementState state)
        {
            var result = new List<AchievementProgressSnapshot>();
            if (state == null) return result;

            foreach (var kvp in state.AchievementProgressMap)
            {
                result.Add(new AchievementProgressSnapshot
                {
                    AchievementId = kvp.Key,
                    Count = kvp.Value.Count,
                    CompletedDateTicks = kvp.Value.CompletedDate.Ticks
                });
            }

            return result;
        }

        /// <summary>MHServerEmu-internal admin/CSR flags -- raw ints, safe across builds since AvailableBadges is a plain enum in this shared engine source, not client Calligraphy data.</summary>
        public static List<int> ExportBadges(IEnumerable<AvailableBadges> badges)
        {
            var result = new List<int>();
            if (badges == null) return result;

            foreach (AvailableBadges badge in badges)
                result.Add((int)badge);

            return result;
        }

        /// <summary>Stash tab display name/color/sort order, keyed by that tab's own inventory PrototypeId -- only meaningful for tabs also present in AccountMigrationSnapshot.UnlockedStashInventoryGuids.</summary>
        public static List<StashTabOptionsSnapshot> ExportStashTabOptions(IEnumerable<KeyValuePair<PrototypeId, StashTabOptions>> stashTabOptions)
        {
            var result = new List<StashTabOptionsSnapshot>();
            if (stashTabOptions == null) return result;

            foreach (var kvp in stashTabOptions)
            {
                result.Add(new StashTabOptionsSnapshot
                {
                    InventoryGuid = GuidToString(GameDatabase.GetPrototypeGuid(kvp.Key)),
                    DisplayName = kvp.Value.DisplayName,
                    IconPathAssetGuid = AssetIdToGuidString(kvp.Value.IconPathAssetId),
                    SortOrder = kvp.Value.SortOrder,
                    Color = (int)kvp.Value.Color
                });
            }

            return result;
        }

        #endregion

        #region Import

        /// <summary>
        /// Applies a snapshot's properties onto an already-created live
        /// entity (created via EntityManager.CreateEntity using the entity
        /// type resolved from EntityTypeGuid against THIS server's
        /// currently-loaded game data). Returns false if the entity's own
        /// type couldn't be resolved on this version at all -- caller
        /// should skip/report the whole entity in that case.
        ///
        /// NOTE: does NOT touch ItemSpec -- see BuildItemSpec below, which
        /// must run BEFORE entity creation instead.
        /// </summary>
        public static bool ImportEntity(Entity entity, EntitySnapshot snapshot)
        {
            if (entity == null || snapshot == null) return false;

            ImportProperties(entity.Properties, snapshot.Properties);

            return true;
        }

        /// <summary>Applies a list of exported properties onto an arbitrary live PropertyCollection -- used both for entities (ImportEntity) and standalone collections that aren't tied to one, e.g. Player.AvatarProperties.</summary>
        public static void ImportProperties(PropertyCollection properties, List<PropertySnapshot> propSnapshots)
        {
            if (properties == null || propSnapshots == null) return;

            foreach (PropertySnapshot propSnapshot in propSnapshots)
            {
                bool resolved;
                PropertyId propertyId;
                PropertyValue value;

                try
                {
                    resolved = TryImportProperty(propSnapshot, out propertyId, out value);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ImportProperties(): skipped property '{propSnapshot.PropertyName}' — threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (resolved == false)
                {
                    Logger.Warn($"ImportProperties(): skipped property '{propSnapshot.PropertyName}' — not resolvable on this version");
                    continue;
                }

                properties[propertyId] = value;
            }
        }

        /// <summary>
        /// Builds a fully-populated ItemSpec for a new item. ItemSpec's own
        /// fields (ItemProtoRef, CreditsAmount, Seed, EquippableBy,
        /// AffixSpecs) only have getters -- unlike Properties, there is no
        /// way to apply them to an already-created entity after the fact.
        /// Callers must build this BEFORE calling EntityManager.CreateEntity()
        /// and assign it to EntitySettings.ItemSpec. Returns null if the
        /// item type itself couldn't be resolved.
        /// </summary>
        public static ItemSpec BuildItemSpec(PrototypeId itemProtoRef, ItemSpecSnapshot snapshot)
        {
            if (itemProtoRef == PrototypeId.Invalid || snapshot == null) return null;

            TryResolveGuidRef(snapshot.RarityProtoGuid, out PrototypeId rarityRef);
            TryResolveGuidRef(snapshot.EquippableByGuid, out PrototypeId equippableByRef);

            return new ItemSpec(itemProtoRef, rarityRef, snapshot.ItemLevel, snapshot.CreditsAmount,
                BuildAffixes(snapshot.Affixes), snapshot.Seed, equippableByRef)
            {
                StackCount = Math.Max(1, snapshot.StackCount)
            };
        }

        /// <summary>Hex PrototypeGuid string for a live PrototypeId, or null if Invalid. For tagging exported entities (e.g. which avatar/inventory an item was equipped in) with references the WebFrontend export handler doesn't otherwise touch.</summary>
        public static string ExportPrototypeRef(PrototypeId id) =>
            id == PrototypeId.Invalid ? null : GuidToString(GameDatabase.GetPrototypeGuid(id));

        /// <summary>Resolves a hex PrototypeGuid string exported via ExportPrototypeRef back to a PrototypeId on this server. Returns false if unresolvable on this version.</summary>
        public static bool TryImportPrototypeRef(string guidString, out PrototypeId id)
        {
            id = PrototypeId.Invalid;
            if (string.IsNullOrEmpty(guidString)) return false;
            return TryResolveGuidRef(guidString, out id) && id != PrototypeId.Invalid;
        }

        /// <summary>Resolves EntityTypeGuid against this server's currently-loaded data. Invalid if the content doesn't exist on this version.</summary>
        public static PrototypeId ResolveEntityType(EntitySnapshot snapshot)
        {
            if (snapshot == null || TryParseGuid(snapshot.EntityTypeGuid, out PrototypeGuid guid) == false)
                return PrototypeId.Invalid;

            return GameDatabase.GetDataRefByPrototypeGuid(guid);
        }

        private static bool TryImportProperty(PropertySnapshot propSnapshot, out PropertyId propertyId, out PropertyValue value)
        {
            propertyId = default;
            value = default;

            if (Enum.TryParse(propSnapshot.PropertyName, out PropertyEnum propertyEnum) == false)
                return false; // property no longer exists by this name on this version

            PropertyInfo info = GameDatabase.PropertyInfoTable.LookupPropertyInfo(propertyEnum);
            if (info == null) return false;

            // See the matching comment in ExportProperty() -- clamp to the
            // real storage cap rather than trusting ParamCount directly.
            int paramCount = Math.Min(info.ParamCount, Property.MaxParamCount);
            var paramValues = new PropertyParam[paramCount];
            for (int i = 0; i < paramCount && i < propSnapshot.Params.Count; i++)
            {
                string raw = propSnapshot.Params[i];
                switch (info.GetParamType(i))
                {
                    case PropertyParamType.Prototype:
                        if (TryResolveGuidRef(raw, out PrototypeId protoParam) == false) return false;
                        paramValues[i] = Property.ToParam(propertyEnum, i, protoParam);
                        break;
                    case PropertyParamType.Asset:
                        if (TryResolveAssetGuidRef(raw, out AssetId assetParam) == false) return false;
                        paramValues[i] = Property.ToParam(assetParam);
                        break;
                    default:
                        if (int.TryParse(raw, out int intParam) == false) return false;
                        paramValues[i] = (PropertyParam)intParam;
                        break;
                }
            }

            propertyId = new PropertyId(propertyEnum, paramValues);

            switch (info.DataType)
            {
                case PropertyDataType.Prototype:
                    if (TryResolveGuidRef(propSnapshot.Value, out PrototypeId protoValue) == false) return false;
                    value = protoValue;
                    break;
                case PropertyDataType.Asset:
                    if (TryResolveAssetGuidRef(propSnapshot.Value, out AssetId assetValue) == false) return false;
                    value = assetValue;
                    break;
                case PropertyDataType.Boolean:
                    if (bool.TryParse(propSnapshot.Value, out bool boolValue) == false) return false;
                    value = boolValue;
                    break;
                case PropertyDataType.Real:
                    if (float.TryParse(propSnapshot.Value, out float floatValue) == false) return false;
                    value = floatValue;
                    break;
                default:
                    if (long.TryParse(propSnapshot.Value, out long longValue) == false) return false;
                    value = longValue;
                    break;
            }

            return true;
        }

        private static IEnumerable<AffixSpec> BuildAffixes(List<AffixSpecSnapshot> affixSnapshots)
        {
            foreach (AffixSpecSnapshot affixSnapshot in affixSnapshots)
            {
                if (TryResolveGuidRef(affixSnapshot.AffixProtoGuid, out PrototypeId affixRef) == false)
                {
                    Logger.Warn($"ImportItemSpec(): skipped affix — not resolvable on this version (guid={affixSnapshot.AffixProtoGuid})");
                    continue;
                }

                var affixProto = affixRef.As<GameData.Prototypes.AffixPrototype>();
                if (affixProto == null) continue;

                TryResolveGuidRef(affixSnapshot.ScopeProtoGuid, out PrototypeId scopeRef);
                yield return new AffixSpec(affixProto, scopeRef, affixSnapshot.Seed);
            }
        }

        /// <summary>Applies exported achievement progress directly -- ids are raw uints, verified safe to copy as-is (see ExportAchievements).</summary>
        public static void ImportAchievements(AchievementState state, List<AchievementProgressSnapshot> snapshots)
        {
            if (state == null || snapshots == null) return;

            foreach (AchievementProgressSnapshot snap in snapshots)
            {
                var progress = new AchievementProgress(snap.Count, new TimeSpan(snap.CompletedDateTicks));
                state.SetAchievementProgress(snap.AchievementId, progress);
            }
        }

        /// <summary>Grants every exported badge directly -- raw ints are safe to copy as-is (see ExportBadges). Invalid/out-of-range values are skipped rather than cast into garbage.</summary>
        public static void ImportBadges(Player player, List<int> badges)
        {
            if (player == null || badges == null) return;

            foreach (int badge in badges)
            {
                if (badge <= 0 || badge >= (int)AvailableBadges.NumberOfBadges)
                    continue;

                player.AddBadge((AvailableBadges)badge);
            }
        }

        /// <summary>Restores stash tab display name/color/sort for tabs that resolve on this version. Skips (not error) for tabs not resolvable here.</summary>
        public static void ImportStashTabOptions(Player player, List<StashTabOptionsSnapshot> snapshots)
        {
            if (player == null || snapshots == null) return;

            foreach (StashTabOptionsSnapshot snap in snapshots)
            {
                if (TryResolveGuidRef(snap.InventoryGuid, out PrototypeId invRef) == false || invRef == PrototypeId.Invalid)
                    continue;

                TryResolveAssetGuidRef(snap.IconPathAssetGuid, out AssetId iconAssetId);

                player.SetStashTabOptionsDirect(invRef, new StashTabOptions
                {
                    DisplayName = snap.DisplayName,
                    IconPathAssetId = iconAssetId,
                    SortOrder = snap.SortOrder,
                    Color = (StashTabColor)snap.Color
                });
            }
        }

        #endregion

        #region Guid helpers

        private static string GuidToString(PrototypeGuid guid) => ((ulong)guid).ToString("X16");

        private static bool TryParseGuid(string s, out PrototypeGuid guid)
        {
            guid = PrototypeGuid.Invalid;
            if (string.IsNullOrEmpty(s)) return false;
            if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong raw) == false) return false;
            guid = (PrototypeGuid)raw;
            return true;
        }

        private static bool TryResolveGuidRef(string value, out PrototypeId protoRef)
        {
            protoRef = PrototypeId.Invalid;
            if (string.IsNullOrEmpty(value)) return true; // Invalid ref is a legitimate value for many properties

            string hex = value.StartsWith(GuidPrefix) ? value[GuidPrefix.Length..] : value;
            if (TryParseGuid(hex, out PrototypeGuid guid) == false) return false;
            if (guid == PrototypeGuid.Invalid) return true;

            protoRef = GameDatabase.GetDataRefByPrototypeGuid(guid);
            return protoRef != PrototypeId.Invalid;
        }

        private static string AssetIdToGuidString(AssetId assetId)
        {
            if (assetId == AssetId.Invalid) return string.Empty;
            var assetType = MHServerEmu.Games.GameData.Calligraphy.AssetDirectory.Instance.GetAssetType(assetId);
            if (assetType == null) return string.Empty;

            AssetGuid guid = assetType.GetAssetGuid(assetId);
            return ((ulong)guid).ToString("X16");
        }

        private static bool TryResolveAssetGuidRef(string value, out AssetId assetId)
        {
            assetId = AssetId.Invalid;
            if (string.IsNullOrEmpty(value)) return true;

            string hex = value.StartsWith(GuidPrefix) ? value[GuidPrefix.Length..] : value;
            if (string.IsNullOrEmpty(hex)) return true;

            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong raw) == false) return false;
            AssetGuid guid = (AssetGuid)raw;
            if (guid == AssetGuid.Invalid) return true;

            assetId = MHServerEmu.Games.GameData.Calligraphy.AssetDirectory.Instance.GetAssetRef(guid);
            return assetId != AssetId.Invalid;
        }

        #endregion
    }
}
