using MHServerEmu.Core.Config;

namespace MHServerEmu.WebFrontend
{
    // Lazy ground-texture extraction (Phase 2B/2D). Off when CookedPCConsolePath
    // is empty — the /webapi/groundtex endpoint returns 503 and the 3D viewer
    // falls back to its canonical bundled tile.
    public class ClientAssetsConfig : ConfigContainer
    {
        // Absolute path to the client's CookedPCConsole directory. Must hold
        // the .upk files we're sampling diffuse textures from.
        public string CookedPCConsolePath { get; private set; } = "";

        // Extra CookedPCConsole roots searched if the .upk isn't found in the
        // primary path above. Useful when assets for some cells live in an
        // older client build (e.g. test-center 1.34 Knowhere upks) that's
        // kept separate from the main 1.53 client tree. Semicolon-separated.
        // The TFC manifest used for fall-through is the one that sits in the
        // SAME root the matching .upk was found in.
        public string CookedPCConsoleAlternatePaths { get; private set; } = "";

        // Where to keep extracted PNGs. Relative to the server's working dir.
        public string GroundTexCacheDirectory { get; private set; } = "Cache/groundTex";

        // Path to the helper exe that does the actual UpkManager parse + decode.
        // Spawned per-request when the cache misses.
        public string UpkExtractPath { get; private set; } = "Tools/UpkExtract/UpkExtract.exe";

        // Per-call timeout — most diffuse extractions finish in <1s, but bigger
        // .upks or first-time LZO warmup can stretch. Hard stop above this.
        public int ExtractionTimeoutSeconds { get; private set; } = 15;

        // TFC fall-through: when the inline-mip diffuse extraction yields no
        // bytes (texture's pixel data lives in a .tfc cache like Knowhere),
        // we ask UpkExtract for metadata then run TfcExtract.exe against the
        // manifest to pull the HD mip out and write PNG.
        //
        // Default: <CookedPCConsolePath>/TextureFileCacheManifest.bin (resolved
        // in the handler when this is empty). Override here if the manifest
        // lives elsewhere on disk.
        public string TfcManifestPath { get; private set; } = "";

        // Path to TfcExtract.exe (sibling of UpkExtract.exe in the same
        // Tools/ tree). Spawned only on TFC fall-through, never per inline-mip
        // request.
        public string TfcExtractPath { get; private set; } = "Tools/TfcExtract/TfcExtract.exe";
    }
}
