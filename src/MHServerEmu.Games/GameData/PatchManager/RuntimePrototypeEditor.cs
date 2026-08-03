using System.Reflection;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.GameData.PatchManager
{
    /// <summary>
    /// Generic, sibling utility to <see cref="PrototypePatchManager"/> — reads and mutates fields on an
    /// ALREADY-LOADED <see cref="Prototype"/> at arbitrary runtime, not just during the one-time data-directory
    /// load. <see cref="PrototypePatchManager"/> can't be reused directly for this: its mutation logic is gated
    /// by a <c>_protoStack</c>/<c>_pathDict</c> pair built during <see cref="Prototype.PostProcess"/>'s one-time
    /// walk, and a call after startup silently no-ops. This class bypasses that machinery entirely and always
    /// resolves the live cached prototype fresh via <see cref="GameDatabase.GetPrototype{T}"/>.
    ///
    /// No new PrototypeId can ever be minted this way — <see cref="DataDirectory"/> has no runtime registration
    /// path, every valid ID already exists in the client-shipped data. What this DOES let an operator do is
    /// repurpose an already-loaded MetaGame/MetaState (or any other) prototype's fields in place. Because
    /// DataDirectory caches one Prototype instance per PrototypeId for the life of the process, a mutation here
    /// is immediately visible to every future caller referencing that ID — globally, with no revert path short
    /// of restarting the server. See the OmegaDev2 "Region Events" tool for the operator-facing warning.
    /// </summary>
    public static class RuntimePrototypeEditor
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public sealed class FieldSnapshot
        {
            public string FieldName { get; set; }
            public string FieldTypeName { get; set; }
            public bool IsArray { get; set; }
            public bool IsEditable { get; set; }
            public object Value { get; set; }
        }

        /// <summary>Reads every top-level field declared on the prototype's own concrete runtime type.</summary>
        public static List<FieldSnapshot> ReadAllFields(PrototypeId protoRef)
        {
            var result = new List<FieldSnapshot>();

            Prototype prototype = GameDatabase.GetPrototype<Prototype>(protoRef);
            if (prototype == null)
                return result;

            foreach (PropertyInfo prop in prototype.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue; // skip indexers

                object rawValue;
                try { rawValue = prop.GetValue(prototype); }
                catch { continue; }

                bool isArray = prop.PropertyType.IsArray;
                // Nested Prototype-typed fields (e.g. EvalCanActivate) are out of scope for v1 editing —
                // surface them as read-only/opaque instead of trying to recurse into arbitrary child prototypes.
                bool isEditable = IsEditableType(isArray ? prop.PropertyType.GetElementType() : prop.PropertyType);

                result.Add(new FieldSnapshot
                {
                    FieldName = prop.Name,
                    FieldTypeName = prop.PropertyType.Name,
                    IsArray = isArray,
                    IsEditable = isEditable,
                    Value = SummarizeValue(rawValue, isArray),
                });
            }

            return result;
        }

        /// <summary>Reads a single field's current value by dotted path (mirrors <see cref="PrototypePatchEntry.Path"/> syntax).</summary>
        public static bool TryReadField(PrototypeId protoRef, string path, out object value, out string error)
        {
            value = null;
            error = null;

            Prototype prototype = GameDatabase.GetPrototype<Prototype>(protoRef);
            if (prototype == null) { error = $"prototype {protoRef} not found"; return false; }

            PrototypePatchEntry.ParsePath(path, out _, out string fieldName, out bool isArray, out int arrayIndex);

            PropertyInfo prop = prototype.GetType().GetProperty(fieldName);
            if (prop == null) { error = $"field '{fieldName}' not found on {prototype.GetType().Name}"; return false; }

            object rawValue;
            try { rawValue = prop.GetValue(prototype); }
            catch (Exception ex) { error = $"failed to read '{fieldName}': {ex.Message}"; return false; }

            if (isArray && arrayIndex >= 0)
            {
                if (rawValue is not Array array || arrayIndex >= array.Length)
                { error = $"index {arrayIndex} out of range for '{fieldName}'"; return false; }
                value = array.GetValue(arrayIndex);
            }
            else
            {
                value = rawValue;
            }

            return true;
        }

        /// <summary>
        /// Writes a single field by dotted path, converting via the same <see cref="PrototypePatchManager.ConvertValue"/>
        /// the load-time patch system uses. Supports scalar fields, indexed array element writes ("Field[2]"), and
        /// array append ("Field[]" or a bare array field with no index — mirrors PrototypePatchEntry's ArrayValue/ArrayIndex convention).
        /// </summary>
        public static bool TryWriteField(PrototypeId protoRef, string path, ValueBase newValue, out object previousValue, out string error)
        {
            previousValue = null;
            error = null;

            Prototype prototype = GameDatabase.GetPrototype<Prototype>(protoRef);
            if (prototype == null) { error = $"prototype {protoRef} not found"; return false; }

            PrototypePatchEntry.ParsePath(path, out _, out string fieldName, out bool isArrayPath, out int arrayIndex);

            PropertyInfo prop = prototype.GetType().GetProperty(fieldName);
            if (prop == null) { error = $"field '{fieldName}' not found on {prototype.GetType().Name}"; return false; }

            try
            {
                if (isArrayPath)
                {
                    if (arrayIndex >= 0)
                    {
                        previousValue = ReadArrayElement(prop, prototype, arrayIndex);
                        SetIndexValue(prototype, prop, arrayIndex, newValue);
                    }
                    else
                    {
                        previousValue = prop.GetValue(prototype); // whole array, pre-append
                        InsertValue(prototype, prop, newValue);
                    }
                }
                else
                {
                    previousValue = prop.GetValue(prototype);
                    object converted = PrototypePatchManager.ConvertValue(newValue.GetValue(), prop.PropertyType);
                    prop.SetValue(prototype, converted);
                }
            }
            catch (Exception ex)
            {
                error = $"write failed: {ex.Message}";
                return false;
            }

            Logger.Info($"[RuntimePrototypeEditor] {protoRef.GetName()}.{path} = {newValue.GetValue()}");
            return true;
        }

        /// <summary>Reads a field off <paramref name="sourceProtoRef"/> and writes it onto <paramref name="targetProtoRef"/> at the given path(s).</summary>
        public static bool TryCloneField(PrototypeId sourceProtoRef, PrototypeId targetProtoRef, string sourcePath, string targetPath, out object previousValue, out string error)
        {
            previousValue = null;
            targetPath ??= sourcePath;

            if (TryReadField(sourceProtoRef, sourcePath, out object sourceValue, out error) == false)
                return false;

            ValueBase wrapped = WrapAsValueBase(sourceValue);
            if (wrapped == null)
            {
                error = $"source value for '{sourcePath}' has no supported wire representation (likely a nested Prototype-typed field — not supported for clone in v1)";
                return false;
            }

            return TryWriteField(targetProtoRef, targetPath, wrapped, out previousValue, out error);
        }

        private static object ReadArrayElement(PropertyInfo prop, Prototype prototype, int index)
        {
            if (prop.GetValue(prototype) is Array array && index < array.Length)
                return array.GetValue(index);
            return null;
        }

        // --- Array write helpers, replicated from PrototypePatchManager's private SetIndexValue/InsertValue.
        // Kept as a standalone copy rather than making those private methods shared — this class's lifecycle
        // (arbitrary runtime calls) is unrelated to the load-time-only patch stack those methods are coupled to.

        private static void SetIndexValue(Prototype prototype, PropertyInfo fieldInfo, int index, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not an array.");

            Array array = (Array)fieldInfo.GetValue(prototype);
            if (array == null || index < 0 || index >= array.Length)
                throw new IndexOutOfRangeException($"Invalid index {index} for array {fieldInfo.Name}.");

            Type elementType = fieldType.GetElementType();
            object converted = PrototypePatchManager.ConvertValue(value.GetValue(), elementType);
            array.SetValue(converted, index);
        }

        private static void InsertValue(Prototype prototype, PropertyInfo fieldInfo, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not an array.");

            Type elementType = fieldType.GetElementType();
            Array currentArray = (Array)fieldInfo.GetValue(prototype);
            int currentLength = currentArray?.Length ?? 0;

            Array newArray = Array.CreateInstance(elementType, currentLength + 1);
            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentLength);

            object converted = PrototypePatchManager.ConvertValue(value.GetValue(), elementType);
            newArray.SetValue(converted, currentLength);

            fieldInfo.SetValue(prototype, newArray);
        }

        private static bool IsEditableType(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return true;
            // PrototypeId/AssetId/LocaleStringId/PrototypeGuid etc. are all lightweight value-type wrappers
            // (not Prototype-derived reference types), so they're safe to edit as opaque scalars.
            if (typeof(Prototype).IsAssignableFrom(type)) return false;
            return type.IsValueType;
        }

        private static object SummarizeValue(object rawValue, bool isArray)
        {
            if (rawValue == null) return null;
            if (isArray && rawValue is Array array)
            {
                var items = new object[array.Length];
                for (int i = 0; i < array.Length; i++)
                    items[i] = SummarizeScalar(array.GetValue(i));
                return items;
            }
            return SummarizeScalar(rawValue);
        }

        /// <summary>
        /// Converts a single field value into something System.Text.Json can
        /// actually serialize. Confirmed live 2026-07-27 — the base
        /// Prototype.ClassType field (type System.Type) reached
        /// SendJsonAsync raw and threw NotSupportedException. A first attempt
        /// special-cased Type/MemberInfo/Delegate directly, but that only
        /// covers the crash observed at the TOP level of a prototype's own
        /// properties — a property whose return type is some OTHER complex
        /// object (e.g. PrototypeFieldInfo, which itself exposes a ClassType
        /// : Type property — GameData/PrototypeFieldInfo.cs:31) still passes
        /// that nested Type through untouched and crashes exactly the same
        /// way one level deeper. Rather than keep enumerating specific
        /// offending nested types one crash at a time, switched to a
        /// WHITELIST: only pass a value through as-is if it's a type we
        /// affirmatively know System.Text.Json can handle (primitives,
        /// string, enums, our known lightweight value-wrapper structs).
        /// Everything else — any unrecognized class or struct, however deep
        /// or whatever it contains — becomes value.ToString() instead. This
        /// can never crash on serialization since it never delegates to
        /// Json for an unknown type's own field graph.
        /// </summary>
        private static object SummarizeScalar(object value)
        {
            if (value == null) return null;
            if (value is Prototype p)
            {
                string name = p.DataRef != PrototypeId.Invalid ? GameDatabase.GetPrototypeName(p.DataRef) : null;
                return string.IsNullOrEmpty(name) == false ? $"<{p.GetType().Name}:{name}>" : $"<Prototype:{p.GetType().Name}>";
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum
                || type == typeof(PrototypeId) || type == typeof(AssetId)
                || type == typeof(PrototypeGuid) || type == typeof(LocaleStringId)
                || type == typeof(BlueprintId) || type == typeof(AssetTypeId))
                return value;

            return value.ToString();
        }

        private static ValueBase WrapAsValueBase(object value)
        {
            return value switch
            {
                null => null,
                string s => new SimpleValue<string>(s, ValueType.String),
                bool b => new SimpleValue<bool>(b, ValueType.Boolean),
                float f => new SimpleValue<float>(f, ValueType.Float),
                int i => new SimpleValue<int>(i, ValueType.Integer),
                PrototypeId id => new SimpleValue<PrototypeId>(id, ValueType.PrototypeId),
                PrototypeGuid guid => new SimpleValue<PrototypeGuid>(guid, ValueType.PrototypeGuid),
                LocaleStringId locale => new SimpleValue<LocaleStringId>(locale, ValueType.LocaleStringId),
                _ => null, // nested Prototype-typed / unsupported values — not cloneable in v1
            };
        }
    }
}
