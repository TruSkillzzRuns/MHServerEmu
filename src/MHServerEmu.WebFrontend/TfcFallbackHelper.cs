using System.Diagnostics;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.WebFrontend
{
    // Phase 4G shared helper: probe the TextureFileCacheManifest.bin for a
    // texture (by leaf or full PathName), then shell TfcExtract.exe to
    // decompress its mip0 and emit a PNG. Both GroundTexWebHandler and
    // TextureByNameWebHandler rely on this fallback for any texture whose
    // bytes were cooked out of the .upk and into a .tfc (notably HD packs
    // like Knowhere). One implementation, two call sites.
    //
    // Defaults match the GroundTex flow: 1024×1024 DXT5. Manifest doesn't
    // carry dimensions or pixel format so format mismatches yield a
    // garbled-but-non-empty PNG; the caller still serves them in preference
    // to a 404.
    internal static class TfcFallbackHelper
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // Returns the path to a freshly-extracted PNG on success, or null
        // if the texture is not in the TFC manifest, the extractor errors,
        // or any tool path is missing. `needle` may be a leaf name
        // (e.g. "NordicRuin_Diffuse") or a fully-qualified PathName
        // (e.g. "Lighting.NordicRuin_Diffuse") — TfcExtract --has matches
        // both. `outPngPath` is the cache target; caller owns its lifetime.
        public static async Task<string> TryExtractByTextureNameAsync(
            ClientAssetsConfig cfg, string needle, string outPngPath, string contextLabel)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(needle) || string.IsNullOrWhiteSpace(outPngPath))
                return null;

            string manifestPath = !string.IsNullOrWhiteSpace(cfg.TfcManifestPath)
                ? cfg.TfcManifestPath
                : Path.Combine(cfg.CookedPCConsolePath ?? "", "TextureFileCacheManifest.bin");
            if (!File.Exists(manifestPath))
            {
                Logger.Trace($"TfcFallback[{contextLabel}]: manifest not found at {manifestPath}");
                return null;
            }

            // Resolved so a config written on another machine still works.
            string exe = ClientAssetToolPaths.ResolveTfcExtract(cfg.TfcExtractPath);
            if (!File.Exists(exe))
            {
                Logger.Warn($"TfcFallback[{contextLabel}]: TfcExtract not found at {exe}");
                return null;
            }

            // Cheap existence check first — avoids paying the full decode
            // path for the common "leaf simply isn't in TFC" case.
            var hit = await RunHasAsync(exe, manifestPath, needle, cfg.ExtractionTimeoutSeconds);
            if (hit == null)
            {
                Logger.Trace($"TfcFallback[{contextLabel}]: '{needle}' not in TFC manifest");
                return null;
            }

            // Disambiguation log: caller asked for a leaf and TFC resolved
            // it to one specific full path. Future enhancement: accept an
            // explicit ?package= override.
            if (!string.Equals(hit.Value.fullName, needle, StringComparison.OrdinalIgnoreCase))
                Logger.Trace($"TfcFallback[{contextLabel}]: '{needle}' -> '{hit.Value.fullName}' (tfc={hit.Value.tfcName})");

            Directory.CreateDirectory(Path.GetDirectoryName(outPngPath)!);
            string ddsSidecar = outPngPath + ".tfc.dds";
            try
            {
                bool ok = await RunExtractAsync(exe, manifestPath, hit.Value.fullName, ddsSidecar, outPngPath, cfg.ExtractionTimeoutSeconds, contextLabel);
                if (!ok) return null;
                return File.Exists(outPngPath) && new FileInfo(outPngPath).Length > 0 ? outPngPath : null;
            }
            finally
            {
                try { if (File.Exists(ddsSidecar)) File.Delete(ddsSidecar); } catch { }
            }
        }

        // Returns (tfcName, mip0Size, mipCount, fullName) or null. Output
        // schema matches `TfcExtract --has` which writes one tab-separated
        // line on success.
        private static async Task<(string tfcName, ulong mip0Size, int mipCount, string fullName)?> RunHasAsync(
            string exe, string manifestPath, string needle, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(manifestPath);
            psi.ArgumentList.Add("--has");
            psi.ArgumentList.Add(needle);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
            string stdout;
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(cts.Token);
                stdout = (await stdoutTask).Trim();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(stdout)) return null;
            string[] parts = stdout.Split('\t');
            if (parts.Length < 4) return null;
            ulong.TryParse(parts[1], out ulong sz);
            int.TryParse(parts[2], out int mc);
            return (parts[0], sz, mc, parts[3]);
        }

        private static async Task<bool> RunExtractAsync(
            string exe, string manifestPath, string textureFullName,
            string ddsOut, string pngOut, int timeoutSeconds, string contextLabel)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(manifestPath);
            psi.ArgumentList.Add(textureFullName);
            psi.ArgumentList.Add(ddsOut);
            psi.ArgumentList.Add("--fmt"); psi.ArgumentList.Add("DXT5");
            psi.ArgumentList.Add("--w");   psi.ArgumentList.Add("1024");
            psi.ArgumentList.Add("--h");   psi.ArgumentList.Add("1024");
            psi.ArgumentList.Add("--png-out"); psi.ArgumentList.Add(pngOut);

            using var proc = Process.Start(psi);
            if (proc == null) { Logger.Warn($"TfcFallback[{contextLabel}]: failed to start TfcExtract"); return false; }
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Logger.Warn($"TfcFallback[{contextLabel}]: TfcExtract timed out on '{textureFullName}'");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                string err = (await proc.StandardError.ReadToEndAsync()).Trim();
                Logger.Trace($"TfcFallback[{contextLabel}]: TfcExtract exit {proc.ExitCode} on '{textureFullName}': {err}");
                return false;
            }
            return true;
        }
    }
}
