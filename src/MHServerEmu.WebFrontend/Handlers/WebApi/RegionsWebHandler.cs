// Regions WebAPI — installed for OmegaDev2's Teleport Pad tool.
//
//   GET  /webapi/regions/list       -> {"regions":[{"id":..., "path":"...", "displayName":"..."}], ...]}
//   POST /webapi/regions/teleport    body: {"regionRef":"<path or numeric id>"}
//
// The list endpoint enumerates the safe-warp allowlist (RegionPrototypeId enum) —
// same filter the !region warp chat command uses when not passed "unsafe" — so
// only regions that ship complete assets appear.
//
// Teleport works by finding the first live Avatar (same helper the Phantom Heroes
// handler uses), resolving the region ref, and scheduling a game-thread event on
// that avatar (see Avatar.WebTeleport.cs). Off-thread WebAPI callers never touch
// thread-static Game state directly.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class RegionsListWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override Task Get(WebRequestContext context)
        {
            var regions = RegionsRuntime.ListSafeRegions();
            return context.SendJsonAsync(new
            {
                ok = true,
                count = regions.Count,
                regions,
            });
        }
    }

    public class RegionsTeleportWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            string? regionRefStr = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("regionRef", out var r)) regionRefStr = r.GetString();
            }
            catch { /* fall through — regionRef missing is a bad-request error below */ }

            if (string.IsNullOrWhiteSpace(regionRefStr))
            {
                await context.SendJsonAsync(new { ok = false, error = "missing 'regionRef' — expected a numeric prototype id or a Regions/... path string" });
                return;
            }

            // Resolve numeric or path.
            ulong regionRefId = RegionsRuntime.ResolveRegionRef(regionRefStr, out string? resolveErr);
            if (regionRefId == 0)
            {
                await context.SendJsonAsync(new { ok = false, error = resolveErr ?? $"could not resolve regionRef '{regionRefStr}'" });
                return;
            }

            var (avatar, err) = RegionsRuntime.FindAnyPlayerAvatar();
            if (avatar == null)
            {
                await context.SendJsonAsync(new { ok = false, error = err ?? "no player online" });
                return;
            }

            var scheduleMethod = avatar.GetType().GetMethod("TeleportToRegionFromWeb",
                BindingFlags.Public | BindingFlags.Instance);
            if (scheduleMethod == null)
            {
                await context.SendJsonAsync(new { ok = false, error = "TeleportToRegionFromWeb missing — is Avatar.WebTeleport.cs current?" });
                return;
            }
            try { scheduleMethod.Invoke(avatar, new object[] { regionRefId }); }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }

            Logger.Info($"[Regions:Web] teleport queued → {regionRefStr}");
            await context.SendJsonAsync(new { ok = true, regionRefId, regionRef = regionRefStr, note = "Teleport queued on the game thread." });
        }
    }

    internal static class RegionsRuntime
    {
        /// <summary>
        /// Enumerate every entry in the RegionPrototypeId safe-warp allowlist.
        /// The enum's field name matches the prototype short-name; the value
        /// is the numeric PrototypeId. Display names come from the [Description]
        /// attribute if present, otherwise the enum field name.
        /// </summary>
        public static List<RegionInfo> ListSafeRegions()
        {
            var enumType = Type.GetType("MHServerEmu.Games.Regions.RegionPrototypeId, MHServerEmu.Games");
            var result = new List<RegionInfo>();
            if (enumType == null) return result;

            var gameDatabaseType = Type.GetType("MHServerEmu.Games.GameData.GameDatabase, MHServerEmu.Games");
            var getNameMethod = gameDatabaseType?.GetMethod("GetPrototypeName", BindingFlags.Public | BindingFlags.Static);

            foreach (var value in Enum.GetValues(enumType))
            {
                string shortName = value.ToString() ?? "";
                ulong id = Convert.ToUInt64(value);
                if (id == 0) continue;

                string path = shortName;
                if (getNameMethod != null)
                {
                    try
                    {
                        var pathObj = getNameMethod.Invoke(null, new object[] { id });
                        if (pathObj is string s && !string.IsNullOrEmpty(s)) path = s;
                    }
                    catch { /* fall through — use shortName */ }
                }

                result.Add(new RegionInfo
                {
                    id = id,
                    shortName = shortName,
                    path = path,
                    displayName = HumanReadable(shortName),
                });
            }
            return result;
        }

        public static ulong ResolveRegionRef(string s, out string? error)
        {
            error = null;
            if (ulong.TryParse(s, out ulong id)) return id;

            // Path lookup via GameDatabase.GetPrototypeRefByName(string) -> PrototypeId.
            var gameDatabaseType = Type.GetType("MHServerEmu.Games.GameData.GameDatabase, MHServerEmu.Games");
            var getRefMethod = gameDatabaseType?.GetMethod("GetPrototypeRefByName", BindingFlags.Public | BindingFlags.Static);
            if (getRefMethod == null)
            {
                error = "GameDatabase.GetPrototypeRefByName not reachable via reflection";
                return 0;
            }
            try
            {
                var refObj = getRefMethod.Invoke(null, new object[] { s });
                if (refObj != null)
                {
                    ulong resolved = Convert.ToUInt64(refObj);
                    if (resolved != 0) return resolved;
                }
                error = $"region path '{s}' did not resolve to a prototype";
                return 0;
            }
            catch (Exception ex)
            {
                error = $"path resolve threw {ex.GetType().Name}: {ex.Message}";
                return 0;
            }
        }

        /// <summary>Reuse the Phantom Heroes runtime's player-avatar finder.</summary>
        public static (object? avatar, string? error) FindAnyPlayerAvatar()
            => PhantomHeroRuntime.FindAnyPlayerAvatar();

        /// <summary>Turn "CH0203RhinoBargeRegion" into "CH0203 Rhino Barge Region" so the UI has readable text.</summary>
        private static string HumanReadable(string camel)
        {
            if (string.IsNullOrEmpty(camel)) return camel;
            var sb = new System.Text.StringBuilder(camel.Length + 8);
            for (int i = 0; i < camel.Length; i++)
            {
                char c = camel[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(camel[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public class RegionInfo
        {
            public ulong id { get; set; }
            public string shortName { get; set; } = "";
            public string path { get; set; } = "";
            public string displayName { get; set; } = "";
        }
    }
}
