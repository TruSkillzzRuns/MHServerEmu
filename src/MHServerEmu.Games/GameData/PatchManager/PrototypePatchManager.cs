using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private Stack<PrototypeId> _protoStack = new();
        private readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();

        // Owner is the DataRef of the true top-level prototype this nested
        // entry's path is relative to, captured at SetPath/SetPathIndex time
        // by walking the real object tree — NOT read from _protoStack at
        // PostOverride time. See PostOverride's nested-prototype branch for
        // why this matters (2026-07-28 Discord report: a patch meant for
        // InfinityOrbsAgeOfUltronTable was instead applied to an unrelated
        // RelicBoxOFTHTable, which then propagated to every RelicBox*
        // variant that uses it as a parent).
        private Dictionary<Prototype, (PrototypeId Owner, string Path)> _pathDict = new();
        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        /// <summary>
        /// Loads patches before Globals are loaded.
        /// </summary>
        public void PreInitialize(bool enablePatchManager)
        {
            if (enablePatchManager) _initialized |= LoadPatchDataFromDisk("PrePatchData");
        }

        /// <summary>
        /// Loads patches after Globals are loaded.
        /// </summary>
        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager) _initialized |= LoadPatchDataFromDisk("PatchData");
        }

        private bool LoadPatchDataFromDisk(string prefix)
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (Directory.Exists(patchDirectory) == false)
                return Logger.WarnReturn(false, "LoadPatchDataFromDisk(): Game data directory not found");

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            // Read all .json files that start with the specified prefix
            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, prefix, "json"))
            {
                string fileName = Path.GetFileName(filePath);

                PrototypePatchEntry[] updateValues = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                if (updateValues == null)
                {
                    Logger.Warn($"LoadPatchDataFromDisk(): Failed to parse {fileName}, skipping");
                    continue;
                }

                foreach (PrototypePatchEntry value in updateValues)
                {
                    if (value.Enabled == false) continue;
                    PrototypeId prototypeId = GameDatabase.GetPrototypeRefByName(value.Prototype);
                    if (prototypeId == PrototypeId.Invalid) continue;
                    AddPatchValue(prototypeId, value);
                    count++;
                }

                Logger.Trace($"Parsed patch data from {fileName}");
            }

            if (count == 0)
                return false;

            Logger.Info($"Loaded {count} {prefix} patches");
            return true;
        }

        private void AddPatchValue(PrototypeId prototypeId, in PrototypePatchEntry value)
        {
            if (_patchDict.TryGetValue(prototypeId, out var patchList) == false)
            {
                patchList = [];
                _patchDict[prototypeId] = patchList;
            }
            patchList.Add(value);
        }

        public bool CheckProperties(PrototypeId protoRef, out Properties.PropertyCollection prop)
        {
            prop = null;
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
                foreach (var entry in list)
                    if (entry.Value.ValueType == ValueType.Properties)
                    {
                        prop = entry.Value.GetValue() as Properties.PropertyCollection;
                        return prop != null;
                    }

            return false;
        }

        public bool PreCheck(PrototypeId protoRef)
        {
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
            {
                if (NotPatched(list))
                    _protoStack.Push(protoRef);
            }

            return _protoStack.Count > 0;
        }

        private static bool NotPatched(List<PrototypePatchEntry> list)
        {
            foreach (var entry in list)
                if (entry.Patched == false) return true;
            return false;
        }

        public void PostOverride(Prototype prototype)
        {
            if (_protoStack.Count == 0) return;

            PrototypeId patchProtoRef;
            string currentPath;

            if (prototype.DataRef != PrototypeId.Invalid)
            {
                // Top-level prototype — must match whatever the stack
                // currently thinks is the active patch target.
                patchProtoRef = _protoStack.Peek();
                if (prototype.DataRef != patchProtoRef) return;
                if (_patchDict.ContainsKey(prototype.DataRef))
                    _protoStack.Pop();
                currentPath = string.Empty;
            }
            else
            {
                // Nested/embedded prototype (mixin, loot table entry, etc.)
                // — resolve its patch target from the TRUE ownership chain
                // recorded at SetPath/SetPathIndex time, not from
                // _protoStack.Peek(). Blindly trusting the top of a single
                // global stack here is what let an unrelated sibling
                // prototype's patch bleed onto this one whenever the real
                // target hadn't been popped yet (see field comment above
                // _pathDict) — two structurally similar tables (e.g. two
                // loot tables with a parallel ".Choices[n]" shape) could
                // coincidentally share a ClearPath string, and without an
                // ownership check the stuck patch would apply to whichever
                // one happened to be loading while it sat on the stack.
                if (_pathDict.TryGetValue(prototype, out var entry) == false) return;
                if (entry.Owner == PrototypeId.Invalid) return;

                patchProtoRef = entry.Owner;
                currentPath = entry.Path;
            }

            if (_patchDict.TryGetValue(patchProtoRef, out var list) == false) return;

            foreach (var patchEntry in list)
                if (patchEntry.Patched == false)
                    CheckAndUpdate(patchEntry, prototype, currentPath);

            if (_protoStack.Count == 0)
                _pathDict.Clear();
        }

        private static bool CheckAndUpdate(PrototypePatchEntry entry, Prototype prototype, string currentPath)
        {
            if (currentPath.StartsWith('.')) currentPath = currentPath[1..];
            if (entry.СlearPath != currentPath) return false;

            var fieldInfo = prototype.GetType().GetProperty(entry.FieldName);
            if (fieldInfo == null)
            {
                Logger.Warn($"CheckAndUpdate: {entry.FieldName} not found for {entry.Prototype}");
                return false;
            }

            UpdateValue(prototype, fieldInfo, entry);
            Logger.Trace($"Patch Prototype: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()}");

            return true;
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            if (targetType.IsInstanceOfType(rawValue))
                return rawValue;

            if (targetType.IsSubclassOf(typeof(Prototype)))
            {
                PrototypeId? protoRef = null;
                switch (rawValue)
                {
                    case PrototypeId protoId:   protoRef = protoId; break;
                    case ulong dataId:          protoRef = (PrototypeId)dataId; break;
                }

                if (protoRef.HasValue)
                {
                    PatchContext contextBefore = Instance.CreateSubContext();

                    Prototype proto = GameDatabase.GetPrototype<Prototype>(protoRef.Value);

                    Instance.RestoreContext(contextBefore);

                    return proto;
                }
            }

            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                return converter.ConvertFrom(rawValue);

            // Confirmed live 2026-07-27 — Convert.ChangeType refuses a direct
            // enum-to-enum conversion (e.g. PrototypeGuid -> AssetId), even
            // though every one of this engine's opaque ID wrapper types
            // (PrototypeId/AssetId/AssetTypeId/BlueprintId/PrototypeGuid/
            // LocaleStringId) is just a ulong underneath ("Invalid cast from
            // 'PrototypeGuid' to 'AssetId'"). Route through the underlying
            // numeric value first so any of these wrapper types can convert
            // into any other — needed to patch e.g. a WorldEntityPrototype's
            // UnrealClass (AssetId) field from a plain numeric input.
            if (rawValue is Enum && targetType.IsEnum)
                return Enum.ToObject(targetType, Convert.ToUInt64(rawValue));

            return Convert.ChangeType(rawValue, targetType);
        }

        private static void UpdateValue(Prototype prototype, PropertyInfo fieldInfo, PrototypePatchEntry entry)
        {
            try
            {
                Type fieldType = fieldInfo.PropertyType;
                if (entry.ArrayValue)
                {
                    if (entry.ArrayIndex != -1)
                        SetIndexValue(prototype, fieldInfo, entry.ArrayIndex, entry.Value);
                    else
                        InsertValue(prototype, fieldInfo, entry.Value);
                }
                else
                {
                    object convertedValue = ConvertValue(entry.Value.GetValue(), fieldType);
                    fieldInfo.SetValue(prototype, convertedValue);
                }
                entry.Patched = true;
            }
            catch (PrototypeRefNotFoundException ex)
            {
                Logger.Trace($"Skipped UpdateValue: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed UpdateValue: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
            }
        }

        // Thrown by GetElementValue() when a patch/tuning entry references a
        // PrototypeId that doesn't resolve on this game version -- an expected,
        // non-error skip (see GetElementValue), distinct from a genuine failure.
        private class PrototypeRefNotFoundException : Exception
        {
            public PrototypeRefNotFoundException(PrototypeId dataRef)
                : base($"DataRef {dataRef} does not exist on this game version") { }
        }

        private static void SetIndexValue(Prototype prototype, PropertyInfo fieldInfo, int index, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");

            Array array = (Array)fieldInfo.GetValue(prototype);
            if (array == null || index < 0 || index >= array.Length)
                throw new IndexOutOfRangeException($"Invalid index {index} for array {fieldInfo.Name}.");

            object valueEntry = value.GetValue();

            var entryType = valueEntry.GetType();
            Type elementType = fieldType.GetElementType();

            if (elementType == null || IsTypeCompatible(elementType, entryType, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable to {elementType?.Name}.");

            object converted = ConvertValue(valueEntry, elementType);
            array.SetValue(converted, index);
        }

        private static void InsertValue(Prototype prototype, PropertyInfo fieldInfo, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");

            var valueEntry = value.GetValue();

            var entryType = valueEntry.GetType();
            Type elementType = fieldType.GetElementType();

            if (elementType == null || IsTypeCompatible(elementType, entryType, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable for {elementType?.Name}.");

            var currentArray = (Array)fieldInfo.GetValue(prototype);

            int newLength = CalcNewLength(currentArray, valueEntry);
            var newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentArray.Length);

            AddElements(newArray, elementType, valueEntry, currentArray.Length);

            fieldInfo.SetValue(prototype, newArray);
        }

        private static int CalcNewLength(Array currentArray, object valueEntry)
        {
            int currentLength = currentArray?.Length ?? 0;
            int valuesCount = 1;
            if (valueEntry is Array array)
            {
                int length = array.Length;
                if (length > 1) valuesCount = length;
            }
            return currentLength + valuesCount;
        }

        private static bool IsTypeCompatible(Type baseType, Type entryType, ValueType valueType)
        {
            if (entryType.IsArray) entryType = entryType.GetElementType();
            if (valueType == ValueType.PrototypeDataRef || valueType == ValueType.PrototypeDataRefArray)
                entryType = typeof(Prototype);
            return baseType.IsAssignableFrom(entryType) || entryType.IsAssignableFrom(baseType);
        }

        private static void AddElements(Array newArray, Type elementType, object valueEntry, int lastIndex)
        {
            if (valueEntry is Array array)
            {
                foreach (var entry in array)
                {
                    object elementValue = GetElementValue(entry, elementType);
                    newArray.SetValue(elementValue, lastIndex++);
                }
            }
            else
            {
                object elementValue = GetElementValue(valueEntry, elementType);
                newArray.SetValue(elementValue, lastIndex);
            }
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
            if (elementType.IsClass && valueEntry is PrototypeId dataRef)
            {
                // Patch/tuning data is authored against 1.52's content set, which
                // is a superset of what 1.48/1.53 ship -- a ref that doesn't
                // resolve on this version is an expected skip, not an error.
                // MUST still throw (not return null) here: this value is about
                // to be written into an array slot by AddElements/InsertValue,
                // and a null entry surviving into that array crashes downstream
                // code (e.g. LootTablePrototype.PostProcess() iterating Choices[]
                // with no null guard) instead of the whole patch entry being
                // cleanly dropped the way UpdateValue's catch expects. Use a
                // dedicated exception type so UpdateValue can log this known,
                // expected case at Trace instead of as a WarnException.
                if (GameDatabase.PrototypeExists(dataRef) == false)
                    throw new PrototypeRefNotFoundException(dataRef);

                var prototype = GameDatabase.GetPrototype<Prototype>(dataRef)
                    ?? throw new InvalidOperationException($"DataRef {dataRef} is not Prototype.");
                valueEntry = prototype;
            }

            return ConvertValue(valueEntry, elementType);
        }

        /// <summary>
        /// Resolves the true (owner, relative path) pair for a child of
        /// <paramref name="parent"/> by walking the real object tree, rather
        /// than trusting whatever's currently on top of _protoStack.
        /// </summary>
        private (PrototypeId Owner, string Path) ResolveParentContext(Prototype parent)
        {
            if (parent.DataRef != PrototypeId.Invalid)
            {
                // parent is itself a top-level prototype — it's the root of
                // this subtree regardless of whether it currently has any
                // patches of its own pending (if it doesn't, the eventual
                // _patchDict lookup for it simply finds nothing).
                return (parent.DataRef, string.Empty);
            }

            if (_pathDict.TryGetValue(parent, out var parentEntry))
                return parentEntry;

            return (PrototypeId.Invalid, string.Empty);
        }

        public void SetPath(Prototype parent, Prototype child, string fieldName)
        {
            var (owner, parentPath) = ResolveParentContext(parent);
            _pathDict[child] = (owner, $"{parentPath}.{fieldName}");
        }

        public void SetPathIndex(Prototype parent, Prototype child, string fieldName, int index)
        {
            var (owner, parentPath) = ResolveParentContext(parent);
            _pathDict[child] = (owner, $"{parentPath}.{fieldName}[{index}]");
        }

        // Snapshots and clears the current patch-resolution state so that a
        // recursive GameDatabase.GetPrototype() call (e.g. resolving a
        // PrototypeId reference encountered mid-patch in ConvertValue) can't
        // push onto / read from the SAME _protoStack/_pathDict as the
        // in-progress outer patch and corrupt it. Adopted from upstream's
        // PatchContext re-entrancy fix, adapted to our (Owner, Path) tuple
        // _pathDict rather than upstream's plain-string PathDict, since ours
        // is what the nested-prototype cross-contamination fix above depends
        // on.
        private PatchContext CreateSubContext()
        {
            PatchContext oldContext = new(_protoStack, _pathDict);
            _protoStack = new();
            _pathDict = new();
            return oldContext;
        }

        private void RestoreContext(in PatchContext context)
        {
            _protoStack = context.ProtoStack;
            _pathDict = context.PathDict;
        }

        private readonly struct PatchContext
        {
            public readonly Stack<PrototypeId> ProtoStack;
            public readonly Dictionary<Prototype, (PrototypeId Owner, string Path)> PathDict;

            public PatchContext(Stack<PrototypeId> protoStack, Dictionary<Prototype, (PrototypeId Owner, string Path)> pathDict)
            {
                ProtoStack = protoStack;
                PathDict = pathDict;
            }
        }
    }
}
