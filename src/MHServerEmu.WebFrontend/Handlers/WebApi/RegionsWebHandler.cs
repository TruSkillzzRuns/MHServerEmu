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
            var regions = RegionsRuntime.ListAllRegions();
            // Return both OmegaDev's expected shape (totalRegions/regions[*]{protoRef,name,path,isSafe})
            // and my Teleport Pad's original fields (id/shortName/displayName), so both consumers work.
            return context.SendJsonAsync(new
            {
                ok = true,
                count = regions.Count,
                totalRegions = regions.Count,
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
        /// Enumerate every RegionPrototype in the loaded client data. Mark
        /// isSafe=true for entries in the RegionPrototypeId safe-warp
        /// allowlist. Returns fields both my Teleport Pad and OmegaDev's
        /// Region Remix expect.
        /// </summary>
        public static List<RegionInfo> ListAllRegions()
        {
            var result = new List<RegionInfo>();
            var safeIds = new HashSet<ulong>();

            var enumType = Type.GetType("MHServerEmu.Games.Regions.RegionPrototypeId, MHServerEmu.Games");
            if (enumType != null)
            {
                foreach (var value in Enum.GetValues(enumType))
                {
                    ulong id = Convert.ToUInt64(value);
                    if (id != 0) safeIds.Add(id);
                }
            }

            var gameDatabaseType = Type.GetType("MHServerEmu.Games.GameData.GameDatabase, MHServerEmu.Games");
            var getNameMethod = gameDatabaseType?.GetMethod("GetPrototypeName", BindingFlags.Public | BindingFlags.Static);
            var dataDirType = Type.GetType("MHServerEmu.Games.GameData.DataDirectory, MHServerEmu.Games");
            var flagsType = Type.GetType("MHServerEmu.Games.GameData.PrototypeIterateFlags, MHServerEmu.Games");
            var regionProtoType = Type.GetType("MHServerEmu.Games.GameData.Prototypes.RegionPrototype, MHServerEmu.Games");

            if (dataDirType == null || flagsType == null || regionProtoType == null || gameDatabaseType == null)
            {
                // Fallback — just enumerate the safe-warp allowlist so we always return SOMETHING.
                foreach (var id in safeIds)
                    result.Add(BuildRow(id, TryGetPath(getNameMethod, id) ?? id.ToString(), isSafe: true));
                return result;
            }

            object flags = Enum.Parse(flagsType, "NoAbstractApprovedOnly");
            var iterMethod = dataDirType.GetMethod("IteratePrototypesInHierarchy",
                new[] { typeof(Type), flagsType });
            var instanceProp = dataDirType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var dataDir = instanceProp?.GetValue(null);
            if (iterMethod == null || dataDir == null)
            {
                foreach (var id in safeIds)
                    result.Add(BuildRow(id, TryGetPath(getNameMethod, id) ?? id.ToString(), isSafe: true));
                return result;
            }

            var iter = iterMethod.Invoke(dataDir, new object[] { regionProtoType, flags });
            if (iter == null) return result;
            // PrototypeIterator has a duck-typed struct enumerator — not IEnumerable.
            var getEnum = iter.GetType().GetMethod("GetEnumerator");
            var e = getEnum?.Invoke(iter, null);
            if (e == null) return result;
            var moveNext = e.GetType().GetMethod("MoveNext");
            var currentProp = e.GetType().GetProperty("Current");
            if (moveNext == null || currentProp == null) return result;
            while (moveNext.Invoke(e, null) is bool ok && ok)
            {
                var refObj = currentProp.GetValue(e);
                if (refObj == null) continue;
                ulong id = Convert.ToUInt64(refObj);
                if (id == 0) continue;
                string path = TryGetPath(getNameMethod, id) ?? id.ToString();
                result.Add(BuildRow(id, path, safeIds.Contains(id)));
            }
            return result;
        }

        private static string? TryGetPath(MethodInfo? getName, ulong id)
        {
            if (getName == null) return null;
            try { return getName.Invoke(null, new object[] { id }) as string; }
            catch { return null; }
        }

        private static RegionInfo BuildRow(ulong id, string path, bool isSafe)
        {
            string leaf = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0) leaf = path[(slash + 1)..];
            if (leaf.EndsWith(".prototype")) leaf = leaf[..^".prototype".Length];
            return new RegionInfo
            {
                id = id,
                protoRef = "0x" + id.ToString("X"),
                shortName = leaf,
                path = path,
                name = HumanReadable(leaf),
                displayName = HumanReadable(leaf),
                isSafe = isSafe,
            };
        }

        // Kept for backward compat with any caller expecting the old shape.
        public static List<RegionInfo> ListSafeRegions()
            => ListAllRegions().Where(r => r.isSafe).ToList();

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
            public string protoRef { get; set; } = "";
            public string shortName { get; set; } = "";
            public string path { get; set; } = "";
            public string name { get; set; } = "";
            public string displayName { get; set; } = "";
            public bool isSafe { get; set; }
        }
    }
}
