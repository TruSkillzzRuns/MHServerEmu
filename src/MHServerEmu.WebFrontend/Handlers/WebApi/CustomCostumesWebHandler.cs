// Custom costume catalog — a bridge for MHCostumeMod (Mr.Gippy), which lets
// players add costumes the retail game never had.
//
//   GET /webapi/customcostumes/catalog
//     -> 200 { "ok": true, "costumes": [ { name, token, enum, customId } ],
//                          "fxPacks":  [ { token, displayName, hero, effects } ] }
//     -> 404 on any server without the mod's costume loader
//
// Why this exists
// ---------------
// A custom costume has no entry anywhere in the game's prototype data. The
// mod's loader mints a prototype id at runtime and aliases it onto a real
// "donor" costume's data record, and the injected client DLL substitutes the
// art. Every catalog endpoint in this fork enumerates prototypes from the
// loaded client data, so none of them can ever see a custom costume, and
// OmegaDev2 therefore cannot offer one when picking a phantom's costume.
//
// The mod's own MHServerEmu fork publishes an equivalent endpoint, but only
// its WebFrontend does — and merging a WebFrontend is the awkward half of the
// merge, since that is where every route in here is registered. Anyone who
// merges only the Games-layer half gets working custom costumes in game and
// no way for the app to list them.
//
// So this handler reaches the loader by reflection rather than referencing it.
// On a stock server the type is simply absent and the endpoint 404s, which
// costs nothing and changes nothing. On a server that merged the mod's Games
// layer it starts answering on its own, with no WebFrontend merge at all.

using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class CustomCostumesCatalogWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static Loader _loader;
        private static bool _resolved;
        private static readonly object _resolveLock = new();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            Loader loader = Resolve();
            if (loader == null)
            {
                // Not an error: this is the answer for every server not
                // running the costume mod, which is nearly all of them.
                context.StatusCode = (int)HttpStatusCode.NotFound;
                await context.SendJsonAsync(new
                {
                    Ok = false,
                    Error = "This server does not have custom costume support installed."
                });
                return;
            }

            try
            {
                var root = new JsonObject
                {
                    ["ok"] = true,
                    ["costumes"] = BuildCostumes(loader),
                    ["fxPacks"] = BuildFxPacks(loader),
                };

                await context.SendAsync(root.ToJsonString(), "application/json");
            }
            catch (Exception e)
            {
                // An unexpected shape is worth a log line and a real error:
                // silently returning an empty catalog would read as "no custom
                // costumes installed", which is a different thing entirely.
                Logger.Warn($"CustomCostumesCatalog: failed to read the custom costume catalog: {e.Message}");
                context.StatusCode = (int)HttpStatusCode.InternalServerError;
                await context.SendJsonAsync(new { Ok = false, Error = "Failed to read the custom costume catalog." });
            }
        }

        /// <summary>
        /// Field names match the mod's own catalog endpoint exactly, so one
        /// client parser reads either server.
        /// </summary>
        private static JsonArray BuildCostumes(Loader loader)
        {
            var costumes = new JsonArray();

            foreach (object entry in loader.ReadCatalog())
            {
                if (entry == null) continue;

                costumes.Add(new JsonObject
                {
                    ["name"] = AsString(GetMember(entry, "Name")),
                    ["token"] = AsString(GetMember(entry, "Token")),
                    ["enum"] = AsInt(GetMember(entry, "Enum")),
                    // The mod formats its minted ids this way; matching it
                    // means a client never has to know which server it asked.
                    ["customId"] = $"0x{AsULong(GetMember(entry, "CustomId")):X16}",
                });
            }

            return costumes;
        }

        private static JsonArray BuildFxPacks(Loader loader)
        {
            var packs = new JsonArray();

            foreach (object entry in loader.ReadFxPacks())
            {
                if (entry == null) continue;

                packs.Add(new JsonObject
                {
                    ["token"] = AsString(GetMember(entry, "Token")),
                    ["displayName"] = AsString(GetMember(entry, "DisplayName")),
                    ["hero"] = AsString(GetMember(entry, "Hero")),
                    // A count of effect packages, not a list of them.
                    ["effects"] = AsInt(GetMember(entry, "Effects")),
                });
            }

            return packs;
        }

        /// <summary>
        /// Finds the mod's costume loader, once per process.
        /// </summary>
        /// <remarks>
        /// The negative result is cached too: an assembly absent at the first
        /// request will not appear at the second, and rescanning every loaded
        /// assembly on each poll would be wasted work.
        /// </remarks>
        private static Loader Resolve()
        {
            lock (_resolveLock)
            {
                if (_resolved) return _loader;
                _resolved = true;

                Type type = FindLoaderType();
                if (type == null) return null;

                PropertyInfo catalog = FindStatic(type, "Catalog");
                PropertyInfo fxPacks = FindStatic(type, "FxPackCatalog");

                if (catalog == null)
                {
                    Logger.Warn($"CustomCostumesCatalog: found {type.FullName} but it has no static Catalog " +
                                "property — custom costumes will not be listed.");
                    return null;
                }

                Logger.Info($"CustomCostumesCatalog: custom costume support detected ({type.FullName}), " +
                            "/webapi/customcostumes/catalog is live.");

                _loader = new Loader(catalog, fxPacks);
                return _loader;
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

        /// <summary>
        /// The reflected entry points, resolved once and re-read per request:
        /// the mod reloads its costume list at runtime, so the values behind
        /// these properties change even though the properties do not.
        /// </summary>
        private sealed class Loader
        {
            private readonly PropertyInfo _catalog;
            private readonly PropertyInfo _fxPacks;

            public Loader(PropertyInfo catalog, PropertyInfo fxPacks)
            {
                _catalog = catalog;
                _fxPacks = fxPacks;
            }

            public IEnumerable<object> ReadCatalog() => Read(_catalog);
            public IEnumerable<object> ReadFxPacks() => Read(_fxPacks);

            private static IEnumerable<object> Read(PropertyInfo property)
            {
                if (property == null) return Array.Empty<object>();

                if (property.GetValue(null) is System.Collections.IEnumerable items)
                    return items.Cast<object>();

                return Array.Empty<object>();
            }
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
