using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Properties.Evals;

namespace MHServerEmu.Games.Entities.Avatars
{
    // Omega rarity gear set bonuses (1.53 only).
    //
    // How the data is wired, confirmed against 1.53 Calligraphy on 2026-08-02:
    //
    //   - 57 EquipmentSetPrototypes live under Entity/Items/EquipmentSets/OmegaSets/.
    //   - An item joins a set through an affix (Entity/Items/Affixes/ArmorAffixes/Omega/
    //     EquipmentSetAffixes/SetAffix*) whose Properties contain EquipmentSetLevel[<set>] = 1.
    //     58 such affixes exist. Equipped item properties are already aggregated onto the
    //     avatar by Agent.OnOtherEntityAddedToMyInventory via Properties.AddChildCollection,
    //     and EquipmentSetLevel aggregates with AggMethod Sum, so the avatar's set level is
    //     simply "how many equipped items carry that set's affix". That part already worked.
    //   - What was missing is anything that READS it. EquipmentSetPrototype, its Entries and
    //     the EquipmentSetLevel/EquipmentSetLevelBonus property enums all existed with zero
    //     consumers anywhere in the server, so no set bonus was ever granted.
    //
    //   - EquipmentSetPrototype.SetLevelToEntryIndex maps set level -> entry index. Every one
    //     of the 57 curves is identity-minus-one over the range 1..1000, while 52 sets have 5
    //     entries and 5 sets have 1, so the index MUST be clamped to the entry count or a
    //     one-entry set at level 3 would run off the end of the array.
    //
    //   - Entries are cumulative tiers, not a single selected tier. OmegaSetEnergyCrit100Pct
    //     grants DamagePctBonusForPowerKeyword[Energy] = 0.15 at BOTH entry 1 and entry 3;
    //     that only makes sense if they stack to 0.30, otherwise the 4-piece tier would be a
    //     no-op. So entries 0..index are all applied, and repeated properties are combined
    //     using the property's own AggMethod rather than overwriting.
    //
    // Bonuses are held in a dedicated child PropertyCollection so they can be swapped wholesale
    // without disturbing the item collections hanging off the same parent. Set entries commonly
    // contain ProcProp/ProcKeywordProp, so the collection goes through UpdateProcEffectPowers()
    // in the same order the equipment path uses: assign before attaching, detach before
    // unassigning.
#if GAME_VERSION_1_53
    public partial class Avatar
    {
        private static readonly Logger EquipmentSetLogger = LogManager.CreateLogger();

        private readonly PropertyCollection _equipmentSetProperties = new();
        private readonly Dictionary<PrototypeId, int> _equipmentSetTiers = new();
        private bool _updatingEquipmentSetBonuses;

        /// <summary>
        /// Recomputes which <see cref="EquipmentSetPrototype"/> bonus tiers this avatar qualifies
        /// for and applies them. Cheap to call repeatedly: it exits without touching anything when
        /// no set's tier actually changed.
        /// </summary>
        /// <param name="force">
        /// Rebuild even if the tiers are unchanged. Needed when entering the world, because proc
        /// powers cannot be assigned while out of world and would otherwise stay missing.
        /// </param>
        public void UpdateEquipmentSetBonuses(bool force = false)
        {
            // Guard against re-entry: applying the bonuses writes properties, and a set entry is
            // free to grant EquipmentSetLevelBonus, which would come straight back through
            // OnPropertyChange.
            if (_updatingEquipmentSetBonuses)
                return;

            using var tierHandle = DictionaryPool<PrototypeId, int>.Instance.Get(out Dictionary<PrototypeId, int> newTiers);
            GetEquipmentSetTiers(newTiers);

            if (force == false && TiersMatch(newTiers))
                return;

            _updatingEquipmentSetBonuses = true;

            try
            {
                // Detach before unassigning, mirroring Agent.OnOtherEntityRemovedFromMyInventory().
                // Guarded: the collection is not attached on the first pass, and RemoveFromParent
                // logs a Verify failure rather than returning quietly when it isn't a child.
                if (_equipmentSetProperties.IsChildOf(Properties))
                    _equipmentSetProperties.RemoveFromParent(Properties);

                UpdateProcEffectPowers(_equipmentSetProperties, false);
                _equipmentSetProperties.Clear();

                foreach (var kvp in newTiers)
                {
                    EquipmentSetPrototype setProto = kvp.Key.As<EquipmentSetPrototype>();
                    if (setProto?.Entries == null)
                        continue;

                    // Cumulative: every tier up to and including the qualifying one
                    for (int i = 0; i <= kvp.Value && i < setProto.Entries.Length; i++)
                        ApplyEquipmentSetEntry(setProto.Entries[i]);
                }

                // Assign before attaching, mirroring Agent.OnOtherEntityAddedToMyInventory()
                UpdateProcEffectPowers(_equipmentSetProperties, true);
                Properties.AddChildCollection(_equipmentSetProperties);

                _equipmentSetTiers.Clear();
                foreach (var kvp in newTiers)
                    _equipmentSetTiers[kvp.Key] = kvp.Value;
            }
            finally
            {
                _updatingEquipmentSetBonuses = false;
            }
        }

        /// <summary>
        /// Clears any applied set bonuses. Called on exiting the world so the proc powers granted
        /// by set entries are unassigned along with everything else.
        /// </summary>
        private void ClearEquipmentSetBonuses()
        {
            if (_equipmentSetTiers.Count == 0)
                return;

            if (_equipmentSetProperties.IsChildOf(Properties))
                _equipmentSetProperties.RemoveFromParent(Properties);

            UpdateProcEffectPowers(_equipmentSetProperties, false);
            _equipmentSetProperties.Clear();
            _equipmentSetTiers.Clear();
        }

