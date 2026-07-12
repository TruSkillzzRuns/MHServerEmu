using System.Net;
using System.Security.Cryptography;
using System.Text;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    // GET /webapi/portrait?path=<UE3.asset.path>[&w=128&h=128]
    //
    // Resolves a UE3 asset path (e.g. "UIPackage.SomeTextureName" or just
    // a leaf "Placeholder") to PNG bytes. The OmegaDev picker hits this when
    // its local PortraitService can't decode a texture because the mip data
    // is TFC-streamed instead of cooked inline. Most non-iconic enemies in
    // 1.53 share the literal "Placeholder" asset, which itself ships only in
    // the TFC stream â€” so a working fallback turns thousands of blank rows
    // into recognizable icons.
    //
    // Pipeline:
    //   1. SHA-256 the (path,w,h) tuple â†’ cache key.
    //   2. Look in <LocalAppData>/MHServerEmu/portrait_tfc_cache/<key>.png.
    //      Hit â†’ serve straight from disk (cheap path for repeated lookups).
    //   3. Miss â†’ call TfcFallbackHelper.TryExtractByTextureNameAsync. That
    //      shells out to Tools/TfcExtract/TfcExtract.exe which knows how to
    //      decompress mip0 from the .tfc stream into PNG.
    //   4. On success: stream the PNG back; on failure: 404.
    //
    // Cached aggressively because (a) PNGs are tiny (~3-30KB), (b) every
    // picker open re-requests the same paths, (c) TFC extraction is slow
    // (~150ms per call shelling out).
    internal class PortraitWebHandler : WebHandler
    {
        // No API key required â€” LocalOnlyGuard.CheckAsync() in Get() already
        // restricts callers to loopback so remote hosts can't hit this. This
        // lets WinUI's Image control fetch portrait URLs directly (no key on
        // the bare URL). Same pattern as AnimsForWebHandler / CellMeshWebHandler.
        public override WebApiAccessType Access { get => WebApiAccessType.None; }

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            var qs = System.Web.HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            string path = (qs["path"] ?? "").Trim();
            if (path.Length == 0)
            { context.StatusCode = (int)HttpStatusCode.BadRequest; await context.SendAsync("missing ?path", "text/plain"); return; }

            int w = int.TryParse(qs["w"], out var pw) && pw > 0 ? pw : 128;
            int h = int.TryParse(qs["h"], out var ph) && ph > 0 ? ph : 128;
            // Clamp the dimension range â€” TfcExtract is happy at anything but
            // we don't want a runaway caller asking for 16kÂ². 1024Â² is the
            // largest mip the cooked HD portraits ship at.
            if (w > 1024) w = 1024;
            if (h > 1024) h = 1024;

            string cachePath = GetCachePath(path, w, h);

            // Read the cache INSIDE the try, send OUTSIDE it. The old shape
            // wrapped the send too â€” when a client aborted mid-write the
            // exception was swallowed and control fell through to extraction
            // + a SECOND send on the same (already submitted) response,
            // spamming InvalidOperationException into the log.
            byte[] cachedPng = null;
            try
            {
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                    cachedPng = await File.ReadAllBytesAsync(cachePath);
            }
            catch { /* unreadable cache file â€” fall through to regenerate */ }

            if (cachedPng != null && cachedPng.Length > 0)
            {
                await context.SendAsync(cachedPng, "image/png");
                return;
            }

            var cfg = ConfigManager.Instance.GetConfig<ClientAssetsConfig>();
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.TfcExtractPath))
            { context.StatusCode = (int)HttpStatusCode.NotFound; await context.SendAsync("TfcExtractPath not configured", "text/plain"); return; }

            // TfcExtract takes the needle as either a leaf or full asset
            // path. The picker sends the full path; if extraction fails,
            // retry with the leaf only (handles "Pkg.Tex" vs bare "Tex"
            // tagging mismatches between manifest entries).
            string outDir = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(outDir);

            string produced = await TfcFallbackHelper.TryExtractByTextureNameAsync(cfg, path, cachePath, "PortraitWebHandler");
            if (produced == null)
            {
                int lastDot = path.LastIndexOf('.');
                if (lastDot >= 0 && lastDot + 1 < path.Length)
                {
                    string leaf = path.Substring(lastDot + 1);
                    produced = await TfcFallbackHelper.TryExtractByTextureNameAsync(cfg, leaf, cachePath, "PortraitWebHandler/leaf");
                }
            }

            if (produced == null || !File.Exists(produced))
            { context.StatusCode = (int)HttpStatusCode.NotFound; await context.SendAsync($"no TFC entry for '{path}'", "text/plain"); return; }

            await SendPngAsync(context, produced);
        }

        private static async Task SendPngAsync(WebRequestContext context, string filePath)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(filePath); }
            catch (Exception ex)
            { context.StatusCode = (int)HttpStatusCode.InternalServerError; await context.SendAsync(ex.Message, "text/plain"); return; }
            await context.SendAsync(bytes, "image/png");
        }

        private static string GetCachePath(string path, int w, int h)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string keyInput = $"{path.ToLowerInvariant()}|{w}x{h}";
            string hash;
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(keyInput));
                hash = Convert.ToHexString(digest).Substring(0, 24).ToLowerInvariant();
            }
            return Path.Combine(root, "MHServerEmu", "portrait_tfc_cache", $"{hash}.png");
        }
    }
}

