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
            bool cookedOk = string.IsNullOrWhiteSpace(cooked) == false && Directory.Exists(cooked);

            string upk = ClientAssetToolPaths.ResolveUpkExtract(cfg?.UpkExtractPath);
            string tfc = ClientAssetToolPaths.ResolveTfcExtract(cfg?.TfcExtractPath);

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
    }
}