        /// <summary>
        /// Fills <paramref name="tiers"/> with the highest qualifying entry index for each
        /// <see cref="EquipmentSetPrototype"/> this avatar has any level in.
        /// </summary>
        private void GetEquipmentSetTiers(Dictionary<PrototypeId, int> tiers)
        {
            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.EquipmentSetLevel))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId setProtoRef);
                if (setProtoRef == PrototypeId.Invalid)
                    continue;

                EquipmentSetPrototype setProto = setProtoRef.As<EquipmentSetPrototype>();
                if (setProto == null || setProto.Entries.IsNullOrEmpty())
                    continue;

                int setLevel = kvp.Value;
                setLevel += Properties[PropertyEnum.EquipmentSetLevelBonus, setProtoRef];
                if (setLevel <= 0)
                    continue;

                Curve curve = setProto.SetLevelToEntryIndex.AsCurve();
                if (curve == null)
                    continue;

                int entryIndex = curve.GetIntAt(Math.Clamp(setLevel, curve.MinPosition, curve.MaxPosition));

                // The curves run to set level 1000 while no set has more than five entries
                entryIndex = Math.Clamp(entryIndex, 0, setProto.Entries.Length - 1);

                tiers[setProtoRef] = entryIndex;
            }
        }

        private bool TiersMatch(Dictionary<PrototypeId, int> newTiers)
        {
            if (newTiers.Count != _equipmentSetTiers.Count)
                return false;

            foreach (var kvp in newTiers)
            {
                if (_equipmentSetTiers.TryGetValue(kvp.Key, out int existing) == false || existing != kvp.Value)
                    return false;
            }

            return true;
        }

        private void ApplyEquipmentSetEntry(EquipmentSetEntryPrototype entryProto)
        {
            if (entryProto?.Properties == null)
                return;

            using EvalContextData evalContext = ObjectPoolManager.Instance.Get<EvalContextData>();
            evalContext.SetReadOnlyVar_PropertyCollectionPtr(EvalContext.Entity, Properties);

            foreach (PropertySetEntryPrototype propEntry in entryProto.Properties)
            {
                if (propEntry == null)
                    continue;

                PropertyInfo propertyInfo = GameDatabase.PropertyInfoTable.LookupPropertyInfo(propEntry.Prop.Enum);
                if (propertyInfo == null)
                    continue;

                switch (propertyInfo.DataType)
                {
                    case PropertyDataType.Real:
                        float floatValue = propEntry.Value != null ? Eval.RunFloat(propEntry.Value, evalContext) : 0f;
                        _equipmentSetProperties[propEntry.Prop] = CombineFloat(
                            propertyInfo, _equipmentSetProperties[propEntry.Prop], floatValue,
                            _equipmentSetProperties.HasProperty(propEntry.Prop));
                        break;

                    case PropertyDataType.Integer:
                        int intValue = propEntry.Value != null ? Eval.RunInt(propEntry.Value, evalContext) : 0;
                        _equipmentSetProperties[propEntry.Prop] = CombineInt(
                            propertyInfo, _equipmentSetProperties[propEntry.Prop], intValue,
                            _equipmentSetProperties.HasProperty(propEntry.Prop));
                        break;

                    case PropertyDataType.Boolean:
                        _equipmentSetProperties[propEntry.Prop] = propEntry.Value == null || Eval.RunBool(propEntry.Value, evalContext);
                        break;

                    case PropertyDataType.Asset:
                        _equipmentSetProperties[propEntry.Prop] = propEntry.Value != null
                            ? Eval.RunAssetId(propEntry.Value, evalContext)
                            : AssetId.Invalid;
                        break;

                    default:
                        EquipmentSetLogger.Warn($"ApplyEquipmentSetEntry(): unsupported data type " +
                            $"{propertyInfo.DataType} for [{propertyInfo.PropertyName}] on [{this}]");
                        break;
                }
            }
        }

        /// <summary>
        /// Combines two values of the same property within this collection using the property's own
        /// aggregation method. Needed because tiers repeat properties deliberately (two +0.15 damage
        /// entries are meant to total +0.30), and writing the same <see cref="PropertyId"/> twice in
        /// one collection would otherwise just overwrite.
        /// </summary>
        private static float CombineFloat(PropertyInfo propertyInfo, float existing, float incoming, bool hasExisting)
        {
            if (hasExisting == false)
                return incoming;

            return propertyInfo.Prototype?.AggMethod switch
            {
                AggregationMethod.Sum => existing + incoming,
                AggregationMethod.Max => MathF.Max(existing, incoming),
                AggregationMethod.Min => MathF.Min(existing, incoming),
                AggregationMethod.Mul => existing * incoming,
                _ => incoming,
            };
        }

        private static int CombineInt(PropertyInfo propertyInfo, int existing, int incoming, bool hasExisting)
        {
            if (hasExisting == false)
                return incoming;

            return propertyInfo.Prototype?.AggMethod switch
            {
                AggregationMethod.Sum => existing + incoming,
                AggregationMethod.Max => Math.Max(existing, incoming),
                AggregationMethod.Min => Math.Min(existing, incoming),
                AggregationMethod.Mul => existing * incoming,
                _ => incoming,
            };
        }
    }
#endif
}
