using System.Net;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    // Loopback-only guard for asset-serving endpoints (textures, meshes,
    // animations extracted from the operator's owned game client install).
    // Reject anything not from 127.0.0.1 / ::1 so those endpoints stay
    // operator-local rather than redistributing third-party content.
    internal static class LocalOnlyGuard
    {
        public static async Task<bool> CheckAsync(WebRequestContext context)
        {
            string ip = context.GetIPAddress();
            if (IsLocal(ip)) return true;

            context.StatusCode = (int)HttpStatusCode.Forbidden;
            await context.SendAsync(
                "This endpoint is restricted to loopback (127.0.0.1 / ::1).",
                "text/plain");
            return false;
        }

        private static bool IsLocal(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip == "127.0.0.1" || ip == "::1" || ip == "0:0:0:0:0:0:0:1") return true;
            if (IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr)) return true;
            return false;
        }
    }
}
