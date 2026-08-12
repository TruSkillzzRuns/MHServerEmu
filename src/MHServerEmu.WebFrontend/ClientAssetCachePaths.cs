using System;
using System.IO;
using MHServerEmu.Core.Serialization;

namespace MHServerEmu.WebFrontend
{
    /// <summary>
    /// Where derived client-asset data (texture indexes, decoded PNGs) is kept.
    ///
    /// Two rules this exists to enforce:
    ///
    ///   1. NOTHING derived from the user's game client is written inside the
    ///      repository. Both MHServerEmu and OmegaDev2 are public, and an index
    ///      or a decoded texture is game-derived data. Everything therefore
    ///      lives under %LocalAppData%\MHServerEmu\ClientAssets, outside both
    ///      working trees, where no git operation can pick it up.
    ///
    ///      The previous behaviour wrote Cache/texIndex.json relative to the
    ///      server's working directory, which is inside the repo tree.
    ///
    ///   2. Caches are scoped PER GAME VERSION. 1.48, 1.52 and 1.53 ship
    ///      different art under the same asset names, so a single shared cache
    ///      would serve 1.48 art to a 1.53 client (or vice versa) depending on
    ///      which server warmed it first. The version segment makes that
    ///      impossible rather than merely unlikely.
    /// </summary>
    public static class ClientAssetCachePaths
    {
        /// <summary>
        /// Short game-version tag ("1.48", "1.52", "1.53") used to keep the
        /// three servers' caches apart. Derived from the archive version the
        /// build targets, so it follows the build rather than needing config.
        /// </summary>
        public static string GameVersionTag { get; } = ResolveVersionTag();

        /// <summary>
        /// %LocalAppData%\MHServerEmu\ClientAssets\&lt;version&gt; — created on demand.
        /// </summary>
        public static string RootDirectory { get; } = BuildRoot();

        /// <summary>Global texture-name -&gt; package index for this game version.</summary>
        public static string TextureIndexPath => Path.Combine(RootDirectory, "texIndex.json");

        /// <summary>Decoded PNG cache for this game version.</summary>
        public static string PortraitCacheDirectory => Path.Combine(RootDirectory, "portraits");

        /// <summary>Extracted ground textures for this game version.</summary>
        public static string GroundTexCacheDirectory => Path.Combine(RootDirectory, "groundTex");

        public static string EnsureDirectory(string path)
        {
            try { Directory.CreateDirectory(path); } catch { /* surfaced by the caller's own IO */ }
            return path;
        }

        private static string ResolveVersionTag()
        {
#if GAME_VERSION_1_48
            return "1.48";
#elif GAME_VERSION_1_53
            return "1.53";
#elif GAME_VERSION_1_52
            return "1.52";
#else
            // No build constant: fall back to the archive version so the cache
            // is still separated rather than silently shared.
            return ((int)ArchiveVersion.Current).ToString();
#endif
        }

        private static string BuildRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, "MHServerEmu", "ClientAssets", GameVersionTag);
            EnsureDirectory(root);
            return root;
        }
    }
}
