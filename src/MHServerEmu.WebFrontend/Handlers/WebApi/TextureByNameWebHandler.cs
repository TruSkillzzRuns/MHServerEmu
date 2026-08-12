using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    // GET /webapi/texbyname?name=<TextureLeafName-or-FullPathName>
    //
    // Phase 4F — global texture-by-name resolver. Uses Cache/texIndex.json
    // (built by UpkExtract.exe indexmeshes) to map a texture's leaf name to
    // its host .upk, then shells UpkExtract.exe texone to extract via the
    // TFC manifest (HD when available, inline fallback). Caches the PNG.
    //
    // Caller may pass either:
    //   - the bare leaf name ("Mtnt_Slms_Stools_DIFF_A_A")
    //   - the full PathName ("Bar.Textures.Mtnt_Slms_Stools_DIFF_A_A")
    // We always strip to the leaf for the lookup since UE3 leaf names are
    // unique within their .upk and the index is leaf-keyed.
    internal class TextureByNameWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly ConcurrentDictionary<string, Task<string>> _inflight = new();
        private static Dictionary<string, string> _texIndex;
        private static readonly object _indexLock = new();

        public override WebApiAccessType Access { get => WebApiAccessType.None; }

        /// <summary>
        /// Pre-load texIndex.json on a background thread so the first request
        /// doesn't pay the parse cost on the WebService dispatcher.
        /// </summary>
        public static Task WarmAsync() => Task.Run(() => EnsureIndexLoaded());

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            var cfg = ConfigManager.Instance.GetConfig<ClientAssetsConfig>();
            if (string.IsNullOrWhiteSpace(cfg.CookedPCConsolePath))
            {
                context.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await context.SendAsync("ClientAssets.CookedPCConsolePath is not set", "text/plain");
                return;
            }

            var qs = System.Web.HttpUtility.ParseQueryString(context.QueryString ?? "");
            // Accept ?fullName= (preferred — Package.Object) or ?name= (legacy
            // leaf or full). The inline-mip texIndex.json is leaf-keyed so we
            // always reduce to a leaf for the .upk lookup, but we keep the
            // ORIGINAL string for the TFC fallthrough where it disambiguates
            // duplicate leaves across packages.
            string raw = qs["fullName"];
            if (string.IsNullOrWhiteSpace(raw)) raw = qs["name"];
            if (string.IsNullOrWhiteSpace(raw))
            {
                context.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.SendAsync("missing ?name=... or ?fullName=...", "text/plain");
                return;
            }
            string fullCandidate = raw.Trim();
            string leaf = fullCandidate.Contains('.') ? fullCandidate[(fullCandidate.LastIndexOf('.') + 1)..] : fullCandidate;
            leaf = Sanitize(leaf);
            if (leaf.Length == 0)
            {
                context.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.SendAsync("name leaf must be [A-Za-z0-9_-]", "text/plain");
                return;
            }

            // Mod-region texture overrides not ported to this fork.

            var idx = EnsureIndexLoaded();
            string upkBase = null;
            idx?.TryGetValue(leaf, out upkBase);

            string pngPath = null;
            try { pngPath = await GetOrExtractAsync(cfg, leaf, upkBase, fullCandidate); }
            catch (Exception ex)
            {
                Logger.Warn($"TexByName: extraction failed for '{leaf}': {ex.Message}");
                context.StatusCode = (int)HttpStatusCode.InternalServerError;
                await context.SendAsync($"extract failed: {ex.Message}", "text/plain");
                return;
            }
            if (pngPath == null || !File.Exists(pngPath))
            {
                context.StatusCode = (int)HttpStatusCode.NotFound;
                await context.SendAsync(upkBase == null
                    ? $"no .upk or TFC entry for Texture2D '{leaf}'"
                    : $"extraction yielded no PNG for '{leaf}'", "text/plain");
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(pngPath);
            await context.SendAsync(bytes, "image/png");
        }

        // Exposed for sibling handlers (MatResolve) that need to look up the
        // host .upk for a texture leaf. Returns null if the index hasn't been
        // built yet (UpkExtract.exe indexmeshes).
        internal static Dictionary<string, string> GetIndexOrNull() => EnsureIndexLoaded();

        private static Dictionary<string, string> EnsureIndexLoaded()
        {
            if (_texIndex != null) return _texIndex;
            lock (_indexLock)
            {
                if (_texIndex != null) return _texIndex;
                // Outside the repo and scoped per game version — see
                // ClientAssetCachePaths. Previously this resolved to
                // Cache/texIndex.json inside the server folder, i.e. inside a
                // public repository tree.
                string indexPath = ClientAssetCachePaths.TextureIndexPath;
                if (!File.Exists(indexPath))
                {
                    // Build it from the user's own client rather than leaving
                    // every icon blank until someone runs a manual setup step.
                    // Fire-and-forget: this request still misses, the next
                    // one hits a warm index.
                    Logger.Warn($"TexByName: index not found at {indexPath} — building it now");
                    ClientAssetIndexBuilder.EnsureBuiltInBackground();
                    return null;
                }
                try
                {
                    string json = File.ReadAllText(indexPath);
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    _texIndex = raw != null
                        ? new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    Logger.Info($"TexByName: loaded texture index — {_texIndex.Count} entries");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"TexByName: failed to parse texIndex.json: {ex.Message}");
                    _texIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                return _texIndex;
            }
        }

        private static Task<string> GetOrExtractAsync(ClientAssetsConfig cfg, string leaf, string upkBase, string fullCandidate)
        {
            return _inflight.GetOrAdd(leaf, n => Task.Run(async () =>
            {
                try
                {
                    string cachePath = Path.GetFullPath(Path.Combine(cfg.GroundTexCacheDirectory, "..", "texByName", $"{n}.png"));
                    if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0) return cachePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

                    // Step 1: inline-mip path via UpkExtract texone (covers most
                    // standard textures).
                    if (!string.IsNullOrEmpty(upkBase))
                    {
                        string upkPath = Path.Combine(cfg.CookedPCConsolePath, $"{upkBase}.upk");
                        if (!File.Exists(upkPath))
                        {
                            Logger.Trace($"TexByName: source .upk missing: {upkPath}");
                        }
                        else
                        {
                            bool inlineOk = await RunTexOneAsync(cfg, upkPath, n, cachePath);
                            if (inlineOk && File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                                return cachePath;
                            Logger.Trace($"TexByName: inline-path empty for '{n}', falling back to TFC manifest");
                        }
                    }

                    // Step 2: TFC fallthrough. Mirror what GroundTexWebHandler
                    // does for HD streaming textures (Knowhere et al.) — many
                    // diffuses are cooked out of the .upk and only exist in
                    // .tfc. Try the full-path candidate first if the caller
                    // provided one, then the leaf (the manifest indexes both).
                    if (!string.IsNullOrEmpty(fullCandidate) && fullCandidate.Contains('.') &&
                        !string.Equals(fullCandidate, n, StringComparison.OrdinalIgnoreCase))
                    {
                        string p = await TfcFallbackHelper.TryExtractByTextureNameAsync(cfg, fullCandidate, cachePath, $"TexByName/{n}");
                        if (p != null) return p;
                    }
                    string pngFromTfc = await TfcFallbackHelper.TryExtractByTextureNameAsync(cfg, n, cachePath, $"TexByName/{n}");
                    return pngFromTfc;
                }
                finally { _inflight.TryRemove(n, out _); }
            }));
        }

        private static async Task<bool> RunTexOneAsync(ClientAssetsConfig cfg, string upkPath, string leaf, string outPng)
        {
            // Resolve through ClientAssetToolPaths rather than trusting the
            // configured path directly: the config default points at a
            // Tools/UpkExtract folder next to the server, which doesn't exist
            // in a repo build. The resolver also checks the repo's build
            // output, which is where the exe actually lives during development.
            //
            // Without this the inline-mip path silently failed for every
            // texture that isn't in the TFC manifest — i.e. all the hero
            // banners — while /webapi/clientassets/status reported the tool as
            // present, because status already used the resolver.
            string exe = ClientAssetToolPaths.ResolveUpkExtract(cfg.UpkExtractPath);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Logger.Warn($"TexByName: extractor not found (configured '{cfg.UpkExtractPath}', resolved '{exe}')");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("texone");
            psi.ArgumentList.Add(upkPath);
            psi.ArgumentList.Add(leaf);
            psi.ArgumentList.Add(outPng);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ExtractionTimeoutSeconds));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Logger.Warn($"TexByName: extractor timed out on {leaf}");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                string err = (await proc.StandardError.ReadToEndAsync()).Trim();
                Logger.Trace($"TexByName: extractor exit {proc.ExitCode} on {leaf}: {err}");
                return false;
            }
            return true;
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            return sb.ToString();
        }
    }
}
