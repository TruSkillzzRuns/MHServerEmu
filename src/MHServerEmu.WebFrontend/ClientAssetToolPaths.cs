using System;
using System.IO;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.WebFrontend
{
    /// <summary>
    /// Locates the UpkExtract / TfcExtract helper executables.
    ///
    /// Why this is not just a config value: the config defaults are RELATIVE
    /// ("Tools/UpkExtract/UpkExtract.exe") and resolve against the server's
    /// working directory. That is correct for a deployed install, where the
    /// OmegaDev2 Setup page builds the tools and drops them next to the server.
    /// It is wrong when running from a source build, where the tools live in
    /// the repo's own bin folders — and the natural workaround, hardcoding an
    /// absolute path into Config.ini, produces a config that only works on the
    /// machine it was written on.
    ///
    /// So: honour an explicit configured path when it actually exists, and
    /// otherwise search the places the tools genuinely live. That keeps a
    /// shipped Config.ini portable between machines.
    /// </summary>
    public static class ClientAssetToolPaths
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public static string ResolveUpkExtract(string configured)
            => Resolve(configured, "UpkExtract");

        public static string ResolveTfcExtract(string configured)
            => Resolve(configured, "TfcExtract");

        /// <summary>
        /// Returns the first existing candidate, or an empty string when the
        /// tool genuinely is not present anywhere.
        /// </summary>
        private static string Resolve(string configured, string toolName)
        {
            string exeName = toolName + ".exe";

            // 1. An explicit path that exists always wins.
            if (string.IsNullOrWhiteSpace(configured) == false)
            {
                string full = SafeFullPath(configured);
                if (full.Length > 0 && File.Exists(full))
                    return full;
            }

            string baseDir = AppContext.BaseDirectory;

            // 2. Deployed layout: Tools/<tool>/<tool>.exe next to the server.
            //    This is what Setup produces and what the defaults assume.
            string deployed = Path.Combine(baseDir, "Tools", toolName, exeName);
            if (File.Exists(deployed)) return deployed;

            // 3. Source-build layout: walk up to the repo root and look in the
            //    tool's own build output, then in the per-version build folders.
            string repoRoot = FindRepoRoot(baseDir);
            if (repoRoot != null)
            {
                foreach (string candidate in EnumerateRepoCandidates(repoRoot, toolName, exeName))
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }

            return "";
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateRepoCandidates(string repoRoot, string toolName, string exeName)
        {
            // tools/<tool>/bin/<config>/<tfm>/<rid>/<tool>.exe — enumerated
            // rather than hardcoded because the framework/RID moves with the
            // project (net8.0-windows -> net10.0-windows already happened once).
            string toolBin = Path.Combine(repoRoot, "tools", toolName, "bin");
            if (Directory.Exists(toolBin))
            {
                string[] found;
                try { found = Directory.GetFiles(toolBin, exeName, SearchOption.AllDirectories); }
                catch { found = Array.Empty<string>(); }

                // Prefer Release over Debug when both exist.
                Array.Sort(found, (a, b) => b.Contains("Release", StringComparison.OrdinalIgnoreCase)
                    .CompareTo(a.Contains("Release", StringComparison.OrdinalIgnoreCase)));

                foreach (string f in found) yield return f;
            }

            // build/<version>/Tools/<tool>/<tool>.exe — the deployed per-version
            // server folders, which already carry the tools.
            string buildDir = Path.Combine(repoRoot, "build");
            if (Directory.Exists(buildDir))
            {
                foreach (string versionDir in Directory.GetDirectories(buildDir))
                    yield return Path.Combine(versionDir, "Tools", toolName, exeName);
            }
        }

        /// <summary>Walks up from the server binary looking for the repo root.</summary>
        private static string FindRepoRoot(string startDir)
        {
            DirectoryInfo dir = new(startDir);

            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "tools")) &&
                    File.Exists(Path.Combine(dir.FullName, "MHServerEmu.sln")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            return null;
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return ""; }
        }
    }
}
