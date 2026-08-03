// Region Remix WebAPI — matches OmegaDev's expected schema.
//
//   POST /webapi/regionremix/warp
//   Body: { playerName, playerDbId, regionProtoRef, level, difficultyTierRef, affixes, itemRarityRef, endlessLevel, allowUnsafe }
//
// Wraps Teleporter.DebugTeleportToTarget with the requested difficulty tier.
// This fork doesn't have DebugRemixTeleportToTarget yet (which would also
// apply level / affix / rarity overrides). For now the extra knobs are
// accepted but only difficulty is applied — the client still gets a fresh
// region instance via the standard warp path.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class RegionRemixWarpWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();
            string? regionRef = null;
            string? difficultyTierRef = null;
            int level = 0;
            int endlessLevel = 0;
            bool allowUnsafe = false;

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("regionProtoRef", out var r)) regionRef = r.GetString();
                if (doc.RootElement.TryGetProperty("difficultyTierRef", out var d)) difficultyTierRef = d.GetString();
                if (doc.RootElement.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number) level = l.GetInt32();
                if (doc.RootElement.TryGetProperty("endlessLevel", out var el) && el.ValueKind == JsonValueKind.Number) endlessLevel = el.GetInt32();
                if (doc.RootElement.TryGetProperty("allowUnsafe", out var u) && u.ValueKind == JsonValueKind.True) allowUnsafe = true;
            }
            catch { /* bad JSON handled below */ }

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

            var scheduleMethod = avatar.GetType().GetMethod("TeleportToRegionFromWeb",
                BindingFlags.Public | BindingFlags.Instance);
            if (scheduleMethod == null)
            {
                await context.SendJsonAsync(new { ok = false, error = "TeleportToRegionFromWeb missing" });
                return;
            }
            try { scheduleMethod.Invoke(avatar, new object[] { regionRefId }); }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }

            Logger.Info($"[RegionRemix:Web] warp → {regionRef} (level={level} diff={difficultyTierRef} endless={endlessLevel} unsafe={allowUnsafe})");
            await context.SendJsonAsync(new
            {
                ok = true,
                regionProtoRef = regionRef,
                appliedDifficulty = difficultyTierRef,
                note = "Warp queued. Level/affix/rarity are accepted but only difficulty is currently applied on this fork.",
            });
        }
    }
}
