using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.WebFrontend
{
    /// <summary>
    /// Builds the texture/mesh name index from the user's own installed client,
    /// automatically, the first time it is needed.
    ///
    /// Why this exists: resolving art by name (item icons, costume sheet art)
    /// requires an index of every texture in the client's packages. Without one
    /// every lookup 404s and the UI just shows blanks. Previously that index had
    /// to be produced by a manual setup step, so a fresh install silently had no
    /// images at all until someone knew to run it.
    ///
    /// The index is derived from the user's client and is therefore written to
    /// %LocalAppData%, never into the repository — see ClientAssetCachePaths.
    ///
    /// Runs at most once per process, in the background, and only when
    /// CookedPCConsolePath and UpkExtractPath are both configured. A missing
    /// tool or path is a normal "not set up yet" state, not an error.
    /// </summary>
    public static class ClientAssetIndexBuilder
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly SemaphoreSlim s_gate = new(1, 1);
        private static bool s_attempted;

        public static bool IndexExists => File.Exists(ClientAssetCachePaths.TextureIndexPath);

        /// <summary>
        /// Kicks off an index build if one is needed. Returns immediately;
        /// safe to call repeatedly.
        /// </summary>
        public static void EnsureBuiltInBackground()
        {
            if (IndexExists || s_attempted) return;
            _ = Task.Run(BuildAsync);
        }

        public static async Task<bool> BuildAsync()
        {
            if (IndexExists) return true;

            await s_gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IndexExists) return true;
                if (s_attempted) return false;      // don't retry a failed build every request
                s_attempted = true;

                var cfg = ConfigManager.Instance.GetConfig<ClientAssetsConfig>();
                if (cfg == null) return false;

                string cooked = cfg.CookedPCConsolePath ?? "";
                string tool = cfg.UpkExtractPath ?? "";

                if (string.IsNullOrWhiteSpace(cooked) || !Directory.Exists(cooked))
                {
                    Logger.Info("ClientAssetIndex: CookedPCConsolePath not set — skipping index build (item icons and costume art will be unavailable).");
                    return false;
                }

                // Resolved rather than taken literally, so a Config.ini written
                // on one machine still works on another.
                string toolPath = ClientAssetToolPaths.ResolveUpkExtract(tool);
                if (string.IsNullOrWhiteSpace(toolPath))
                {
                    Logger.Info("ClientAssetIndex: UpkExtract not found (checked config, Tools/ next to the server, and the repo build output) — skipping index build.");
                    return false;
                }

                string outDir = ClientAssetCachePaths.EnsureDirectory(ClientAssetCachePaths.RootDirectory);

                // UpkExtract writes meshIndex.json to the path given, then puts
                // texIndex.json and classMeshIndex.json alongside it. Passing a
                // directory would fail — it opens the destination as a file.
                string meshIndexPath = Path.Combine(outDir, "meshIndex.json");

                Logger.Info($"ClientAssetIndex: building {ClientAssetCachePaths.GameVersionTag} index from {cooked} (one time, ~15-60s)...");

                var psi = new ProcessStartInfo
                {
                    FileName = toolPath,
                    WorkingDirectory = Path.GetDirectoryName(toolPath) ?? ".",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("indexmeshes");
                psi.ArgumentList.Add(cooked);
                psi.ArgumentList.Add(meshIndexPath);

                using Process proc = Process.Start(psi);
                if (proc == null)
                {
                    Logger.Warn("ClientAssetIndex: failed to start UpkExtract.");
                    return false;
                }

                string lastLine = "";
                proc.OutputDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) lastLine = e.Data; };
                proc.ErrorDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) Logger.Trace($"ClientAssetIndex: {e.Data}"); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await proc.WaitForExitAsync().ConfigureAwait(false);

                if (proc.ExitCode != 0 || IndexExists == false)
                {
                    Logger.Warn($"ClientAssetIndex: build failed (exit {proc.ExitCode}). {lastLine}");
                    return false;
                }

                Logger.Info($"ClientAssetIndex: {ClientAssetCachePaths.GameVersionTag} index ready — {lastLine}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ClientAssetIndex: build error — {ex.Message}");
                return false;
            }
            finally
            {
                s_gate.Release();
            }
        }
    }
}
