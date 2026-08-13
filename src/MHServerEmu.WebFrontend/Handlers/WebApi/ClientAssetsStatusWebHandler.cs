// GET /webapi/clientassets/status
//
// Reports whether this server can resolve client artwork, and why not if it
// can't. Exists because the OmegaDev2 Setup page previously inferred that
// state by guessing at a file path, which broke the moment the cache moved —
// it reported "index not built" while the server was happily serving from it.
//
// The server owns these paths, so the server is what should answer.

using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class ClientAssetsStatusWebHandler : WebHandler
    {
        // No API key: same reasoning as the portrait handler — LocalOnlyGuard
        // already restricts this to loopback, and the Setup page reads it
        // before any key exists.
        public override WebApiAccessType Access { get => WebApiAccessType.None; }

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            var cfg = ConfigManager.Instance.GetConfig<ClientAssetsConfig>();

            string cooked = cfg?.CookedPCConsolePath ?? "";

            // Cached: this endpoint is polled every few seconds by the app's
            // server-discovery loop, and the three probes below are all
            // filesystem work whose answers change about never.
            //
            // Directory.Exists on the client path can hit a different physical
            // drive (mine sits on E: while the server runs from C:) and the
            // tool resolvers walk repo build directories hunting for the exe.
            // Uncached, this handler measured 2.4-2.8 SECONDS on 1.52 versus
            // under 1ms on the other two, which blew past the discovery
            // client's timeout and made a healthy server flap online/offline.
            (bool cookedOk, string upk, string tfc) = GetProbesCached(cfg, cooked);

            bool indexBuilt = ClientAssetIndexBuilder.IndexExists;

            // A build may be running right now (startup kicks one off), so a
            // missing index is not necessarily a failure.
            string state =
                indexBuilt ? "ready" :
                cookedOk == false ? "no-client-path" :
                upk.Length == 0 ? "no-tool" :
                "building";

            await context.SendJsonAsync(new
            {
                Ok = true,
                GameVersion = ClientAssetCachePaths.GameVersionTag,
                State = state,

                // Everything derived from the client lives here, deliberately
                // outside the repository.
                CacheDirectory = ClientAssetCachePaths.RootDirectory,
                IndexPath = ClientAssetCachePaths.TextureIndexPath,
                IndexBuilt = indexBuilt,

                CookedPCConsolePath = cooked,
                CookedPathOk = cookedOk,

                UpkExtractResolved = upk,
                TfcExtractResolved = tfc,
                ToolsOk = upk.Length > 0 && tfc.Length > 0,

                Hint = state switch
                {
                    "ready" => "Client artwork is available.",
                    "no-client-path" => "Set CookedPCConsolePath to your own game client's CookedPCConsole folder.",
                    "no-tool" => "UpkExtract was not found next to the server or in the repo build output.",
                    _ => "Index is building from your client; artwork appears once it finishes (about 15-60 seconds).",
                },
            });
        }

        // ---- probe cache -------------------------------------------------

        private static readonly object s_probeLock = new();
        private static string s_probeKey;
        private static (bool CookedOk, string Upk, string Tfc) s_probeValue;
        private static DateTime s_probeAtUtc = DateTime.MinValue;

        private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Returns the filesystem probe results, recomputing at most once per
        /// <see cref="ProbeTtl"/>. Keyed on the configured paths so editing
        /// Config.ini and re-checking still reflects the change promptly.
        /// </summary>
        private static (bool CookedOk, string Upk, string Tfc) GetProbesCached(ClientAssetsConfig cfg, string cooked)
        {
            string key = $"{cooked}|{cfg?.UpkExtractPath}|{cfg?.TfcExtractPath}";

            lock (s_probeLock)
            {
                if (s_probeKey == key && DateTime.UtcNow - s_probeAtUtc < ProbeTtl)
                    return s_probeValue;

                bool cookedOk = string.IsNullOrWhiteSpace(cooked) == false && Directory.Exists(cooked);
                string upk = ClientAssetToolPaths.ResolveUpkExtract(cfg?.UpkExtractPath);
                string tfc = ClientAssetToolPaths.ResolveTfcExtract(cfg?.TfcExtractPath);

                s_probeKey = key;
                s_probeValue = (cookedOk, upk, tfc);
                s_probeAtUtc = DateTime.UtcNow;
                return s_probeValue;
            }
        }

    }
}
