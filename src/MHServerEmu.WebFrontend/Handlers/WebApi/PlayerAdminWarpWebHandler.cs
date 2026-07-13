// Player admin warp — used by OmegaDev's Region Forge to teleport the player
// to an arbitrary region prototype. Reuses TeleportToRegionFromWeb like the
// remix endpoint does.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PlayerAdminWarpWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            string? regionRef = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("regionProtoRef", out var r)) regionRef = r.GetString();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(regionRef))
            {
                await context.SendJsonAsync(new { ok = false, error = "missing 'regionProtoRef'" });
                return;
            }
            ulong regionRefId = RegionsRuntime.ResolveRegionRef(regionRef, out string? resolveErr);
            if (regionRefId == 0)
            {
                await context.SendJsonAsync(new { ok = false, error = resolveErr ?? "cannot resolve regionProtoRef" });
                return;
            }
            var (avatar, err) = RegionsRuntime.FindAnyPlayerAvatar();
            if (avatar == null)
            {
                await context.SendJsonAsync(new { ok = false, error = err ?? "no player online" });
                return;
            }
            var m = avatar.GetType().GetMethod("TeleportToRegionFromWeb", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) { await context.SendJsonAsync(new { ok = false, error = "teleport method missing" }); return; }
            try { m.Invoke(avatar, new object[] { regionRefId }); }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }
            await context.SendJsonAsync(new { ok = true, regionProtoRef = regionRef });
        }
    }

    // Stub — Region Forge's "save mod" endpoint. This fork doesn't have a
    // real ModPacker on the server yet; accept the payload so the tool
    // reports success and iterate later.
    public class ModSaveWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            await context.SendJsonAsync(new { ok = true, accepted = body?.Length ?? 0, note = "Mod-save is a client-side stub on this fork." });
        }
    }
}
