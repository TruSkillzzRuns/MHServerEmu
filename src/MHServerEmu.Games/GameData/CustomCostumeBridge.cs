// Reflection bridge to MHCostumeMod's costume loader.
//
// MHCostumeMod (Mr.Gippy) adds costumes the retail game never had. Its loader
// mints a prototype id at runtime and aliases it onto a real "donor" costume's
// data record; the injected client DLL then substitutes the art. None of that
// exists in this fork, and referencing the mod would make it a build
// dependency, so everything here goes through reflection.
//
// On a server without the mod the type is simply absent, every member here
// reports "no custom costumes", and callers carry on unchanged.

using System.Collections;
using System.Reflection;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.GameData
{
    /// <summary>One custom costume, as the mod's loader describes it.</summary>
    public readonly struct CustomCostumeInfo(string name, string token, int enumValue, ulong customId)
    {
        public readonly string Name = name;
        public readonly string Token = token;
        public readonly int Enum = enumValue;
        public readonly ulong CustomId = customId;
    }

    /// <summary>One visual-effect pack. Effects is a count, not a list.</summary>
    public readonly struct CustomCostumeFxPackInfo(string token, string displayName, string hero, int effects)
    {
        public readonly string Token = token;
        public readonly string DisplayName = displayName;
        public readonly string Hero = hero;
        public readonly int Effects = effects;
    }

    public static class CustomCostumeBridge
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly object _resolveLock = new();
        private static bool _resolved;

        private static PropertyInfo _customInfo;
        private static PropertyInfo _catalog;
        private static PropertyInfo _fxPackCatalog;

        /// <summary>True when this server has the mod's costume loader.</summary>
        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _customInfo != null || _catalog != null;
            }
        }

        /// <summary>
        /// True when this costume id was minted by the mod rather than coming
        /// from the game's own data.
        /// </summary>
        /// <remarks>
        /// Worth knowing because a custom id is indistinguishable from its
        /// donor through the normal prototype lookups -- it aliases the donor's
        /// record, so <c>As&lt;CostumePrototype&gt;()</c> hands back the donor.
        /// </remarks>
        public static bool IsCustom(PrototypeId costumeRef)
        {
            if (costumeRef == PrototypeId.Invalid) return false;

            Resolve();
            if (_customInfo == null) return false;

            try
            {
                // The loader keys this by PrototypeId, and we are in the same
                // assembly that defines it, so the boxed key compares equal.
                return _customInfo.GetValue(null) is IDictionary map && map.Contains(costumeRef);
            }
            catch (Exception e)
            {
                Logger.Warn($"CustomCostumeBridge.IsCustom: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The real costume a custom id is aliased onto, or Invalid when there
        /// isn't a usable one.
        /// </summary>
        public static PrototypeId GetDonor(PrototypeId customRef)
        {
            CostumePrototype donorProto = GameDatabase.GetPrototype<CostumePrototype>(customRef);
            PrototypeId donorRef = donorProto != null ? donorProto.DataRef : PrototypeId.Invalid;

            // A donor that resolves back to the custom id itself means the
            // aliasing never happened, and swapping to it would be a no-op.
            return donorRef == customRef ? PrototypeId.Invalid : donorRef;
        }

        /// <summary>Every custom costume the mod has loaded.</summary>
        public static List<CustomCostumeInfo> GetCatalog()
        {
            var results = new List<CustomCostumeInfo>();

            Resolve();
            foreach (object entry in Read(_catalog))
            {
                if (entry == null) continue;

                results.Add(new CustomCostumeInfo(
                    AsString(GetMember(entry, "Name")),
                    AsString(GetMember(entry, "Token")),
                    AsInt(GetMember(entry, "Enum")),
                    AsULong(GetMember(entry, "CustomId"))));
            }

            return results;
        }

        /// <summary>Every visual-effect pack the mod has loaded.</summary>
        public static List<CustomCostumeFxPackInfo> GetFxPacks()
        {
            var results = new List<CustomCostumeFxPackInfo>();

            Resolve();
            foreach (object entry in Read(_fxPackCatalog))
            {
                if (entry == null) continue;

                results.Add(new CustomCostumeFxPackInfo(
                    AsString(GetMember(entry, "Token")),
                    AsString(GetMember(entry, "DisplayName")),
                    AsString(GetMember(entry, "Hero")),
                    AsInt(GetMember(entry, "Effects"))));
            }

            return results;
        }

        /// <summary>
        /// Finds the mod's loader, once per process.
        /// </summary>
        /// <remarks>
        /// The negative result is cached too: an assembly absent at the first
        /// call will not appear at the second, and rescanning every loaded
        /// assembly on each phantom spawn would be wasted work.
        /// </remarks>
        private static void Resolve()
        {
            lock (_resolveLock)
            {
                if (_resolved) return;
                _resolved = true;

                Type type = FindLoaderType();
                if (type == null) return;

                _customInfo = FindStatic(type, "CustomInfo");
                _catalog = FindStatic(type, "Catalog");
                _fxPackCatalog = FindStatic(type, "FxPackCatalog");

                Logger.Info($"CustomCostumeBridge: custom costume support detected ({type.FullName}).");
            }
        }

        private static Type FindLoaderType()
        {
            // Fast path: where the mod actually puts it.
            Type type = Type.GetType("MHServerEmu.Games.GameData.CustomCostumeLoader, MHServerEmu.Games");
            if (type != null) return type;

            // Fallback for a fork that moved or renamed the namespace.
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                try
                {
                    foreach (Type candidate in assembly.GetTypes())
                    {
                        if (candidate.Name == "CustomCostumeLoader")
                            return candidate;
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // An assembly that cannot be fully loaded is not the one.
                }
            }

            return null;
        }

        private static PropertyInfo FindStatic(Type type, string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);

        // Re-read per call rather than cached: the mod reloads its costume list
        // at runtime, so the value behind the property changes even though the
        // property does not.
        private static IEnumerable<object> Read(PropertyInfo property)
        {
            if (property == null) return Array.Empty<object>();

            try
            {
                if (property.GetValue(null) is IEnumerable items)
                    return items.Cast<object>();
            }
            catch (Exception e)
            {
                Logger.Warn($"CustomCostumeBridge: failed to read {property.Name}: {e.Message}");
            }

            return Array.Empty<object>();
        }

        // The mod declares its catalog entries as readonly structs with public
        // fields; checking properties too costs nothing and survives a fork
        // that rewrote them.
        private static object GetMember(object instance, string name)
        {
            Type type = instance.GetType();

            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(instance);

            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(instance);
        }

        private static string AsString(object value) => value?.ToString() ?? string.Empty;

        private static int AsInt(object value) => value == null ? 0 : Convert.ToInt32(value);

        private static ulong AsULong(object value) => value == null ? 0UL : Convert.ToUInt64(value);
    }
}
