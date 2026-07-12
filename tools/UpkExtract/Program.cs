using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UpkManager.Constants;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile.Compression;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace UpkExtract;

// Modes:
//   1) UpkExtract.exe <upkPath> <outDir>
//        Dumps every parsable Texture2D in the .upk as PNG.
//   2) UpkExtract.exe diffuse <upkPath> <outPngPath>
//        Picks ONE texture (largest 2D, non-normal/non-spec/non-mask) and
//        writes it to outPngPath. Used by the server's lazy ground-texture
//        cache for the Region/World 3D viewer. Exit 0 ok, 2 not found, 3
//        no usable texture, 4 io/parse error.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 3 && string.Equals(args[0], "diffuse", StringComparison.OrdinalIgnoreCase))
            return await RunDiffuseAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "groundtex-meta", StringComparison.OrdinalIgnoreCase))
            return await RunGroundTexMetaAsync(args[1], args[2]);

        if (args.Length >= 2 && string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
            return await RunProbeAsync(args[1]);

        if (args.Length >= 2 && string.Equals(args[0], "meshprobe", StringComparison.OrdinalIgnoreCase))
            return await RunMeshProbeAsync(args[1]);

        if (args.Length >= 2 && string.Equals(args[0], "matprobe", StringComparison.OrdinalIgnoreCase))
            return await RunMatProbeAsync(args[1]);

        if (args.Length >= 3 && string.Equals(args[0], "indexmeshes", StringComparison.OrdinalIgnoreCase))
            return await RunIndexMeshesAsync(args[1], args[2]);

        if (args.Length >= 4 && string.Equals(args[0], "meshone", StringComparison.OrdinalIgnoreCase))
            return await RunMeshOneAsync(args[1], args[2], args[3]);

        if (args.Length >= 4 && string.Equals(args[0], "texone", StringComparison.OrdinalIgnoreCase))
            return await RunTexOneAsync(args[1], args[2], args[3]);

        if (args.Length >= 3 && string.Equals(args[0], "mesh", StringComparison.OrdinalIgnoreCase))
            return await RunMeshAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "levelinst", StringComparison.OrdinalIgnoreCase))
            return await RunLevelInstAsync(args[1], args[2]);

        // Long-running daemon: accepts tab-separated commands on stdin,
        // writes a single status line per command to stdout. Keeps the
        // UpkFileRepository (and packages.db index) loaded across calls,
        // eliminating the ~250ms CLR cold-start + index-load cost that
        // dominates one-shot invocations. Protocol:
        //   stdin : "<cmd>\t<arg1>\t<arg2>\n"   (e.g. "levelinst\tC:\\a.upk\tC:\\b.json")
        //   stdout: "OK\n" on success, "ERR <message>\n" on failure
        //   stdin EOF or "exit\n" → process exits cleanly with code 0
        // Single-threaded by design; callers run a pool of daemons for
        // parallelism (LevelInstancesDaemonPool on the server side).
        if (args.Length >= 1 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase))
            return await RunDaemonAsync();

        if (args.Length >= 2 && string.Equals(args[0], "smcprobe", StringComparison.OrdinalIgnoreCase))
            return await RunSmcProbeAsync(args[1]);

        if (args.Length >= 3 && string.Equals(args[0], "smcfullprobe", StringComparison.OrdinalIgnoreCase))
            return await RunSmcFullProbeAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "smccompare", StringComparison.OrdinalIgnoreCase))
            return await RunSmcCompareAsync(args[1], args[2]);

        if (args.Length >= 4 && string.Equals(args[0], "animone", StringComparison.OrdinalIgnoreCase))
            return await RunAnimOneAsync(args[1], args[2], args[3]);

        if (args.Length >= 3 && string.Equals(args[0], "animlist", StringComparison.OrdinalIgnoreCase))
            return await RunAnimListAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indexanims", StringComparison.OrdinalIgnoreCase))
            return await RunIndexAnimsAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indexmaterials", StringComparison.OrdinalIgnoreCase))
            return await RunIndexMaterialsAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indexpackages", StringComparison.OrdinalIgnoreCase))
            return await RunIndexPackagesAsync(args[1], args[2]);

        if (args.Length >= 4 && string.Equals(args[0], "matone", StringComparison.OrdinalIgnoreCase))
            return await RunMatOneAsync(args[1], args[2], args[3]);

        if (args.Length >= 2 && string.Equals(args[0], "umatprobe", StringComparison.OrdinalIgnoreCase))
            return await RunUMatProbeAsync(args[1]);

        if (args.Length >= 2 && string.Equals(args[0], "importprobe", StringComparison.OrdinalIgnoreCase))
            return await RunImportProbeAsync(args[1]);

        if (args.Length >= 2 && string.Equals(args[0], "smcouterprobe", StringComparison.OrdinalIgnoreCase))
            return await RunSmcOuterProbeAsync(args[1]);

        if (args.Length >= 2 && string.Equals(args[0], "smcaprobe", StringComparison.OrdinalIgnoreCase))
            return await RunSmcaProbeAsync(args[1]);

        if (args.Length >= 3 && string.Equals(args[0], "emitterbytes", StringComparison.OrdinalIgnoreCase))
            return await RunEmitterBytesAsync(args[1], args[2]);

        if (args.Length >= 2 && string.Equals(args[0], "lmprobe", StringComparison.OrdinalIgnoreCase))
            return await RunLightMapProbeAsync(args[1], args.Length >= 3 ? args[2] : null);

        if (args.Length >= 3 && string.Equals(args[0], "findmat", StringComparison.OrdinalIgnoreCase))
            return await RunFindMatAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indexcells", StringComparison.OrdinalIgnoreCase))
            return await RunIndexCellsAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indexnames", StringComparison.OrdinalIgnoreCase))
            return await RunIndexNamesAsync(args[1], args[2]);

        if (args.Length >= 3 && string.Equals(args[0], "indeximports", StringComparison.OrdinalIgnoreCase))
            return await RunIndexImportsAsync(args[1], args[2]);

        // TR32-A — UPK Inspector. X-ray a .upk: summary header + name/import/
        // export tables, plus optional per-export deep dive. Built as the
        // diagnostic foundation for the mesh-swap project; runs alongside
        // every patch attempt so we can diff before/after deterministically.
        //   inspect      <upk>                        full tables overview
        //   inspect      <upk> <export>               same + deep dump of one export
        if (args.Length >= 2 && string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
            return await RunInspectAsync(args[1], args.Length >= 3 ? args[2] : null);

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  UpkExtract.exe <upkPath> <outDir>");
            Console.Error.WriteLine("  UpkExtract.exe diffuse <upkPath> <outPngPath>");
            Console.Error.WriteLine("  UpkExtract.exe groundtex-meta <upkPath> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe probe      <upkPath>");
            Console.Error.WriteLine("  UpkExtract.exe meshprobe  <upkPath>");
            Console.Error.WriteLine("  UpkExtract.exe mesh        <upkPath> <outMhmpPath>");
            Console.Error.WriteLine("  UpkExtract.exe indexmeshes <cookedDir> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe meshone     <upkPath> <exportName> <outMhmpPath>");
            Console.Error.WriteLine("  UpkExtract.exe texone      <upkPath> <textureLeafName> <outPngPath>");
            Console.Error.WriteLine("  UpkExtract.exe levelinst   <upkPath> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe indexcells  <cookedDir> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe indexnames  <cookedDirsSemi> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe indeximports <cookedDirsSemi> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe animone     <upkPath> <animSeqName> <outMhapPath>");
            Console.Error.WriteLine("  UpkExtract.exe animlist    <upkPath> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe indexanims  <cookedDir> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe indexmaterials <cookedDir> <outJsonPath>");
            Console.Error.WriteLine("  UpkExtract.exe inspect     <upkPath> [exportName]");
            return 1;
        }
        return await RunDumpAllAsync(args[0], args[1]);
    }

    // ── Mode 3: probe — print each Texture2D's SizeX/Y, format, TFC name,
    //            and inline-mip sizes. Tells us whether HD mips even exist
    //            in the cooked .upk or only as TFC streams.
    private static async Task<int> RunProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }

        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        Console.WriteLine($"=== {Path.GetFileName(upkPath)} ===");
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
            if (cls != "texture2d") continue;
            string name = (e.ObjectNameIndex?.Name ?? "noname");
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D tex) { Console.WriteLine($"  {name}: not parsable"); continue; }
                string tfc = tex.TextureFileCacheName?.Name ?? "(none)";
                var guid = tex.TextureFileCacheGuid;
                Console.Write($"  {name}: serialOff=0x{e.SerialDataOffset:X} serialSize={e.SerialDataSize}  declared {tex.SizeX}x{tex.SizeY} {tex.Format}  TFC={tfc}  guid={guid}  mipArrayOff=0x{tex.MipArrayOffset:X}  mips=[");
                if (tex.Mips != null)
                {
                    bool first = true;
                    foreach (var m in tex.Mips)
                    {
                        if (!first) Console.Write(", ");
                        first = false;
                        int bytes = m?.Data?.Length ?? 0;
                        Console.Write($"{m.SizeX}x{m.SizeY}={bytes}b flags=0x{m.BulkDataFlags:X} off={m.BulkCompressedOffset} csz={m.BulkCompressedSize}");
                    }
                }
                Console.WriteLine("]");

                // Dump 80 bytes starting at mipArrayOffset — that's where the
                // Mips UArray<FTexture2DMipMap> begins inside the decompressed
                // export buffer. Layout per FTexture2DMipMap.ReadMipMap is:
                //   [4 BulkDataFlags][4 ElementCount][4 SizeOnDisk][4 OffsetInFile]
                //   ...optional inline body...
                //   [4 SizeX][4 SizeY]
                // We want to see what's REALLY in those bytes for the streamed
                // (StoreInSeparateFile) mips, because the parser shows -1/-1.
                if (name == "rock_dif" && e.UnrealObjectReader != null)
                {
                    var raw = e.UnrealObjectReader.GetBytes();
                    int start = tex.MipArrayOffset;
                    Console.WriteLine($"    raw bytes [+0x{start:X}..+0x{start+128:X}]:");
                    // First int32 at MipArrayOffset is the UArray element count
                    int count = BitConverter.ToInt32(raw, start);
                    Console.WriteLine($"    UArray count = {count}");
                    for (int i = 0; i < 128 && start + i < raw.Length; i += 16)
                    {
                        Console.Write($"      +{(start + i):X4}:");
                        for (int j = 0; j < 16 && start + i + j < raw.Length; j++)
                            Console.Write($" {raw[start + i + j]:X2}");
                        Console.WriteLine();
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  {name}: err {ex.GetType().Name}: {ex.Message}"); }
        }
        return 0;
    }

    // TR32-A — UPK Inspector. Dumps the package's full structural anatomy as
    // human-readable text. This is the X-ray we use BEFORE and AFTER any
    // patch attempt so we can diff what changed. Three sections:
    //   1. Summary header — magic, versions, table counts/offsets, flags
    //   2. Tables — names, imports, exports (filtered + grouped)
    //   3. Optional per-export deep dive — class, parent, serial range, dep refs
    private static async Task<int> RunInspectAsync(string upkPath, string exportFilter)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        var fi = new FileInfo(upkPath);
        Console.WriteLine($"### UPK INSPECT  {Path.GetFileName(upkPath)}  ({fi.Length:N0} bytes)");
        Console.WriteLine();
        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"  NameTable    : count={header.NameTableCount}    offset=0x{header.NameTableOffset:X8}");
        Console.WriteLine($"  ImportTable  : count={header.ImportTableCount}  offset=0x{header.ImportTableOffset:X8}");
        Console.WriteLine($"  ExportTable  : count={header.ExportTableCount}  offset=0x{header.ExportTableOffset:X8}");
        Console.WriteLine();

        // --- Name table — full dump. The patcher needs to look up names by
        // string, so callers grep this list; truncation is a footgun.
        Console.WriteLine("=== NAME TABLE ===");
        for (int i = 0; i < header.NameTable.Count; i++)
        {
            var n = header.NameTable[i];
            string s = n?.Name?.String ?? "<null>";
            Console.WriteLine($"  [{i,5}]  {s}");
        }
        Console.WriteLine();

        // --- Import table ---
        Console.WriteLine("=== IMPORT TABLE ===  (all)");
        for (int i = 0; i < header.ImportTable.Count; i++)
        {
            var imp = header.ImportTable[i];
            string cls = SafeName(imp?.ClassNameIndex);
            string obj = SafeName(imp?.ObjectNameIndex);
            string pkg = SafeName(imp?.PackageNameIndex);
            // Negative index is the convention for ImportTable refs.
            Console.WriteLine($"  [{-(i+1),6}]  {cls,-24} {pkg}.{obj}");
        }
        Console.WriteLine();

        // --- Export table ---
        // Prioritise UClass / SkeletalMesh / Component templates — those are
        // the things we care about most for the mesh-swap project. Print
        // everything but flag the interesting rows.
        Console.WriteLine("=== EXPORT TABLE ===  (all)");
        for (int i = 0; i < header.ExportTable.Count; i++)
        {
            var ex = header.ExportTable[i];
            string cls = SafeName(ex?.ClassReferenceNameIndex);
            string obj = SafeName(ex?.ObjectNameIndex);
            string outer = ex.OuterReference != 0 ? $" outer=#{ex.OuterReference}" : "";
            bool tag = IsInterestingExport(cls);
            string marker = tag ? "* " : "  ";
            Console.WriteLine($"  {marker}[{i+1,6}]  {cls,-24} {obj}   serialOff=0x{ex.SerialDataOffset:X8} size={ex.SerialDataSize}{outer}");
        }
        Console.WriteLine();

        // --- Per-export deep dive (optional) ---
        if (!string.IsNullOrWhiteSpace(exportFilter))
        {
            Console.WriteLine($"=== EXPORT DEEP DIVE  '{exportFilter}' ===");
            var hits = header.ExportTable
                .Where(e => string.Equals(e?.ObjectNameIndex?.Name, exportFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0)
            {
                Console.WriteLine($"  no export named '{exportFilter}' (try one of the names above)");
                return 0;
            }
            foreach (var ex in hits)
            {
                string cls = SafeName(ex?.ClassReferenceNameIndex);
                Console.WriteLine($"  -- {cls} {SafeName(ex?.ObjectNameIndex)}  serialOff=0x{ex.SerialDataOffset:X8} size={ex.SerialDataSize}");
                Console.WriteLine($"     OuterReference={ex.OuterReference}  ArchetypeReference={ex.ArchetypeReference}  ObjectFlags=0x{ex.ObjectFlags:X16}");
                // Raw serial bytes — first 256, hex+ASCII. Lets us spot
                // human-readable property tags (e.g. "SkeletalMesh") inline.
                try
                {
                    if (ex.UnrealObjectReader == null) await ex.ParseUnrealObject(false, false);
                    var raw = ex.UnrealObjectReader?.GetBytes();
                    int dumpLen = Math.Min(256, raw?.Length ?? 0);
                    Console.WriteLine($"     raw serial bytes [0..{dumpLen}]:");
                    for (int off = 0; off < dumpLen; off += 16)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"       +0x{off:X4}: ");
                        for (int j = 0; j < 16 && off + j < dumpLen; j++) sb.Append($"{raw[off + j]:X2} ");
                        for (int pad = 16 - Math.Min(16, dumpLen - off); pad > 0; pad--) sb.Append("   ");
                        sb.Append(' ');
                        for (int j = 0; j < 16 && off + j < dumpLen; j++)
                        {
                            byte b = raw[off + j];
                            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                        }
                        Console.WriteLine(sb.ToString());
                    }
                }
                catch (Exception ex2) { Console.WriteLine($"     deep-read err: {ex2.GetType().Name}: {ex2.Message}"); }
            }
        }
        return 0;
    }

    // FName.Name (and FObject.Name, which extends FName) is the resolved
    // string after name-table lookup. UnrealString.String is used for plain
    // counted strings (e.g. NameTableEntry.Name).
    private static string SafeName(UpkManager.Models.UpkFile.Tables.FName fn)
    {
        try { return fn?.Name ?? "<null>"; } catch { return "<err>"; }
    }

    private static bool IsInterestingExport(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        switch (cls.ToLowerInvariant())
        {
            case "class":
            case "skeletalmesh":
            case "skeletalmeshcomponent":
            case "staticmesh":
            case "animset":
            case "material":
            case "materialinstanceconstant":
            case "objectproperty":
            case "componenttemplate":
                return true;
            default:
                return false;
        }
    }

    // ── texone — extract one Texture2D to PNG. Caller resolves the host .upk
    //    (typically via the texIndex.json built by indexmeshes) and passes
    //    the upk path + leaf name. We open that .upk, find the named export,
    //    and use BestMipBytesAsync (TFC manifest → HD mip, inline fallback).
    private static async Task<int> RunTexOneAsync(string upkPath, string textureLeafName, string outPng)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        string cookedDir = Path.GetDirectoryName(upkPath) ?? ".";
        string leaf = textureLeafName.Contains('.')
            ? textureLeafName.Substring(textureLeafName.LastIndexOf('.') + 1)
            : textureLeafName;

        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        UnrealExportTableEntry hit = null;
        foreach (var e in header.ExportTable)
        {
            string cn = e.ClassReferenceNameIndex?.Name;
            if (!string.Equals(cn, "Texture2D", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cn, "LightMapTexture2D", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cn, "ShadowMapTexture2D", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(e.ObjectNameIndex?.Name, leaf, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
        }
        if (hit == null) { Console.Error.WriteLine($"no Texture2D/LightMap/ShadowMap '{leaf}' in {Path.GetFileName(upkPath)}"); return 3; }

        try
        {
            if (hit.UnrealObject == null) await header.ReadExportObjectAsync(hit, null);
            if (hit.UnrealObject == null) await hit.ParseUnrealObject(false, false);
            if (hit.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D tex)
            { Console.Error.WriteLine("not a Texture2D"); return 4; }

            TfcManifest manifest = LoadManifestOnce(cookedDir);
            var (bytes, w, h, source) = await BestMipBytesAsync(tex, cookedDir, manifest);
            if (bytes == null) { Console.Error.WriteLine("no decodable mip"); return 3; }
            var rgba = DecodeMip(bytes, w, h, tex.Format);
            if (rgba == null) { Console.Error.WriteLine($"unsupported format {tex.Format}"); return 3; }

            Directory.CreateDirectory(Path.GetDirectoryName(outPng) ?? ".");
            using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
            await img.SaveAsPngAsync(outPng);
            Console.WriteLine($"ok {leaf} {w}x{h} {tex.Format} ({source}) -> {outPng}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"err {ex.GetType().Name}: {ex.Message}"); return 4; }
    }

    // ── indexmeshes — one-time scan over every .upk in a CookedPCConsole dir
    //    that records each StaticMesh export's name (lowercased) → the .upk's
    //    base filename (no extension, no path). Output is a JSON map written
    //    to outJsonPath, consumed by the server's GlobalMeshIndex on startup.
    //
    //    For collisions (same mesh name in multiple .upks — common for
    //    Madripoor_Static and Madripoor_Outdoors using shared assets), the
    //    LAST .upk wins. The viewer doesn't care which copy it gets — UE3
    //    cooks them identically.
    private static async Task<int> RunIndexMeshesAsync(string cookedDir, string outJsonPath)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks in {cookedDir}");

        // Same scan, three outputs: meshIndex.json + texIndex.json +
        // classMeshIndex.json. One pass over export tables is barely slower
        // than meshes-only, so unify them.
        //
        // classMeshIndex maps a UE3 class name (e.g. "ProtagonistHulk") to
        // the skeletal mesh leaf name it uses. Derived by walking
        // SkeletalMeshComponent exports whose containing object is a CDO
        // (Default__<ClassName>) and reading their SkeletalMesh property.
        // This is what unblocks the entity → real-character-mesh path in
        // the 3D viewer — no more guessing leaf names.
        var meshIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var texIndex  = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classMeshIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string baseName = Path.GetFileNameWithoutExtension(path);
                foreach (var e in header.ExportTable)
                {
                    string cls = e.ClassReferenceNameIndex?.Name;
                    string n = e.ObjectNameIndex?.Name;
                    if (string.IsNullOrEmpty(cls) || string.IsNullOrEmpty(n)) continue;
                    if (string.Equals(cls, "StaticMesh", StringComparison.OrdinalIgnoreCase))
                        meshIndex[n] = baseName;
                    else if (string.Equals(cls, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        // SkeletalMesh exports share the by-name index — that lets
                        // /webapi/meshbyname resolve character/NPC meshes too. The
                        // meshone extractor handles the dispatch based on class.
                        meshIndex[n] = baseName;
                    else if (string.Equals(cls, "Texture2D", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(cls, "LightMapTexture2D", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(cls, "ShadowMapTexture2D", StringComparison.OrdinalIgnoreCase))
                        texIndex[n] = baseName;
                    // (Class→mesh mapping handled by co-occurrence below.)
                }

                // Class→mesh co-occurrence: For EVERY .upk that has a
                // SkeletalMesh export, register multiple key variants pointing
                // at the largest skeletal mesh inside (most likely the body
                // rather than a weapon). The server's resolver tries each
                // variant when looking up an entity's UnrealClass.
                //
                // Variants derived from the .upk basename:
                //   - raw basename                         "UC__MarvelAgent_MaggiaHHPistolBase_SF"
                //   - "UC__Marvel<Type>_" prefix stripped  "MaggiaHHPistolBase_SF"
                //   - both "UC__Marvel<Type>_" + "_SF"     "MaggiaHHPistolBase"
                //   - "HUB_" prefix stripped               "Asgard_Civ5Talking_V1"
                //   - any class exports' names also map to the mesh
                //
                // Pick the body skel mesh, not a weapon. Strategy:
                //   1. Filter SkeletalMesh exports whose name matches weapon
                //      keywords (gun, pistol, rifle, bat, hammer, knife, sword,
                //      shield, claw, dagger, axe, bow, staff, weapon).
                //   2. Prefer name containing upk basename root (e.g.
                //      "MaggiaHHPistolBase" upk → prefer "maggia*" over "bat").
                //   3. Fall back to biggest serial size among survivors.
                UnrealExportTableEntry biggest = null;
                string baseLower = baseName.ToLowerInvariant();
                string[] weaponWords = { "weapon", "gun", "pistol", "rifle", "bat_", "_bat", "hammer", "knife", "sword", "shield", "claw", "dagger", "axe_", "_axe", "bow_", "_bow", "staff" };
                UnrealExportTableEntry rootMatch = null;
                int classExportCount = 0;
                var classExportNames = new System.Collections.Generic.List<string>();
                foreach (var e2 in header.ExportTable)
                {
                    string c2 = e2.ClassReferenceNameIndex?.Name ?? "";
                    string n2 = e2.ObjectNameIndex?.Name ?? "";
                    if (string.Equals(c2, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                    {
                        string nLower = n2.ToLowerInvariant();
                        bool isWeaponName = false;
                        foreach (var w in weaponWords)
                            if (nLower.Contains(w)) { isWeaponName = true; break; }
                        if (isWeaponName) continue;
                        if (biggest == null || e2.SerialDataSize > biggest.SerialDataSize) biggest = e2;
                        // Prefer skel mesh whose name shares >=4 chars with the upk's character class root.
                        if (rootMatch == null && nLower.Length >= 4 && baseLower.Contains(nLower.Substring(0, Math.Min(6, nLower.Length))))
                            rootMatch = e2;
                    }
                    // Class exports (the class itself, not instances). Heuristic:
                    // ClassReference == "Class" OR object name matches a typical
                    // Marvel class-name pattern (e.g. MarvelAgent_<X>).
                    if (string.Equals(c2, "Class", StringComparison.OrdinalIgnoreCase) ||
                        n2.StartsWith("Marvel", StringComparison.OrdinalIgnoreCase))
                    {
                        classExportCount++;
                        if (!string.IsNullOrEmpty(n2)) classExportNames.Add(n2);
                    }
                }
                var picked = rootMatch ?? biggest;
                if (picked != null)
                {
                    string meshLeaf = picked.ObjectNameIndex?.Name?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(meshLeaf))
                    {
                        // Register every plausible key.
                        var keys = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        keys.Add(baseName);                              // raw basename
                        string trimmed = baseName;
                        // Strip leading UC__ and any Marvel<Type>_ prefix.
                        if (trimmed.StartsWith("UC__", StringComparison.OrdinalIgnoreCase))
                            trimmed = trimmed.Substring("UC__".Length);
                        if (trimmed.StartsWith("Marvel", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = trimmed.IndexOf('_');
                            if (idx > 0) trimmed = trimmed.Substring(idx + 1);
                        }
                        keys.Add(trimmed);
                        // Strip _SF and _V<digit> suffixes.
                        if (trimmed.EndsWith("_SF", StringComparison.OrdinalIgnoreCase))
                            keys.Add(trimmed.Substring(0, trimmed.Length - 3));
                        var mVer = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(.*?)_V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mVer.Success) keys.Add(mVer.Groups[1].Value);
                        if (trimmed.StartsWith("HUB_", StringComparison.OrdinalIgnoreCase))
                            keys.Add(trimmed.Substring("HUB_".Length));
                        // Add explicit class export names from this upk too.
                        foreach (var cn in classExportNames) keys.Add(cn);

                        foreach (var k in keys)
                        {
                            if (string.IsNullOrWhiteSpace(k)) continue;
                            // First write wins to keep first-occurrence-stable for
                            // ambiguous keys (a class name in multiple upks).
                            classMeshIndex.TryAdd(k, meshLeaf);
                        }
                    }
                }
            }
            catch { /* skip unreadable .upk */ }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 200 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s, {meshIndex.Count} meshes, {texIndex.Count} textures)");
        });
        sw.Stop();

        Console.WriteLine($"done. {done} upks scanned in {sw.Elapsed.TotalSeconds:0}s, {meshIndex.Count} unique mesh names, {texIndex.Count} unique texture names");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(meshIndex.ToDictionary(kv => kv.Key, kv => kv.Value), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(outJsonPath).Length} bytes -> {outJsonPath}");

        // Texture index goes alongside the mesh index under the same Cache dir.
        string texIndexPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "texIndex.json");
        await File.WriteAllTextAsync(texIndexPath,
            System.Text.Json.JsonSerializer.Serialize(texIndex.ToDictionary(kv => kv.Key, kv => kv.Value), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(texIndexPath).Length} bytes -> {texIndexPath}");

        // Class→mesh map (Phase 4H ground truth). Server passes this to the
        // viewer to map each entity's UnrealClass to the actual skel mesh.
        string classMeshPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "classMeshIndex.json");
        await File.WriteAllTextAsync(classMeshPath,
            System.Text.Json.JsonSerializer.Serialize(classMeshIndex.ToDictionary(kv => kv.Key, kv => kv.Value), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(classMeshPath).Length} bytes ({classMeshIndex.Count} class→mesh mappings) -> {classMeshPath}");
        return 0;
    }

    // ── meshone — extract ONE StaticMesh export by name from a specific .upk
    //    into a single-mesh .mhmp pack. Used by the global mesh-by-name web
    //    handler after the index resolves the name to a .upk.
    private static async Task<int> RunMeshOneAsync(string upkPath, string exportName, string outPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        // Accept either StaticMesh or SkeletalMesh — the meshbyname index is
        // shared across both, and the viewer wants the resolved geometry
        // regardless of source. For SkeletalMesh we emit the reference-pose
        // verts as if it were a static mesh (no skinning).
        UnrealExportTableEntry hit = null;
        string hitClass = null;
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (!string.Equals(cls, "StaticMesh", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cls, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(e.ObjectNameIndex?.Name, exportName, StringComparison.OrdinalIgnoreCase))
            { hit = e; hitClass = cls; break; }
        }
        if (hit == null) { Console.Error.WriteLine($"no StaticMesh/SkeletalMesh '{exportName}' in {Path.GetFileName(upkPath)}"); return 3; }

        try
        {
            if (hit.UnrealObject == null) await header.ReadExportObjectAsync(hit, null);
            if (hit.UnrealObject == null) await hit.ParseUnrealObject(false, false);
            if (hit.UnrealObject is not IUnrealObject uo) { Console.Error.WriteLine("export not parsable"); return 4; }

            if (string.Equals(hitClass, "StaticMesh", StringComparison.OrdinalIgnoreCase))
            {
                if (uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMesh sm) { Console.Error.WriteLine("parsed but not UStaticMesh"); return 4; }
                if (sm.LODModels == null || sm.LODModels.Count == 0) { Console.Error.WriteLine("no LOD"); return 3; }
                var m = MeshPack.FromStaticMesh(exportName, sm.LODModels[0]);
                if (m == null || m.Indices.Length == 0) { Console.Error.WriteLine("empty mesh"); return 3; }
                TryResolveAllMaps(sm.LODModels[0],
                    out var d, out var n, out var sp, out var em,
                    out var ss, out var es, out var sr, out var sc);
                m.DiffuseTextureName  = d;
                m.NormalTextureName   = n;
                m.SpecularTextureName = sp;
                m.EmissiveTextureName = em;
                m.SmspskTextureName    = ss;
                m.EspaTextureName      = es;
                m.SmrrTextureName      = sr;
                m.SpecColorTextureName = sc;
                m.Sections = ResolveSections(sm.LODModels[0]);
                MeshPack.WritePack(outPath, new[] { m });
                Console.WriteLine($"ok {exportName}: {m.Positions.Length/3}v/{m.Indices.Length/3}t [d={!string.IsNullOrEmpty(d)} n={!string.IsNullOrEmpty(n)} s={!string.IsNullOrEmpty(sp)} e={!string.IsNullOrEmpty(em)} smspsk={!string.IsNullOrEmpty(ss)} espa={!string.IsNullOrEmpty(es)} smrr={!string.IsNullOrEmpty(sr)} specCol={!string.IsNullOrEmpty(sc)}] -> {outPath}");
                return 0;
            }
            else
            {
                if (uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sk) { Console.Error.WriteLine("parsed but not USkeletalMesh"); return 4; }
                if (sk.LODModels == null || sk.LODModels.Count == 0) { Console.Error.WriteLine("no skeletal LOD"); return 3; }
                var m = MeshPackFromSkeletalLod(exportName, sk.LODModels[0]);
                if (m == null || m.Indices.Length == 0) { Console.Error.WriteLine("empty skel mesh"); return 3; }
                TryResolveSkeletalAllMaps(sk, out var d, out var n, out var sp, out var em);
                m.DiffuseTextureName  = d;
                m.NormalTextureName   = n;
                m.SpecularTextureName = sp;
                m.EmissiveTextureName = em;
                m.Sections = ResolveSkeletalSections(sk, sk.LODModels[0]);
                FillSkinningData(sk, sk.LODModels[0], m);
                MeshPack.WritePack(outPath, new[] { m });
                Console.WriteLine($"ok (skel) {exportName}: {m.Positions.Length/3}v/{m.Indices.Length/3}t [d={!string.IsNullOrEmpty(d)} n={!string.IsNullOrEmpty(n)} s={!string.IsNullOrEmpty(sp)} e={!string.IsNullOrEmpty(em)}] -> {outPath}");
                return 0;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"err {ex.GetType().Name}: {ex.Message}"); return 4; }
    }

    // ── matprobe — for each StaticMesh, walk its sections → Material → diffuse
    //    texture ref. Pre-flight evidence for Phase 4F (per-mesh diffuse).
    private static async Task<int> RunMatProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        // Also enumerate all class types so we know what's actually in the .upk.
        var classCounts = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var e2 in header.ExportTable)
        {
            string c = e2.ClassReferenceNameIndex?.Name ?? "<noclass>";
            classCounts[c] = classCounts.TryGetValue(c, out int existing) ? existing + 1 : 1;
        }
        Console.WriteLine("== export class histogram (top 20) ==");
        foreach (var kv in classCounts.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"   {kv.Value,4}x {kv.Key}");
        Console.WriteLine("== StaticMesh material walk ==");

        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
            if (cls != "staticmesh") continue;
            string name = (e.ObjectNameIndex?.Name ?? "noname");
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo ||
                    uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMesh sm) continue;
                if (sm.LODModels == null || sm.LODModels.Count == 0) continue;
                var lod = sm.LODModels[0];
                Console.WriteLine($"-- {name}: {lod.Elements?.Count ?? 0} section(s)");
                if (lod.Elements == null) continue;
                int si = 0;
                foreach (var el in lod.Elements)
                {
                    string matPath = el.Material?.GetPathName() ?? "<null>";
                    string diffuse = "<unresolved>";
                    try
                    {
                        var matObj = el.Material?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant>();
                        if (matObj != null)
                        {
                            var texFObj = matObj.GetTextureParameterValue("Diffuse")
                                ?? matObj.GetTextureParameterValue("BaseColor")
                                ?? matObj.GetTextureParameterValue("Albedo")
                                ?? matObj.GetTextureParameterValue("Texture");
                            if (texFObj != null) diffuse = texFObj.GetPathName() ?? "<no path>";
                        }
                    }
                    catch (Exception ex) { diffuse = $"<err:{ex.GetType().Name}>"; }
                    Console.WriteLine($"   [{si}] firstIdx={el.FirstIndex} tris={el.NumTriangles}  mat={matPath}  diffuse={diffuse}");
                    si++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"  {name}: err {ex.GetType().Name}: {ex.Message}"); }
        }
        return 0;
    }

    // ── Mode 5: mesh — extract every UStaticMesh's LOD0 into a .mhmp pack ──
    // Phase 4C — real cell geometry in the 3D viewer. Output is a custom
    // binary format (see MeshPack.cs) sized for fast WebView2 fetch.
    private static async Task<int> RunMeshAsync(string upkPath, string outPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }

        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        var collected = new System.Collections.Generic.List<MeshPack.MeshOut>();
        int total = 0, ok = 0, skipped = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
            if (cls != "staticmesh") continue;
            total++;
            string name = (e.ObjectNameIndex?.Name ?? "noname");
            // Skip collision-only meshes — they're invisible nav geometry, not art.
            string nameLower = name.ToLowerInvariant();
            if (nameLower.StartsWith("collision_") || nameLower.Contains("_collision")) { skipped++; continue; }

            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo ||
                    uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMesh sm)
                { skipped++; continue; }
                if (sm.LODModels == null || sm.LODModels.Count == 0) { skipped++; continue; }

                var m = MeshPack.FromStaticMesh(name, sm.LODModels[0]);
                if (m == null || m.Indices.Length == 0) { skipped++; continue; }
                m.DiffuseTextureName = TryResolveFirstDiffuse(sm.LODModels[0]);
                collected.Add(m);
                ok++;
            }
            catch { skipped++; }
        }

        // Cross-package fallback: most cell .upks have NO local StaticMesh exports
        // — they only IMPORT meshes from shared district-art .upks. Walk the
        // ImportTable, look up each StaticMesh name in meshIndex.json (which
        // pre-mapped every mesh export to its hosting .upk basename), open each
        // hosting .upk once, and pull the referenced StaticMesh exports out.
        if (collected.Count == 0)
        {
            string cookedRoot = Path.GetDirectoryName(upkPath) ?? ".";

            // Load meshIndex.json (mesh-name → upk-basename). Look in ./Cache
            // next to the server's working dir first. Silent-null if absent —
            // fall through to the old outer-chain lookup as a last resort.
            System.Collections.Generic.Dictionary<string, string> meshIndex = null;
            string meshIndexPath = Path.Combine(Environment.CurrentDirectory, "Cache", "meshIndex.json");
            if (!File.Exists(meshIndexPath))
                meshIndexPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Cache", "meshIndex.json");
            if (File.Exists(meshIndexPath))
            {
                try
                {
                    string json = File.ReadAllText(meshIndexPath);
                    meshIndex = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
                    if (meshIndex != null)
                        meshIndex = new System.Collections.Generic.Dictionary<string, string>(meshIndex, StringComparer.OrdinalIgnoreCase);
                }
                catch { meshIndex = null; }
            }
            var byPackage = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var imp in header.ImportTable)
            {
                string cls = imp?.ClassNameIndex?.Name ?? "";
                if (!string.Equals(cls, "StaticMesh", StringComparison.OrdinalIgnoreCase)) continue;
                string obj = imp?.ObjectNameIndex?.Name ?? "";
                if (string.IsNullOrEmpty(obj)) continue;
                // Skip collision-only meshes — invisible nav geometry, not art.
                string objLower = obj.ToLowerInvariant();
                if (objLower.StartsWith("collision_") || objLower.Contains("_collision")) continue;
                // PackageNameIndex reports the ROOT UE package ("engine") — useless
                // for file lookup. The actual source .upk is the OUTERMOST Package
                // in the outer chain. Walk it.
                string sourcePackage = null;
                int outerRef = imp.OuterReference;
                int depth = 0;
                while (outerRef != 0 && depth < 8)
                {
                    var outer = header.GetObjectTableEntry(outerRef);
                    if (outer == null) break;
                    string oc = (outer is UnrealImportTableEntry oi ? (oi.ClassNameIndex?.Name ?? "") : "");
                    if (string.Equals(oc, "Package", StringComparison.OrdinalIgnoreCase))
                        sourcePackage = outer.ObjectNameIndex?.Name;
                    outerRef = (outer as UnrealImportTableEntry)?.OuterReference ?? 0;
                    depth++;
                }
                if (string.IsNullOrEmpty(sourcePackage)) continue;
                if (!byPackage.TryGetValue(sourcePackage, out var set))
                    byPackage[sourcePackage] = set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(obj);
            }

            int importsTotal = byPackage.Values.Sum(s => s.Count);
            int importsOk = 0, importsFail = 0;
            // Regroup imports by the ACTUAL hosting .upk (via meshIndex) instead of
            // by the outer-chain package name (which is often inline, not on disk).
            var byHostUpk = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var unresolvedImports = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byPackage)
            {
                foreach (string meshName in kv.Value)
                {
                    string hostUpkBase = null;
                    if (meshIndex != null && meshIndex.TryGetValue(meshName, out string mi))
                        hostUpkBase = mi;
                    if (string.IsNullOrEmpty(hostUpkBase))
                    {
                        // Outer-chain fallback: try <sourcePackage>.upk in cookedRoot.
                        string tryPkg = Path.Combine(cookedRoot, kv.Key + ".upk");
                        if (File.Exists(tryPkg)) hostUpkBase = kv.Key + ".upk";
                    }
                    if (string.IsNullOrEmpty(hostUpkBase)) { unresolvedImports.Add(meshName); continue; }
                    if (!byHostUpk.TryGetValue(hostUpkBase, out var s))
                        byHostUpk[hostUpkBase] = s = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    s.Add(meshName);
                }
            }
            foreach (var kv in byHostUpk)
            {
                // meshIndex stores basename WITHOUT .upk extension.
                string pkgPath = Path.Combine(cookedRoot, kv.Key + ".upk");
                if (!File.Exists(pkgPath)) { importsFail += kv.Value.Count; continue; }
                try
                {
                    var srcHeader = await new UpkFileRepository().LoadUpkFile(pkgPath);
                    await srcHeader.ReadHeaderAsync(null);
                    foreach (string wanted in kv.Value)
                    {
                        UnrealExportTableEntry hit = null;
                        foreach (var e in srcHeader.ExportTable)
                        {
                            string cls = e.ClassReferenceNameIndex?.Name ?? "";
                            if (!string.Equals(cls, "StaticMesh", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.Equals(e.ObjectNameIndex?.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                            hit = e; break;
                        }
                        if (hit == null) { importsFail++; continue; }
                        try
                        {
                            if (hit.UnrealObject == null) await srcHeader.ReadExportObjectAsync(hit, null);
                            if (hit.UnrealObject == null) await hit.ParseUnrealObject(false, false);
                            if (hit.UnrealObject is not IUnrealObject uo ||
                                uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMesh sm)
                            { importsFail++; continue; }
                            if (sm.LODModels == null || sm.LODModels.Count == 0) { importsFail++; continue; }
                            var m = MeshPack.FromStaticMesh(wanted, sm.LODModels[0]);
                            if (m == null || m.Indices.Length == 0) { importsFail++; continue; }
                            m.DiffuseTextureName = TryResolveFirstDiffuse(sm.LODModels[0]);
                            collected.Add(m);
                            importsOk++;
                        }
                        catch { importsFail++; }
                    }
                }
                catch { importsFail += kv.Value.Count; }
            }

            if (importsTotal > 0)
                Console.WriteLine($"import-fallback: {importsOk}/{importsTotal} meshes across {byPackage.Count} package(s) resolved");
        }

        if (collected.Count == 0)
        {
            Console.Error.WriteLine("no extractable static meshes");
            return 3;
        }

        MeshPack.WritePack(outPath, collected.ToArray());
        long sz = new FileInfo(outPath).Length;
        Console.WriteLine($"ok packed {ok}/{total} meshes ({skipped} skipped, +{collected.Count - ok} via imports), {sz} bytes -> {outPath}");
        return 0;
    }

    // ── Mode 4: meshprobe — enumerate UStaticMesh exports with LOD counts ──
    // Phase 4C foundation: tells us how much real geometry exists per cell .upk
    // so we can size the static-mesh extraction work realistically.
    private static async Task<int> RunMeshProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        Console.WriteLine($"=== {Path.GetFileName(upkPath)} ===");
        int total = 0, parsable = 0, totalVerts = 0, totalTris = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
            if (cls != "staticmesh") continue;
            total++;
            string name = (e.ObjectNameIndex?.Name ?? "noname");
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo ||
                    uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMesh sm)
                { Console.WriteLine($"  {name}: not parsable"); continue; }

                parsable++;
                int lodCount = sm.LODModels?.Count ?? 0;
                Console.Write($"  {name}: LODs={lodCount}");
                if (lodCount > 0)
                {
                    var lod0 = sm.LODModels[0];
                    int vc = (int)(lod0.NumVertices);
                    int ic = lod0.IndexBuffer?.Indices?.Count ?? 0;
                    int tc = ic / 3;
                    totalVerts += vc; totalTris += tc;
                    Console.Write($" lod0={vc}v/{tc}t");
                }
                Console.WriteLine();
            }
            catch (Exception ex)
            { Console.WriteLine($"  {name}: err {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine($"=== total: {total} StaticMesh exports, {parsable} parsable, {totalVerts} verts, {totalTris} tris (lod0 only) ===");
        return 0;
    }

    // ── Mode 1: dump everything ──────────────────────────────────────────
    private static async Task<int> RunDumpAllAsync(string upkPath, string outDir)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"opening {upkPath}");
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        int total = 0, ok = 0, skipped = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
            if (cls != "texture2d") continue;
            total++;

            string name = (e.ObjectNameIndex?.Name ?? "noname");
            string safeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D tex) { skipped++; continue; }
                FTexture2DMipMap? best = LargestInlineMip(tex);
                if (best == null) { skipped++; continue; }
                var rgba = DecodeMip(best.Data, best.SizeX, best.SizeY, tex.Format);
                if (rgba == null) { skipped++; continue; }

                using var img = Image.LoadPixelData<Rgba32>(rgba, best.SizeX, best.SizeY);
                string outPath = Path.Combine(outDir, $"{safeName}_{best.SizeX}x{best.SizeY}.png");
                await img.SaveAsPngAsync(outPath);
                ok++;
            }
            catch { skipped++; }
        }

        Console.WriteLine($"done. textures={total} extracted={ok} skipped={skipped}");
        return 0;
    }

    // ── Mode 2: pick one diffuse, write one PNG ──────────────────────────
    // Returns exit codes the server cache uses:
    //   0 success, 2 upk missing, 3 no usable texture found, 4 io/parse error.
    private static async Task<int> RunDiffuseAsync(string upkPath, string outPngPath)
    {
        try
        {
            if (!File.Exists(upkPath))
            {
                Console.Error.WriteLine($"upk not found: {upkPath}");
                return 2;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPngPath) ?? ".");

            var repo = new UpkFileRepository();
            var header = await repo.LoadUpkFile(upkPath);
            await header.ReadHeaderAsync(null);

            // Score each parsable Texture2D and pick the highest score. Scoring
            // prefers explicit diffuse-named textures, then largest declared
            // size. Many candidates only have 64x64 inline mips; the rest live
            // in a .tfc — we score by the DECLARED size so HD TFC mips win.
            (string name, UTexture2D tex)? best = null;
            int bestScore = 0;

            foreach (UnrealExportTableEntry e in header.ExportTable)
            {
                string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
                if (cls != "texture2d") continue;

                string name = (e.ObjectNameIndex?.Name ?? "noname");
                try
                {
                    if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                    if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                    if (e.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D tex) continue;
                    if (tex.Mips == null || tex.Mips.Count == 0) continue;

                    int score = ScoreTexture(name, tex.SizeX, tex.SizeY);
                    if (score <= 0) continue;
                    if (best == null || score > bestScore)
                    { best = (name, tex); bestScore = score; }
                }
                catch { /* try the next export */ }
            }

            if (best == null)
            {
                Console.Error.WriteLine("no usable Texture2D in upk");
                return 3;
            }

            // Pick the highest-resolution mip we can actually obtain. Try the
            // TFC route first (HD), then fall back to the largest inline mip.
            string cookedDir = Path.GetDirectoryName(upkPath) ?? ".";
            TfcManifest manifest = LoadManifestOnce(cookedDir);
            var (mipBytes, mipW, mipH, source) = await BestMipBytesAsync(best.Value.tex, cookedDir, manifest);
            if (mipBytes == null)
            {
                Console.Error.WriteLine("no decodable mip for the chosen texture");
                return 3;
            }

            var rgba = DecodeMip(mipBytes, mipW, mipH, best.Value.tex.Format);
            if (rgba == null)
            {
                Console.Error.WriteLine($"unsupported pixel format: {best.Value.tex.Format}");
                return 3;
            }

            using var img = Image.LoadPixelData<Rgba32>(rgba, mipW, mipH);
            await img.SaveAsPngAsync(outPngPath);

            Console.WriteLine($"ok {best.Value.name} {mipW}x{mipH} {best.Value.tex.Format} ({source}) -> {outPngPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"err: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    // ── groundtex-meta — pick the same "best diffuse" Texture2D RunDiffuseAsync
    //    would pick, then emit JSON describing whether its pixel data lives
    //    inline in the .upk (current happy path) or only in the .tfc.
    //    Consumed by GroundTexWebHandler's TFC fall-through.
    //
    //    Exit 0 always; "found:false" in JSON if no usable Texture2D exists.
    //    Exit 2 only if the .upk itself is missing.
    private static async Task<int> RunGroundTexMetaAsync(string upkPath, string outJsonPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");

        async Task WriteJsonAsync(object payload)
        {
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(outJsonPath, System.Text.Json.JsonSerializer.Serialize(payload, opts));
        }

        try
        {
            var repo = new UpkFileRepository();
            var header = await repo.LoadUpkFile(upkPath);
            await header.ReadHeaderAsync(null);

            (string name, UTexture2D tex, UnrealExportTableEntry export)? best = null;
            int bestScore = 0;
            foreach (UnrealExportTableEntry e in header.ExportTable)
            {
                string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
                if (cls != "texture2d") continue;
                string name = (e.ObjectNameIndex?.Name ?? "noname");
                try
                {
                    if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                    if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                    if (e.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D tex) continue;
                    if (tex.Mips == null || tex.Mips.Count == 0) continue;
                    int score = ScoreTexture(name, tex.SizeX, tex.SizeY);
                    if (score <= 0) continue;
                    if (best == null || score > bestScore) { best = (name, tex, e); bestScore = score; }
                }
                catch { }
            }

            // Always walk materials too — even when we found a Texture2D, the
            // candidate list helps the handler recover if our primary pick
            // turns out to be unresolvable (no inline bytes AND not in TFC).
            var candidates = CollectMaterialTextureCandidates(header);

            if (best == null)
            {
                await WriteJsonAsync(new
                {
                    found = false,
                    candidateTextureNames = candidates.Names,
                    candidateTexturePrefixes = candidates.Prefixes,
                    note = (candidates.Names.Count + candidates.Prefixes.Count) > 0
                        ? $"no usable Texture2D in upk; {candidates.Names.Count} candidate name(s) + {candidates.Prefixes.Count} prefix(es) from materials/imports"
                        : "no usable Texture2D in upk and no material texture refs found"
                });
                Console.WriteLine($"groundtex-meta: not found, {candidates.Names.Count} name(s) {candidates.Prefixes.Count} prefix(es) -> {outJsonPath}");
                return 0;
            }

            var tex2 = best.Value.tex;
            var inline = LargestInlineMip(tex2);
            bool hasInlineBytes = inline != null && inline.Data != null && inline.Data.Length > 0;

            // Find the smallest-index mip available in the .upk's mip array
            // (which mirrors the TFC mip indices we'd need to ask TfcExtract for).
            // For TFC textures the inline mips are zero-length placeholders but
            // still describe declared SizeX/SizeY per mip index — the largest
            // declared mip in the array tells us where index 0 maps.
            int topMipIndex = 0;
            if (tex2.Mips != null && tex2.Mips.Count > 0)
            {
                // We treat MipIndex 0 as the largest mip — which is what the
                // TFC manifest stores. TfcExtract returns mip0 bytes; caller
                // passes tex.SizeX/Y unchanged for topMipIndex=0.
                topMipIndex = 0;
            }

            // package.objectName — needed because TFC manifest stores entries
            // by FullName ("Lighting.Knowhere_Raid_X0Y0_Lighting_Diff").
            string textureFullName = best.Value.export?.GetPathName() ?? best.Value.name;
            string tfcName = tex2.TextureFileCacheName?.Name ?? "";
            bool isExternal = !hasInlineBytes && !string.IsNullOrEmpty(tfcName);

            await WriteJsonAsync(new
            {
                found = true,
                textureFullName,
                textureLeafName = best.Value.name,
                tfcFileName = tfcName,
                format = tex2.Format.ToString().Replace("PF_", ""),
                sizeX = tex2.SizeX,
                sizeY = tex2.SizeY,
                topMipIndex,
                isExternal,
                hasInlineBytes,
                candidateTextureNames = candidates.Names,
                candidateTexturePrefixes = candidates.Prefixes,
                note = isExternal
                    ? "pixel data lives in TFC; caller should run TfcExtract"
                    : (hasInlineBytes ? "inline mip available in .upk" : "no inline bytes and no TFC cache name — unknown source")
            });
            Console.WriteLine($"groundtex-meta: {textureFullName} {tex2.SizeX}x{tex2.SizeY} {tex2.Format} isExternal={isExternal} -> {outJsonPath}");
            return 0;
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(new
            {
                found = false,
                candidateTextureNames = new System.Collections.Generic.List<string>(),
                candidateTexturePrefixes = new System.Collections.Generic.List<string>(),
                note = $"err {ex.GetType().Name}: {ex.Message}"
            });
            Console.Error.WriteLine($"groundtex-meta err: {ex.Message}");
            return 0; // still produce JSON for the caller to read
        }
    }

    // Result of the material → texture sweep. Exact names go in Names; package
    // prefixes (for tiles whose materials are external imports) go in Prefixes.
    private sealed class GroundTexCandidates
    {
        public System.Collections.Generic.List<string> Names { get; } = new();
        public System.Collections.Generic.List<string> Prefixes { get; } = new();
    }

    // Walks every Material / MaterialInstanceConstant export in the .upk and
    // collects the full PathNames of every Texture2D referenced via:
    //   * MIC.TextureParameterValues + parent chain (UE3 MIC inheritance)
    //   * UMaterial.Expressions[] UMaterialExpressionTextureSample nodes
    //   * UMaterial.DiffuseColor input graph (TryGetTextureFromInput)
    //   * UMaterial.MaterialResource[].UniformExpressionTextures (cooked fallback)
    // Also harvests Texture2D / Material imports from the import table — many
    // cell tiles have NO Material exports at all and only reference materials
    // by import. The list is de-duped and ranked by ScoreTexture (diffuse hints
    // first). Errors per material are swallowed — partial results beat none.
    private static GroundTexCandidates CollectMaterialTextureCandidates(
        UpkManager.Models.UpkFile.UnrealHeader header)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scored = new System.Collections.Generic.List<(int score, string path)>();
        var seenPrefixes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixesOut = new System.Collections.Generic.List<string>();

        void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!seen.Add(path)) return;
            string leaf = Leaf(path);
            // Cross-package material refs can include "Engine.DefaultTexture" etc.
            // Reject obvious engine fallbacks since they're never in TFC and never
            // a ground tile.
            string lp = path.ToLowerInvariant();
            if (lp.StartsWith("engine.") || lp.Contains(".defaulttexture") || lp.Contains(".whitetexture") || lp.Contains(".blacktexture"))
                return;
            int sc = ScoreTexture(leaf, 1024, 1024); // dim unknown → assume typical
            scored.Add((sc, path));
        }

        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(cls, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
            bool isMat = string.Equals(cls, "Material", StringComparison.OrdinalIgnoreCase);
            if (!isMic && !isMat) continue;

            try
            {
                if (e.UnrealObject == null) header.ReadExportObjectAsync(e, null).GetAwaiter().GetResult();
                if (e.UnrealObject == null) e.ParseUnrealObject(false, false).GetAwaiter().GetResult();
                if (e.UnrealObject is not IUnrealObject uo) continue;

                if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    object cur = mic;
                    for (int depth = 0; depth < 6 && cur is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant cm; depth++)
                    {
                        if (cm.TextureParameterValues != null)
                        {
                            foreach (var tp in cm.TextureParameterValues)
                            {
                                string p = tp?.ParameterValue?.GetPathName();
                                if (!string.IsNullOrEmpty(p)) Add(p);
                            }
                        }
                        try { cur = cm.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>(); }
                        catch { cur = null; }
                    }
                }
                else if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    string dpath = TryGetTextureFromInput(um.DiffuseColor);
                    if (!string.IsNullOrEmpty(dpath)) Add(dpath);
                    if (um.Expressions != null)
                    {
                        foreach (var er in um.Expressions)
                        {
                            UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample s = null;
                            try { s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>(); } catch { }
                            string p = s?.Texture?.GetPathName();
                            if (!string.IsNullOrEmpty(p)) Add(p);
                        }
                    }
                    if (um.MaterialResource != null)
                    {
                        foreach (var quality in um.MaterialResource)
                        {
                            if (quality?.UniformExpressionTextures == null) continue;
                            foreach (var t in quality.UniformExpressionTextures)
                            {
                                string p = t?.GetPathName();
                                if (!string.IsNullOrEmpty(p)) Add(p);
                            }
                        }
                    }
                }
            }
            catch { /* skip unparseable material */ }
        }

        // ── Import-table sweep ──────────────────────────────────────────────
        // Most cell tiles (NordicRuin_A_NESWcS_A, Norway_A_ES_A, etc.) contain
        // ZERO Material exports — their StaticMeshComponents reference materials
        // via IMPORTS pointing at sibling .upk files (Mats_*.upk, *_terrain.upk).
        // Cross-upk loading at this stage is too expensive (and not always
        // wired up); instead, surface the IMPORT names directly:
        //   * Texture2D imports → exact-match candidates (the manifest stores
        //     these by the same package.object path).
        //   * Material / MIC imports → derive a "<PackageName>" prefix so the
        //     handler can probe for any diffuse-shaped texture sitting in the
        //     same package via --has-prefix (e.g. import "norway_terrain.norway_terrain_snow_vertexpaint_mat"
        //     → prefix "Norway_Terrain." → finds "Norway_Terrain.Norway_Terrain_snowSteps_DIFF_A").
        foreach (var imp in header.ImportTable)
        {
            string cls = imp.ClassNameIndex?.Name ?? "";
            string lcls = cls.ToLowerInvariant();
            string path = imp.GetPathName() ?? "";
            if (string.IsNullOrEmpty(path)) continue;

            if (lcls == "texture2d")
            {
                Add(path);
                continue;
            }
            if (lcls == "material" || lcls == "materialinstanceconstant" || lcls == "materialinstancetimevarying")
            {
                // Derive package-prefix candidate. PathName looks like
                // "outerPkg.subPkg.materialLeaf". The TFC manifest is keyed by
                // "<PackageName>.<TextureName>" where PackageName is the
                // outermost package of the texture (usually shared with the
                // material). Emit the top-level package + "." as a prefix
                // candidate; handler distinguishes by checking for a "."
                // anywhere AND the entry having mip data.
                int dot = path.IndexOf('.');
                if (dot > 0)
                {
                    string pkg = path[..dot];
                    string prefix = pkg + "."; // explicit dot separator
                    if (seenPrefixes.Add(prefix)) prefixesOut.Add(prefix);
                }
            }
        }

        // Highest score first (diffuse hints > ground keywords > rest > rejected).
        // Drop negative-scored entries entirely — they're explicit non-diffuse
        // (normal/spec/mask/etc.) and will never be a usable ground texture.
        scored.Sort((a, b) => b.score.CompareTo(a.score));
        var result = new GroundTexCandidates();
        foreach (var (sc, p) in scored)
        {
            if (sc < 0) continue;
            result.Names.Add(p);
        }
        result.Prefixes.AddRange(prefixesOut);
        return result;
    }

    // Score a candidate texture by name + size. Higher = better diffuse pick.
    // Negative/zero score = reject.
    //   * Strong NO: name ends with non-diffuse suffix (_n, _nrm, _spec, etc.)
    //   * Big bonus: name contains explicit diffuse hint (diff/diffuse/color/albedo)
    //   * Bonus: name contains ground/terrain/floor/dirt/sand/grass keywords
    //   * Tiebreaker: pixel area (favors larger textures)
    private static int ScoreTexture(string name, int w, int h)
    {
        string n = name.ToLowerInvariant();
        string[] nonDiffuseSuffix = {
            "_n", "_nrm", "_normal", "_norm",
            "_s", "_spec", "_specular",
            "_m", "_mask", "_alpha",
            "_emi", "_emit", "_emissive",
            "_height", "_disp", "_displacement",
            "_lit", "_ao", "_occlusion",
        };
        foreach (var suf in nonDiffuseSuffix)
            if (n.EndsWith(suf)) return -1;

        // Reject when the non-diffuse keyword appears as an internal segment
        // (e.g. "rock_d_norm_a" — _d_ would otherwise win the diffuse hint).
        string[] nonDiffuseInside = {
            "_norm_", "_normal_", "_nrm_",
            "_spec_", "_specular_",
            "_mask_", "_alpha_",
            "_emit_", "_emissive_",
            "_ao_", "_occlusion_",
            "_height_", "_disp_",
        };
        foreach (var sub in nonDiffuseInside)
            if (n.Contains(sub)) return -1;

        int score = 0;
        string[] diffuseHints = { "diffuse", "_dif", "_diff", "_d_", "_color", "_col", "_albedo" };
        foreach (var h2 in diffuseHints)
            if (n.Contains(h2)) { score += 100; break; }

        string[] groundHints = { "ground", "terrain", "floor", "dirt", "sand", "stone", "concrete", "asphalt", "rock_d", "grass_d" };
        foreach (var h2 in groundHints)
            if (n.Contains(h2)) { score += 50; break; }

        // Demote tiny thumbnail-ish textures so they only win if no real
        // diffuse exists (16x16 etc.). Big textures stay neutral here —
        // we mostly trust the name.
        if (w * h <= 256) score -= 20;

        // Tiebreaker: tiny boost per kilo-pixel so larger wins among equals.
        score += (w * h) / 1024;

        // Base accept score so a no-hint, non-rejected texture still qualifies
        // as a last-resort pick.
        return score + 1;
    }

    // TR48-U: 6-slot Marvel Heroes material harvest (Diffuse/Normal/
    // Smspsk/Espa/Smrr/SpecColor). Per OAS MeshPreviewGameMaterialResolver,
    // slot routing is by texture parameter name AND/OR texture path name
    // (whichever matches the channel-pack pattern). Walks every MIC
    // parameter, classifies each, and writes into out-strings. Empty
    // string = slot absent (shader's GetMaterialMasks else-branch).
    private static void Classify6Slots(
        UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic,
        ref string diffuse, ref string normal, ref string specular, ref string emissive,
        ref string smspsk, ref string espa, ref string smrr, ref string specColor)
    {
        if (mic?.TextureParameterValues == null) return;
        foreach (var p in mic.TextureParameterValues)
        {
            if (p == null) continue;
            string pname = (p.ParameterName?.Name ?? "").ToLowerInvariant();
            string tpath = "";
            try { tpath = (p.ParameterValue?.GetPathName() ?? "").ToLowerInvariant(); } catch { }
            if (string.IsNullOrEmpty(tpath)) continue;
            string origPath = p.ParameterValue.GetPathName();
            // 6-slot first (more specific name patterns win over generic).
            if (string.IsNullOrEmpty(smspsk) && (pname.Contains("specmult_specpow_skinmask") || tpath.Contains("specmult_specpow_skinmask") || tpath.Contains("smspsk")))
                { smspsk = origPath; continue; }
            if (string.IsNullOrEmpty(espa) && (pname.Contains("espa") || tpath.Contains("emissivespecpow") || tpath.Contains("espa")))
                { espa = origPath; continue; }
            if (string.IsNullOrEmpty(smrr) && (pname.Contains("smrr") || tpath.Contains("specmultrimmaskrefl") || tpath.Contains("smrr")))
                { smrr = origPath; continue; }
            if (string.IsNullOrEmpty(specColor) && (pname.Contains("speccolor") || tpath.Contains("speccolor")))
                { specColor = origPath; continue; }
            // Classic 4-slot.
            if (string.IsNullOrEmpty(diffuse) && (pname.Contains("diffuse") || pname.Contains("basecolor") || pname.Contains("albedo")))
                { diffuse = origPath; continue; }
            if (string.IsNullOrEmpty(normal) && (pname.Contains("normal") || pname.Contains("bump") || tpath.EndsWith("_n") || tpath.Contains("_normal")))
                { normal = origPath; continue; }
            if (string.IsNullOrEmpty(specular) && (pname.Contains("specular") || pname.Contains("specmap")))
                { specular = origPath; continue; }
            if (string.IsNullOrEmpty(emissive) && (pname.Contains("emissive") || pname.Contains("emit") || pname.Contains("selfillum")))
                { emissive = origPath; continue; }
        }
    }

    // Walks the first FStaticMeshElement (largest section) → its Material FObject
    // → UMaterialInstanceConstant.GetTextureParameterValue("Diffuse"|"BaseColor"|...)
    // → returns the texture's PathName, or "" if anything in the chain fails.
    // Walks the MIC's Parent chain when the MIC itself doesn't override the
    // texture parameter (UE3 MICs inherit unset params from their parent).
    // Resolve all four standard PBR-ish texture slots from the LOD's first
    // section material. Each result is "" if not found. Writes results to
    // out vars so callers can fill MeshOut.{Diffuse,Normal,Specular,Emissive}.
    //
    // TR48-U extension: now also outputs the 6-slot Marvel Heroes channels
    // (Smspsk/Espa/Smrr/SpecColor) by walking ALL texture parameters and
    // classifying by name pattern (see Classify6Slots above).
    private static void TryResolveAllMaps(
        UpkManager.Models.UpkFile.Engine.Mesh.FStaticMeshRenderData lod,
        out string diffuse, out string normal, out string specular, out string emissive,
        out string smspsk, out string espa, out string smrr, out string specColor)
    {
        diffuse = normal = specular = emissive = "";
        smspsk = espa = smrr = specColor = "";
        if (lod?.Elements == null || lod.Elements.Count == 0) return;
        var el = lod.Elements[0];
        if (el?.Material == null) return;
        try
        {
            object obj = el.Material.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
            for (int depth = 0; depth < 6 && obj != null; depth++)
            {
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    diffuse  = NonEmpty(diffuse,  FirstMicTex(mic, "Diffuse", "BaseColor", "Albedo", "Color"));
                    normal   = NonEmpty(normal,   FirstMicTex(mic, "Normal", "NormalMap", "Bump"));
                    // Drop generic "Mask"/"Texture" — they're too easily mismatched.
                    specular = NonEmpty(specular, FirstMicTex(mic, "Specular", "SpecMap", "SpecRoughMetal"));
                    emissive = NonEmpty(emissive, FirstMicTex(mic, "Emissive", "Emit", "SelfIllum"));
                    // TR48-U: 6-slot Marvel Heroes channel-packed harvest.
                    Classify6Slots(mic, ref diffuse, ref normal, ref specular, ref emissive,
                                        ref smspsk, ref espa, ref smrr, ref specColor);
                    obj = mic.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    continue;
                }
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    diffuse  = NonEmpty(diffuse,  TryGetTextureFromInput(um.DiffuseColor));
                    if (um.Expressions != null)
                    {
                        foreach (var er in um.Expressions)
                        {
                            var s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                            if (s?.Texture == null) continue;
                            string n = s.Texture.GetPathName() ?? "";
                            string ln = n.ToLowerInvariant();
                            if (diffuse  == "" && (ln.Contains("diff") || ln.EndsWith("_d") || ln.EndsWith("_color") || ln.EndsWith("_albedo"))) diffuse  = n;
                            if (normal   == "" && (ln.EndsWith("_n") || ln.EndsWith("_norm") || ln.Contains("normal") || ln.Contains("bump"))) normal   = n;
                            if (specular == "" && (ln.EndsWith("_s") || ln.EndsWith("_spec") || ln.Contains("specular"))) specular = n;
                            if (emissive == "" && (ln.EndsWith("_e") || ln.Contains("emissive") || ln.Contains("emit") || ln.Contains("selfillum"))) emissive = n;
                        }
                        if (diffuse == "")
                        {
                            // No name hint matched — fall back to the very first sampler as diffuse.
                            foreach (var er in um.Expressions)
                            {
                                var s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                                if (s?.Texture == null) continue;
                                string n = s.Texture.GetPathName();
                                if (!string.IsNullOrEmpty(n)) { diffuse = n; break; }
                            }
                        }
                    }
                }
                break;
            }
        }
        catch { }
    }

    private static string FirstMicTex(
        UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic,
        params string[] paramNames)
    {
        foreach (var p in paramNames)
        {
            var t = mic.GetTextureParameterValue(p);
            string path = t?.GetPathName();
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return "";
    }

    private static string NonEmpty(string current, string candidate)
        => string.IsNullOrEmpty(current) ? (candidate ?? "") : current;

    private static string TryResolveFirstDiffuse(UpkManager.Models.UpkFile.Engine.Mesh.FStaticMeshRenderData lod)
    {
        if (lod?.Elements == null || lod.Elements.Count == 0) return "";
        var el = lod.Elements[0];
        if (el?.Material == null) return "";
        try
        {
            object obj = el.Material.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
            for (int depth = 0; depth < 6 && obj != null; depth++)
            {
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    var tex = mic.GetTextureParameterValue("Diffuse")
                           ?? mic.GetTextureParameterValue("BaseColor")
                           ?? mic.GetTextureParameterValue("Albedo")
                           ?? mic.GetTextureParameterValue("Color")
                           ?? mic.GetTextureParameterValue("Texture");
                    if (tex != null)
                    {
                        string p = tex.GetPathName();
                        if (!string.IsNullOrEmpty(p)) return p;
                    }
                    // Walk up the parent chain. MIC.Parent is FObject of MaterialInterface.
                    obj = mic.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    continue;
                }
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    // Plain UMaterial path. DiffuseColor.Expression points at
                    // a UMaterialExpressionTextureSample whose .Texture is
                    // the actual UTexture2D. UE3 stores this as the resolved
                    // diffuse for the material.
                    string p = TryGetTextureFromInput(um.DiffuseColor);
                    if (!string.IsNullOrEmpty(p)) return p;

                    // Fallback: scan Expressions[] for any TextureSample and
                    // return its texture. Catches materials that route the
                    // diffuse through a Multiply/Lerp node — the diffuse-ish
                    // texture is usually the first/only sampler in Expressions[].
                    if (um.Expressions != null)
                    {
                        foreach (var exprRef in um.Expressions)
                        {
                            var sample = exprRef?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                            if (sample?.Texture == null) continue;
                            string tp = sample.Texture.GetPathName();
                            if (!string.IsNullOrEmpty(tp)) return tp;
                        }
                    }
                }
                break;
            }
        }
        catch { /* leave empty — viewer falls back to flat material */ }
        return "";
    }

    // ─── Phase 4H helpers: skeletal mesh → static-mesh MeshPack ─────────
    // We extract the GPU-skin vertex buffer's positions/normals/UVs as-is,
    // which is the COMPONENT-SPACE bind pose (no animation applied). For
    // viewer purposes this looks like a T-posed character at the entity's
    // world position. Bone weights are intentionally discarded — the viewer
    // doesn't have a skinning shader.
    private static MeshPack.MeshOut MeshPackFromSkeletalLod(string name, UpkManager.Models.UpkFile.Engine.Mesh.FStaticLODModel lod)
    {
        if (lod?.VertexBufferGPUSkin == null) return null;
        var vb = lod.VertexBufferGPUSkin;
        int vc = (int)lod.NumVertices;
        if (vc <= 0) return null;

        var pos = new float[vc * 3];
        var nrm = new float[vc * 3];
        var uvs = new float[vc * 2];

        int i = 0;
        foreach (var vert in vb.VertexData)
        {
            if (i >= vc) break;
            var p = vb.GetVertexPosition(vert);
            pos[i*3 + 0] = p.X; pos[i*3 + 1] = p.Y; pos[i*3 + 2] = p.Z;
            // TangentZ is the packed normal; un-pack to vector then store.
            var n = vert.TangentZ.ToVector();
            nrm[i*3 + 0] = n.X; nrm[i*3 + 1] = n.Y; nrm[i*3 + 2] = n.Z;
            var uv = vert.GetVector2(0);
            uvs[i*2 + 0] = uv.X; uvs[i*2 + 1] = uv.Y;
            i++;
        }

        var idxSrc = lod.MultiSizeIndexContainer?.IndexBuffer;
        var idx = idxSrc != null ? idxSrc.ToArray() : Array.Empty<uint>();

        return new MeshPack.MeshOut
        {
            Name = name,
            Positions = pos,
            Normals = nrm,
            UVs = uvs,
            Indices = idx,
        };
    }

    // Skeletal all-maps walker — same MIC/UMaterial chain logic as static.
    private static void TryResolveSkeletalAllMaps(
        UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sk,
        out string diffuse, out string normal, out string specular, out string emissive)
    {
        diffuse = normal = specular = emissive = "";
        if (sk?.Materials == null || sk.Materials.Count == 0) return;
        try
        {
            object obj = sk.Materials[0]?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
            for (int depth = 0; depth < 6 && obj != null; depth++)
            {
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    diffuse  = NonEmpty(diffuse,  FirstMicTex(mic, "Diffuse", "BaseColor", "Albedo", "Color", "Texture"));
                    normal   = NonEmpty(normal,   FirstMicTex(mic, "Normal", "NormalMap", "Bump"));
                    specular = NonEmpty(specular, FirstMicTex(mic, "Specular", "SpecMap", "Roughness", "Metallic", "Mask"));
                    emissive = NonEmpty(emissive, FirstMicTex(mic, "Emissive", "Emit", "SelfIllum", "Glow"));
                    obj = mic.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    continue;
                }
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    diffuse = NonEmpty(diffuse, TryGetTextureFromInput(um.DiffuseColor));
                    if (um.Expressions != null)
                    {
                        foreach (var er in um.Expressions)
                        {
                            var s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                            if (s?.Texture == null) continue;
                            string n = s.Texture.GetPathName() ?? "";
                            string ln = n.ToLowerInvariant();
                            if (diffuse  == "" && (ln.Contains("diff") || ln.Contains("color") || ln.Contains("albedo"))) diffuse  = n;
                            if (normal   == "" && (ln.Contains("norm") || ln.Contains("bump"))) normal   = n;
                            if (specular == "" && (ln.Contains("spec") || ln.Contains("rough") || ln.Contains("metal"))) specular = n;
                            if (emissive == "" && (ln.Contains("emis") || ln.Contains("glow") || ln.Contains("illum"))) emissive = n;
                        }
                    }
                }
                break;
            }
        }
        catch { }
    }

    // Legacy single-diffuse — kept for callers that haven't migrated.
    private static string TryResolveSkeletalFirstDiffuse(UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sk)
    {
        if (sk?.Materials == null || sk.Materials.Count == 0) return "";
        try
        {
            object obj = sk.Materials[0]?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
            for (int depth = 0; depth < 6 && obj != null; depth++)
            {
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    var tex = mic.GetTextureParameterValue("Diffuse")
                           ?? mic.GetTextureParameterValue("BaseColor")
                           ?? mic.GetTextureParameterValue("Albedo")
                           ?? mic.GetTextureParameterValue("Color")
                           ?? mic.GetTextureParameterValue("Texture");
                    if (tex != null)
                    {
                        string p = tex.GetPathName();
                        if (!string.IsNullOrEmpty(p)) return p;
                    }
                    obj = mic.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    continue;
                }
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    string p = TryGetTextureFromInput(um.DiffuseColor);
                    if (!string.IsNullOrEmpty(p)) return p;
                    if (um.Expressions != null)
                    {
                        foreach (var er in um.Expressions)
                        {
                            var s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                            if (s?.Texture == null) continue;
                            string tp = s.Texture.GetPathName();
                            if (!string.IsNullOrEmpty(tp)) return tp;
                        }
                    }
                }
                break;
            }
        }
        catch { }
        return "";
    }

    // Helper for plain-UMaterial path. FColorMaterialInput.Expression points
    // at a MaterialExpression node; if it's a TextureSample, return the
    // bound texture's PathName.
    private static string TryGetTextureFromInput(UpkManager.Models.UpkFile.Engine.Material.FColorMaterialInput input)
    {
        try
        {
            if (input?.Expression == null) return "";
            // Walk graph via reflection up to 6 hops. Each expression node may
            // chain through Multiply/Add/Lerp/Constant-wrapped nodes before
            // reaching a TextureSample. Walk every FObject / FExpressionInput
            // field to find the sampler — no class-list, no name guess.
            var seen = new System.Collections.Generic.HashSet<object>();
            return WalkForTextureSample(input.Expression, seen, 0) ?? "";
        }
        catch { return ""; }
    }

    private static string WalkForTextureSample(object exprRef, System.Collections.Generic.HashSet<object> seen, int depth)
    {
        if (exprRef == null || depth > 6) return null;
        object node;
        try { node = (exprRef as UpkManager.Models.UpkFile.Tables.FObject)?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpression>(); }
        catch { return null; }
        if (node == null || !seen.Add(node)) return null;
        // Direct hit: TextureSample.
        if (node is UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample ts && ts.Texture != null)
        {
            string p = ts.Texture.GetPathName();
            if (!string.IsNullOrEmpty(p)) return p;
        }
        // Otherwise reflect over every property on the node. Follow:
        //   - FObject  (another expression ref OR a Texture ref on this node)
        //   - FExpressionInput.Expression (next graph hop)
        foreach (var prop in node.GetType().GetProperties())
        {
            object v;
            try { v = prop.GetValue(node); } catch { continue; }
            if (v == null) continue;
            // FExpressionInput-shaped (has Expression FObject child).
            var expProp = v.GetType().GetProperty("Expression");
            if (expProp != null)
            {
                var inner = expProp.GetValue(v);
                if (inner is UpkManager.Models.UpkFile.Tables.FObject)
                {
                    string hit = WalkForTextureSample(inner, seen, depth + 1);
                    if (!string.IsNullOrEmpty(hit)) return hit;
                }
            }
            // Direct FObject reference (some nodes hold expression refs raw).
            if (v is UpkManager.Models.UpkFile.Tables.FObject fo)
            {
                // Could be a Texture (terminal) or another expression.
                try
                {
                    var asTex = fo.LoadObject<UpkManager.Models.UpkFile.Engine.Texture.UTexture2D>();
                    if (asTex != null) { string p = fo.GetPathName(); if (!string.IsNullOrEmpty(p)) return p; }
                }
                catch { }
                string hitFo = WalkForTextureSample(fo, seen, depth + 1);
                if (!string.IsNullOrEmpty(hitFo)) return hitFo;
            }
        }
        return null;
    }

    private static FTexture2DMipMap? LargestInlineMip(UTexture2D tex)
    {
        if (tex.Mips == null || tex.Mips.Count == 0) return null;
        return tex.Mips
            .Where(m => m?.Data != null && m.Data.Length > 0)
            .OrderByDescending(m => Math.Max(m.SizeX, m.SizeY))
            .FirstOrDefault();
    }

    private static TfcManifest _manifestCache;
    private static string _manifestCacheDir = "";
    private static TfcManifest LoadManifestOnce(string cookedDir)
    {
        // Memoize across calls — there's exactly one manifest per cooked dir.
        if (_manifestCache != null && string.Equals(_manifestCacheDir, cookedDir, StringComparison.OrdinalIgnoreCase))
            return _manifestCache;
        string path = Path.Combine(cookedDir, "TextureFileCacheManifest.bin");
        var m = new TfcManifest();
        if (!m.Load(path, out string err))
        {
            Console.Error.WriteLine($"  TFC manifest not loaded ({err}) — HD path disabled");
            _manifestCache = null;
            _manifestCacheDir = cookedDir;
            return null;
        }
        Console.WriteLine($"  loaded TFC manifest: {m.EntryCount} entries");
        _manifestCache = m;
        _manifestCacheDir = cookedDir;
        return m;
    }

    // For one texture: ask the manifest where its HD mips live in the .tfc,
    // open that .tfc, seek to the offset, hand the compressed chunk bytes to
    // UTexture2D.ReadMipMapCache (which parses the standard UE3 chunk header
    // and decompresses). If the manifest miss or any step fails, fall back
    // to the largest inline mip in the .upk.
    private static async Task<(byte[] bytes, int w, int h, string source)> BestMipBytesAsync(UTexture2D tex, string cookedDir, TfcManifest manifest)
    {
        // ── Path A: TFC via TextureFileCacheManifest.bin (HD) ────────────
        if (manifest != null)
        {
            Guid guid = tex.TextureFileCacheGuid?.ToSystemGuid() ?? Guid.Empty;
            string tfcName = (tex.TextureFileCacheName?.Name ?? "").ToLowerInvariant();
            var entry = manifest.TryGet(guid, tfcName);
            if (entry != null && entry.Mips.Count > 0)
            {
                // MipIndex 0 = highest detail; pick the smallest index available.
                var hd = entry.Mips.OrderBy(m => m.MipIndex).First();
                string tfcPath = Path.Combine(cookedDir, entry.TfcFileName + ".tfc");
                if (!File.Exists(tfcPath))
                {
                    tfcPath = Directory.EnumerateFiles(cookedDir, "*.tfc")
                        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                            .Equals(entry.TfcFileName, StringComparison.OrdinalIgnoreCase));
                }
                if (tfcPath != null && File.Exists(tfcPath))
                {
                    byte[] chunkBytes = new byte[hd.Size];
                    await using (var fs = new FileStream(tfcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                    {
                        fs.Seek(hd.Offset, SeekOrigin.Begin);
                        int n = 0;
                        while (n < chunkBytes.Length)
                        {
                            int r = await fs.ReadAsync(chunkBytes.AsMemory(n, chunkBytes.Length - n));
                            if (r <= 0) break;
                            n += r;
                        }
                        if (n == chunkBytes.Length)
                        {
                            try
                            {
                                // OverrideMipMap carries the BASE texture's full size + format
                                // so ReadMipMapCache can derive width/height for any LOD.
                                var overrideMipMap = new FTexture2DMipMap
                                {
                                    SizeX = tex.SizeX,
                                    SizeY = tex.SizeY,
                                    OverrideFormat = UTexture2D.ParseFileFormat(tex.Format),
                                };
                                var upkReader = ByteArrayReader.CreateNew(chunkBytes, 0);
                                int countBefore = tex.Mips.Count;
                                await tex.ReadMipMapCache(upkReader, hd.MipIndex, overrideMipMap);
                                if (tex.Mips.Count > countBefore)
                                {
                                    var newMip = tex.Mips[tex.Mips.Count - 1];
                                    if (newMip.Data != null && newMip.Data.Length > 0)
                                        return (newMip.Data, newMip.SizeX, newMip.SizeY,
                                                $"tfc:{entry.TfcFileName}/mip{hd.MipIndex}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"  tfc parse failed for mip{hd.MipIndex} @ {entry.TfcFileName}+{hd.Offset}: {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        // ── Path B: largest inline mip (fallback / no manifest) ──────────
        var inline = LargestInlineMip(tex);
        if (inline != null)
            return (inline.Data, inline.SizeX, inline.SizeY, "inline");
        return (null, 0, 0, "");
    }

    private static byte[]? DecodeMip(byte[] data, int width, int height, EPixelFormat format)
    {
        var decoder = new BcDecoder();
        ColorRgba32[]? pixels;
        try
        {
            pixels = format switch
            {
                EPixelFormat.PF_DXT1 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc1),
                EPixelFormat.PF_DXT3 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc2),
                EPixelFormat.PF_DXT5 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc3),
                EPixelFormat.PF_A8R8G8B8 => null,
                EPixelFormat.PF_G8 => null,
                _ => null,
            };
        }
        catch { return null; }

        if (format == EPixelFormat.PF_A8R8G8B8)
        {
            var rgba = new byte[width * height * 4];
            int n = Math.Min(rgba.Length, data.Length);
            for (int i = 0; i + 3 < n; i += 4)
            {
                byte a = data[i + 0], r = data[i + 1], g = data[i + 2], b = data[i + 3];
                rgba[i + 0] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
            }
            return rgba;
        }
        if (format == EPixelFormat.PF_G8)
        {
            var rgba = new byte[width * height * 4];
            int n = Math.Min(width * height, data.Length);
            for (int i = 0; i < n; i++)
            {
                byte g = data[i];
                rgba[i * 4 + 0] = g; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = g; rgba[i * 4 + 3] = 0xFF;
            }
            return rgba;
        }
        if (pixels == null) return null;

        var outRgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            outRgba[i * 4 + 0] = p.r;
            outRgba[i * 4 + 1] = p.g;
            outRgba[i * 4 + 2] = p.b;
            outRgba[i * 4 + 3] = p.a;
        }
        return outRgba;
    }

    // ── smcprobe — dump the first decompressed bytes of the first few
    //    StaticMeshComponent exports, plus a name-table lookahead so we can
    //    identify the pre-property prefix layout that Marvel Heroes' cooker
    //    produces. This is evidence-gathering for fixing UStaticMeshComponent
    //    parsing.
    private static async Task<int> RunSmcProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }

        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        // Build a quick { name-table index → string } map so we can decode
        // any 4-byte slot that LOOKS like a name index. UE3 FName is two
        // int32s: nameIndex + instanceNumber. The instance number is usually 0.
        var nameTable = new System.Collections.Generic.Dictionary<int, string>();
        for (int i = 0; i < header.NameTable.Count; i++)
            nameTable[i] = header.NameTable[i].Name?.String;

        Console.WriteLine($"name table size: {nameTable.Count}");
        Console.WriteLine($"export table size: {header.ExportTable.Count}");

        int shown = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (!string.Equals(cls, "StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;

            // ReadExportObjectAsync populates e.UnrealObjectReader with the
            // decompressed serial bytes. After this call we can dump them.
            await header.ReadExportObjectAsync(e, null);
            var rdr = e.UnrealObjectReader;
            if (rdr == null) { Console.WriteLine($"-- {e.ObjectNameIndex?.Name}: no reader"); continue; }

            byte[] bytes = rdr.GetBytes();
            int sz = Math.Min(128, bytes.Length);
            Console.WriteLine($"== {e.ObjectNameIndex?.Name}  serialSize={bytes.Length}");
            for (int row = 0; row < sz; row += 16)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"   {row:X4}  ");
                for (int col = 0; col < 16; col++)
                {
                    if (row + col < sz) sb.Append($"{bytes[row + col]:X2} ");
                    else sb.Append("   ");
                }
                sb.Append("  ");
                for (int col = 0; col < 16 && row + col < sz; col++)
                {
                    byte b = bytes[row + col];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine(sb.ToString());
            }

            // Try interpreting every 4-byte aligned slot as a name-table index
            // and print which names it would resolve to. This tells us where
            // the property tags actually begin.
            Console.WriteLine("   --- 4-byte slot name-table lookahead ---");
            for (int off = 0; off + 4 <= Math.Min(64, bytes.Length); off += 4)
            {
                int v = BitConverter.ToInt32(bytes, off);
                if (nameTable.TryGetValue(v, out var name))
                    Console.WriteLine($"   off=0x{off:X2}  val={v,-10}  -> name[{v}] = \"{name}\"");
            }

            shown++;
            if (shown >= 3) break;
        }
        if (shown == 0) Console.WriteLine("no StaticMeshComponent exports in this upk");
        return 0;
    }

    // ── smccompare — find the per-SMC None terminator and dump the bytes
    //    immediately after it (the post-property block) for the first N SMCs,
    //    side by side. Lets us identify what's constant (struct header) vs
    //    what varies per instance (lightmap refs, scale/bias, etc).
    private static async Task<int> RunSmcCompareAsync(string upkPath, string outTxtPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        var nameTable = new System.Collections.Generic.Dictionary<int, string>();
        for (int i = 0; i < header.NameTable.Count; i++)
            nameTable[i] = header.NameTable[i].Name?.String;

        // Find name index for "None".
        int noneIdx = -1;
        foreach (var kv in nameTable)
            if (string.Equals(kv.Value, "None", StringComparison.OrdinalIgnoreCase)) { noneIdx = kv.Key; break; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"upk: {Path.GetFileName(upkPath)}");
        sb.AppendLine($"None FName index: {noneIdx}");
        sb.AppendLine();

        int shown = 0;
        foreach (var e in header.ExportTable)
        {
            if (!string.Equals(e.ClassReferenceNameIndex?.Name, "StaticMeshComponent", StringComparison.OrdinalIgnoreCase))
                continue;
            await header.ReadExportObjectAsync(e, null);
            var rdr = e.UnrealObjectReader;
            if (rdr == null) continue;
            byte[] bytes = rdr.GetBytes();

            // Find None FName: 8 bytes = [int32 noneIdx, int32 0]. Search.
            int noneOff = -1;
            for (int off = 4; off + 8 <= bytes.Length; off += 4)
            {
                int v = BitConverter.ToInt32(bytes, off);
                int inst = BitConverter.ToInt32(bytes, off + 4);
                if (v == noneIdx && inst == 0) { noneOff = off; break; }
            }
            int postStart = noneOff >= 0 ? noneOff + 8 : -1;
            sb.AppendLine($"== {e.ObjectNameIndex?.Name}  size={bytes.Length}  noneOff=0x{noneOff:X4}  postStart=0x{postStart:X4}");

            if (postStart > 0)
            {
                // Dump first 80 bytes of post-property block as hex + int32 decoding.
                int n = Math.Min(80, bytes.Length - postStart);
                for (int row = 0; row < n; row += 16)
                {
                    sb.Append($"   +{row:X2}  ");
                    for (int col = 0; col < 16 && row + col < n; col++)
                        sb.Append($"{bytes[postStart + row + col]:X2} ");
                    sb.AppendLine();
                }
                // Int32 interpretation of first 12 slots.
                for (int slot = 0; slot < 12 && postStart + (slot+1)*4 <= bytes.Length; slot++)
                {
                    int v = BitConverter.ToInt32(bytes, postStart + slot * 4);
                    string note = "";
                    if (v > 0 && v <= header.ExportTable.Count)
                    {
                        var hit = header.ExportTable[v - 1];
                        note = $" -> {hit.ClassReferenceNameIndex?.Name} \"{hit.ObjectNameIndex?.Name}\"";
                    }
                    else if (v < 0 && -v <= header.ImportTable.Count)
                    {
                        var imp = header.ImportTable[-v - 1];
                        note = $" <- {imp.ClassNameIndex?.Name} \"{imp.ObjectNameIndex?.Name}\"";
                    }
                    sb.AppendLine($"   slot[{slot}] @+{slot*4:X2} = {v,-10}{note}");
                }
            }
            sb.AppendLine();
            shown++;
            if (shown >= 5) break;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outTxtPath) ?? ".");
        await File.WriteAllTextAsync(outTxtPath, sb.ToString());
        Console.WriteLine($"ok {shown} SMCs probed -> {outTxtPath}");
        return 0;
    }

    // ── smcfullprobe — dump the FULL decompressed serial buffer of the first
    //    SMC in a level .upk, plus a per-4-byte name-table lookahead. We need
    //    the whole thing (not just the first 128 bytes) so we can see what's
    //    written AFTER the property "None" terminator — that's where LODData
    //    lightmap binary lives. Output goes to a file so we can paste it back.
    private static async Task<int> RunSmcFullProbeAsync(string upkPath, string outTxtPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }

        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        var nameTable = new System.Collections.Generic.Dictionary<int, string>();
        for (int i = 0; i < header.NameTable.Count; i++)
            nameTable[i] = header.NameTable[i].Name?.String;

        // Pick the SMC with the most LODData (the one most likely to have
        // lightmaps). Walk all SMC exports, pick the one with the biggest
        // serial size — bigger means more LODData attached.
        UnrealExportTableEntry chosen = null;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (!string.Equals(cls, "StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            if (chosen == null || e.SerialDataSize > chosen.SerialDataSize) chosen = e;
        }
        if (chosen == null) { Console.Error.WriteLine("no StaticMeshComponent exports"); return 3; }

        await header.ReadExportObjectAsync(chosen, null);
        byte[] bytes = chosen.UnrealObjectReader?.GetBytes() ?? new byte[0];

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"upk: {Path.GetFileName(upkPath)}");
        sb.AppendLine($"export: {chosen.ObjectNameIndex?.Name}  serialSize={bytes.Length}");
        sb.AppendLine();

        // Hex dump every byte.
        for (int row = 0; row < bytes.Length; row += 16)
        {
            sb.Append($"{row:X4}  ");
            for (int col = 0; col < 16; col++)
                if (row + col < bytes.Length) sb.Append($"{bytes[row + col]:X2} ");
                else sb.Append("   ");
            sb.Append("  ");
            for (int col = 0; col < 16 && row + col < bytes.Length; col++)
            {
                byte b = bytes[row + col];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        // 4-byte slot name-table lookahead — only print slots that resolve
        // to a known name, plus their offset.
        sb.AppendLine("--- name-table hits (every 4-byte slot whose int32 value matches a known name index) ---");
        for (int off = 0; off + 4 <= bytes.Length; off += 4)
        {
            int v = BitConverter.ToInt32(bytes, off);
            if (nameTable.TryGetValue(v, out var name) && !string.IsNullOrEmpty(name))
                sb.AppendLine($"  0x{off:X4}  val={v,-5}  -> \"{name}\"");
        }

        // Object-ref lookahead — UE3 object refs are signed int32: positive
        // = export-table index (1-based), negative = import-table index
        // (-1-based). Show every 4-byte slot that lands on a plausible
        // texture/lightmap object — those are what FLightMap structs point at.
        sb.AppendLine();
        sb.AppendLine("--- object-ref hits (texture / lightmap exports + imports) ---");
        for (int off = 0; off + 4 <= bytes.Length; off += 4)
        {
            int v = BitConverter.ToInt32(bytes, off);
            string cls = null, name = null;
            if (v > 0 && v <= header.ExportTable.Count)
            {
                var e = header.ExportTable[v - 1];
                cls = e.ClassReferenceNameIndex?.Name;
                name = e.ObjectNameIndex?.Name;
            }
            else if (v < 0 && -v <= header.ImportTable.Count)
            {
                var imp = header.ImportTable[-v - 1];
                cls = imp.ClassNameIndex?.Name;
                name = imp.ObjectNameIndex?.Name;
            }
            if (cls != null && (cls.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cls.IndexOf("LightMap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cls.IndexOf("ShadowMap", StringComparison.OrdinalIgnoreCase) >= 0))
                sb.AppendLine($"  0x{off:X4}  val={v,-6}  -> {cls} \"{name}\"");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outTxtPath) ?? ".");
        await File.WriteAllTextAsync(outTxtPath, sb.ToString());
        Console.WriteLine($"ok -> {outTxtPath} ({bytes.Length} serial bytes, {bytes.Length / 16} rows)");
        return 0;
    }

    // v6 skinning: bones (bind pose, parent) + per-vert (4 bone idx, 4 weights).
    // GPU InfluenceBones are CHUNK-LOCAL indices; remap via chunk.BoneMap.
    private static void FillSkinningData(
        UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sk,
        UpkManager.Models.UpkFile.Engine.Mesh.FStaticLODModel lod,
        MeshPack.MeshOut m)
    {
        try
        {
            if (sk?.RefSkeleton == null || sk.RefSkeleton.Count == 0) return;
            // Bones.
            var bones = new System.Collections.Generic.List<MeshPack.BoneOut>(sk.RefSkeleton.Count);
            foreach (var b in sk.RefSkeleton)
            {
                var bp = b.BonePos;
                bones.Add(new MeshPack.BoneOut
                {
                    Name = b.Name?.Name ?? "",
                    ParentIndex = b.ParentIndex,
                    Px = bp.Position.X, Py = bp.Position.Y, Pz = bp.Position.Z,
                    Qx = bp.Orientation.X, Qy = bp.Orientation.Y, Qz = bp.Orientation.Z, Qw = bp.Orientation.W,
                });
            }
            m.Bones = bones.ToArray();

            // Per-vert: 4 bone indices + 4 weights. Read from GPU skin vertex
            // buffer (InfluenceBones byte[4], InfluenceWeights byte[4]).
            // Remap chunk-local to RefSkeleton-absolute via chunk.BoneMap[].
            if (lod?.VertexBufferGPUSkin?.VertexData == null) return;
            int vc = (int)lod.NumVertices;
            if (vc <= 0) return;

            // Build vert→chunk lookup via running cumulative count over chunks.
            int chunkCount = lod.Chunks?.Count ?? 0;
            int[] chunkOfVert = new int[vc];
            if (chunkCount > 0)
            {
                int cursor = 0;
                for (int ci = 0; ci < chunkCount; ci++)
                {
                    var ch = lod.Chunks[ci];
                    int total = ch.NumRigidVertices + ch.NumSoftVertices;
                    int end = Math.Min(cursor + total, vc);
                    for (int vi = cursor; vi < end; vi++) chunkOfVert[vi] = ci;
                    cursor = end;
                }
            }

            ushort[] skinIdx = new ushort[vc * 4];
            byte[]   skinW   = new byte[vc * 4];
            int i = 0;
            foreach (var v in lod.VertexBufferGPUSkin.VertexData)
            {
                if (i >= vc) break;
                int ci = chunkOfVert[i];
                var chunk = (chunkCount > 0) ? lod.Chunks[ci] : null;
                var boneMap = chunk?.BoneMap;
                for (int k = 0; k < 4; k++)
                {
                    byte localIdx = (v.InfluenceBones != null && k < v.InfluenceBones.Length) ? v.InfluenceBones[k] : (byte)0;
                    byte w = (v.InfluenceWeights != null && k < v.InfluenceWeights.Length) ? v.InfluenceWeights[k] : (byte)0;
                    ushort absIdx = 0;
                    if (boneMap != null && localIdx < boneMap.Count) absIdx = boneMap[localIdx];
                    else absIdx = localIdx;   // fall back if no BoneMap (rare)
                    skinIdx[i*4 + k] = absIdx;
                    skinW[i*4 + k]   = w;
                }
                i++;
            }
            m.SkinIndices = skinIdx;
            m.SkinWeights = skinW;
        }
        catch { /* skip skin data on failure; mesh still ships rigid */ }
    }

    // Per-section table for skeletal mesh LOD0. Each FSkelMeshSection has
    // its own MaterialIndex into USkeletalMesh.Materials[]. NumTriangles
    // is triangle count; index range = BaseIndex..BaseIndex+NumTriangles*3.
    private static MeshPack.SectionOut[] ResolveSkeletalSections(
        UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sk,
        UpkManager.Models.UpkFile.Engine.Mesh.FStaticLODModel lod)
    {
        if (lod?.Sections == null || lod.Sections.Count == 0) return Array.Empty<MeshPack.SectionOut>();
        if (sk?.Materials == null) return Array.Empty<MeshPack.SectionOut>();
        var result = new System.Collections.Generic.List<MeshPack.SectionOut>(lod.Sections.Count);
        foreach (var sec in lod.Sections)
        {
            string d = "", n = "", sp = "", em = "";
            string ss = "", es = "", sr = "", sc = "";
            if (sec.MaterialIndex < sk.Materials.Count)
            {
                try
                {
                    object obj = sk.Materials[sec.MaterialIndex]?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    ResolveMapsFromMaterial(obj, out d, out n, out sp, out em, out ss, out es, out sr, out sc);
                }
                catch { }
            }
            result.Add(new MeshPack.SectionOut
            {
                FirstIndex = sec.BaseIndex,
                NumTriangles = sec.NumTriangles,
                Diffuse = d, Normal = n, Specular = sp, Emissive = em,
                Smspsk = ss, Espa = es, Smrr = sr, SpecColor = sc,
            });
        }
        return result.ToArray();
    }

    // Per-Element section table for static mesh LOD0. Each Element has its
    // own (FirstIndex, NumTriangles) and material — buildings/props with
    // mixed materials need this. Walks material chain per section.
    private static MeshPack.SectionOut[] ResolveSections(UpkManager.Models.UpkFile.Engine.Mesh.FStaticMeshRenderData lod)
    {
        if (lod?.Elements == null || lod.Elements.Count == 0) return Array.Empty<MeshPack.SectionOut>();
        var sections = new System.Collections.Generic.List<MeshPack.SectionOut>(lod.Elements.Count);
        foreach (var el in lod.Elements)
        {
            string d = "", n = "", sp = "", em = "";
            string ss = "", es = "", sr = "", sc = "";
            try
            {
                object obj = el.Material?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                ResolveMapsFromMaterial(obj, out d, out n, out sp, out em, out ss, out es, out sr, out sc);
            }
            catch { }
            // NumTriangles stored as TRIANGLE COUNT — index count = ×3.
            sections.Add(new MeshPack.SectionOut
            {
                FirstIndex = el.FirstIndex,
                NumTriangles = el.NumTriangles,
                Diffuse = d, Normal = n, Specular = sp, Emissive = em,
                Smspsk = ss, Espa = es, Smrr = sr, SpecColor = sc,
            });
        }
        return sections.ToArray();
    }

    // Resolve all 4 PBR map paths from a UMaterialInterface (MIC chain or
    // plain UMaterial Expressions[]). Walks parent chain up to 6 deep.
    // TR48-U: 8-out version now also harvests 6-slot Marvel Heroes channels.
    private static void ResolveMapsFromMaterial(object obj,
        out string diffuse, out string normal, out string specular, out string emissive,
        out string smspsk, out string espa, out string smrr, out string specColor)
    {
        diffuse = normal = specular = emissive = "";
        smspsk = espa = smrr = specColor = "";
        try
        {
            for (int depth = 0; depth < 6 && obj != null; depth++)
            {
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                {
                    diffuse  = NonEmpty(diffuse,  FirstMicTex(mic, "Diffuse", "BaseColor", "Albedo", "Color"));
                    normal   = NonEmpty(normal,   FirstMicTex(mic, "Normal", "NormalMap", "Bump"));
                    specular = NonEmpty(specular, FirstMicTex(mic, "Specular", "SpecMap", "SpecRoughMetal"));
                    emissive = NonEmpty(emissive, FirstMicTex(mic, "Emissive", "Emit", "SelfIllum"));
                    Classify6Slots(mic, ref diffuse, ref normal, ref specular, ref emissive,
                                        ref smspsk, ref espa, ref smrr, ref specColor);
                    obj = mic.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                    continue;
                }
                if (obj is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                {
                    diffuse = NonEmpty(diffuse, TryGetTextureFromInput(um.DiffuseColor));
                    if (um.Expressions != null)
                        foreach (var er in um.Expressions)
                        {
                            var s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>();
                            if (s?.Texture == null) continue;
                            string n = s.Texture.GetPathName() ?? "";
                            string ln = n.ToLowerInvariant();
                            if (diffuse == "" && (ln.EndsWith("_d") || ln.Contains("diff") || ln.EndsWith("_color") || ln.EndsWith("_albedo"))) diffuse = n;
                            if (normal == "" && (ln.EndsWith("_n") || ln.EndsWith("_norm") || ln.Contains("normal") || ln.Contains("bump"))) normal = n;
                            if (specular == "" && (ln.EndsWith("_s") || ln.EndsWith("_spec") || ln.Contains("specular"))) specular = n;
                            if (emissive == "" && (ln.EndsWith("_e") || ln.Contains("emissive") || ln.Contains("emit") || ln.Contains("selfillum"))) emissive = n;
                        }
                }
                break;
            }
        }
        catch { }
    }

    // Phase 4G — dump SMC LODInfo bytes from the LightMapTexture2D refs through
    // ~120 bytes after. Goal: identify which slots are FLightMap2D ScaleVectors
    // (4 × FVector4 = 16 floats) and CoordinateScale/Bias (4 floats) so the
    // viewer can stop relying on the intensity=1.4 fudge.
    private static async Task<int> RunLightMapProbeAsync(string upkPath, string maybeExportName)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        int noneIdx = -1;
        for (int i = 0; i < header.NameTable.Count; i++)
            if (string.Equals(header.NameTable[i].Name?.String, "None", StringComparison.OrdinalIgnoreCase))
            { noneIdx = i; break; }

        int dumped = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (cls == null) continue;
            if (!cls.EndsWith("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(cls, "InstancedStaticMeshComponent", StringComparison.OrdinalIgnoreCase))
                continue;
            string name = e.ObjectNameIndex?.Name ?? "";
            if (!string.IsNullOrEmpty(maybeExportName) &&
                !name.Contains(maybeExportName, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                var rdr = e.UnrealObjectReader;
                if (rdr == null) continue;
                byte[] bytes = rdr.GetBytes();
                if (bytes == null || bytes.Length < 64) continue;

                // Find post-None start.
                int scanStart = 4;
                if (noneIdx >= 0)
                {
                    for (int off = 4; off + 8 <= bytes.Length; off += 4)
                    {
                        if (BitConverter.ToInt32(bytes, off) == noneIdx &&
                            BitConverter.ToInt32(bytes, off + 4) == 0)
                        { scanStart = off + 8; break; }
                    }
                }

                // Find first LightMapTexture2D ref.
                int firstLmOff = -1;
                for (int off = scanStart; off + 4 <= bytes.Length; off += 4)
                {
                    int v = BitConverter.ToInt32(bytes, off);
                    if (v <= 0 || v > header.ExportTable.Count) continue;
                    var hit = header.ExportTable[v - 1];
                    if (string.Equals(hit.ClassReferenceNameIndex?.Name, "LightMapTexture2D", StringComparison.OrdinalIgnoreCase))
                    { firstLmOff = off; break; }
                }
                if (firstLmOff < 0) continue;

                // Dump 160 bytes starting at firstLm — print as (i32, f32) pairs.
                Console.WriteLine($"== {name} (cls={cls}) firstLmOff=0x{firstLmOff:X} bufLen={bytes.Length} ==");
                int dumpEnd = Math.Min(bytes.Length, firstLmOff + 160);
                for (int off = firstLmOff; off + 4 <= dumpEnd; off += 4)
                {
                    int rel = off - firstLmOff;
                    int v = BitConverter.ToInt32(bytes, off);
                    float f = BitConverter.ToSingle(bytes, off);
                    string tag = "";
                    if (v > 0 && v <= header.ExportTable.Count)
                    {
                        var ex = header.ExportTable[v - 1];
                        string xcls = ex.ClassReferenceNameIndex?.Name ?? "";
                        if (xcls.Length > 0) tag = $" -> [{xcls}:{ex.ObjectNameIndex?.Name}]";
                    }
                    bool plausibleFloat = !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) > 1e-10f && Math.Abs(f) < 1e10f;
                    string fStr = plausibleFloat ? f.ToString("G6") : "  -  ";
                    Console.WriteLine($"  +0x{rel:X2}  i32={v,10}  f32={fStr,12}{tag}");
                }
                dumped++;
                if (dumped >= 6) break;
            }
            catch (Exception ex) { Console.Error.WriteLine($"  warn {name}: {ex.Message}"); }
        }
        Console.WriteLine($"done. dumped {dumped} SMCs.");
        return 0;
    }

    // Scan an export's decompressed serial buffer for the first int32 whose
    // value indexes an export of the requested class. Used to fish out
    // LightMapTexture2D / ShadowMapTexture2D refs from SMC LODData without
    // having to RE the full FStaticMeshComponentLODInfo binary layout.
    // Returns the leaf name or "" if nothing matched.
    private static string ScanFirstClassRef(UnrealExportTableEntry e, UpkManager.Models.UpkFile.UnrealHeader header, string className)
        => ScanFirstClassRefWithScale(e, header, className, out _, out _, out _);

    // Extended: returns the FLightMap2D RGB scale that follows the texture ref
    // in StaticMeshComponent LODInfo. Format confirmed via lmprobe across
    // Asgardia_Bridge SMCs:
    //   [int32 textureRef][float scaleR][float scaleG][float scaleB] x 2 textures
    //   [int32 0 terminator][3 floats 2.0 (unknown)][CoordinateScale.XY][CoordinateBias.XY]
    // The 2nd texture's scale varies per-instance with the lighting — that's
    // the data we want to feed three.js as lightMapIntensity.
    private static string ScanFirstClassRefWithScale(UnrealExportTableEntry e, UpkManager.Models.UpkFile.UnrealHeader header, string className,
                                                      out float scaleR, out float scaleG, out float scaleB)
    {
        scaleR = scaleG = scaleB = 1.0f;
        try
        {
            var rdr = e.UnrealObjectReader;
            if (rdr == null) return "";
            byte[] bytes = rdr.GetBytes();
            if (bytes == null || bytes.Length < 4) return "";

            int noneIdx = -1;
            for (int i = 0; i < header.NameTable.Count; i++)
                if (string.Equals(header.NameTable[i].Name?.String, "None", StringComparison.OrdinalIgnoreCase))
                { noneIdx = i; break; }
            int scanStart = 4;
            if (noneIdx >= 0)
            {
                for (int off = 4; off + 8 <= bytes.Length; off += 4)
                {
                    int v = BitConverter.ToInt32(bytes, off);
                    int inst = BitConverter.ToInt32(bytes, off + 4);
                    if (v == noneIdx && inst == 0) { scanStart = off + 8; break; }
                }
            }

            // Pass 1: collect all matching refs in order so we can prefer the
            // DirectionalMaxComponent texture (carries the meaningful scale)
            // over NormalizedAverageColor (always 1.0 scale).
            string firstName = "";
            float firstR = 1, firstG = 1, firstB = 1;
            string dirName = "";
            float dirR = 1, dirG = 1, dirB = 1;
            for (int off = scanStart; off + 16 <= bytes.Length; off += 4)
            {
                int v = BitConverter.ToInt32(bytes, off);
                if (v <= 0 || v > header.ExportTable.Count) continue;
                var hit = header.ExportTable[v - 1];
                string cls = hit.ClassReferenceNameIndex?.Name;
                if (!string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)) continue;
                string nm = hit.ObjectNameIndex?.Name ?? "";
                float r = BitConverter.ToSingle(bytes, off + 4);
                float g = BitConverter.ToSingle(bytes, off + 8);
                float b = BitConverter.ToSingle(bytes, off + 12);
                if (!IsPlausibleScale(r) || !IsPlausibleScale(g) || !IsPlausibleScale(b)) { r = g = b = 1.0f; }
                if (string.IsNullOrEmpty(firstName)) { firstName = nm; firstR = r; firstG = g; firstB = b; }
                if (nm.StartsWith("directionalmaxcomponent", StringComparison.OrdinalIgnoreCase))
                { dirName = nm; dirR = r; dirG = g; dirB = b; }
            }
            if (!string.IsNullOrEmpty(dirName))
            {
                scaleR = dirR; scaleG = dirG; scaleB = dirB;
                return dirName;
            }
            scaleR = firstR; scaleG = firstG; scaleB = firstB;
            return firstName;
        }
        catch { }
        return "";
    }

    private static bool IsPlausibleScale(float f)
        => !float.IsNaN(f) && !float.IsInfinity(f) && f >= 0.0f && f <= 16.0f;

    // ── levelinst — walk every placed actor in a level .upk and emit a
    //    per-instance JSON of {actor, mesh, world transform, materials}.
    //    Drives the 3D viewer's per-instance placement (Scale3D + transform
    //    overrides). Only StaticMesh-bearing actor classes today; skeletal
    //    versions can be added by extending ACTOR_CLASSES + the SMC-prop
    //    reflection block below.
    // ── daemon — stdin/stdout command loop. Used by the server's
    // LevelInstancesDaemonPool to amortize CLR startup + packages.db
    // load across thousands of calls. Each line of stdin is one command
    // with tab-separated args; we write exactly one status line per
    // command. Single-threaded — caller is responsible for not pipelining.
    private static async Task<int> RunDaemonAsync()
    {
        // packages.db path is inferred from the first command's outJsonPath
        // (which lives in <cache>/levelInstances/, so packages.db sits at
        // <cache>/packages.db). Cached after first resolution.
        //
        // NOTE on lifecycle: we deliberately DO NOT share an
        // UpkFileRepository instance across commands. The repo accumulates
        // name-table + export-table state from each loaded .upk; reusing it
        // across cells leaks indices and every cell-after-the-first fails
        // instantly with `Index (FFFFFFxx) is out of range of the NameTable
        // size`. Per-call repo costs the packages.db reload but keeps the
        // big CLR cold-start savings — net is still ~10× faster than a
        // fresh subprocess per cell.
        string cachedPkgDbPath = null;
        bool pkgDbCheckedAndMissing = false;
        // Bypass Console.In/Console.Out — those go through SyncTextReader
        // wrappers + buffered StreamReader, which on Windows pipes can
        // hold writes indefinitely without our being able to flush them.
        // Bind directly to the raw redirected handles so writes hit the
        // OS pipe immediately and reads observe newlines as they arrive.
        var rawIn  = Console.OpenStandardInput();
        var rawOut = Console.OpenStandardOutput();
        var stdin  = new StreamReader(rawIn,  System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var stdout = new StreamWriter(rawOut, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };

        // READY token: pool reads one line before sending commands so it
        // knows the daemon finished startup.
        await stdout.WriteLineAsync("READY");

        while (true)
        {
            string line;
            try { line = await stdin.ReadLineAsync(); }
            catch (Exception ex) { try { await stdout.WriteLineAsync($"ERR fatal {ex.Message}"); } catch { } return 0; }
            if (line == null) return 0; // EOF — caller closed pipe
            line = line.Trim();
            if (line.Length == 0) continue;
            if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase)) return 0;

            string[] parts = line.Split('\t');
            string cmd = parts[0];
            try
            {
                if (string.Equals(cmd, "levelinst", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                {
                    string upk = parts[1];
                    string outJson = parts[2];
                    // Fresh repo per call (see note above). Cache only the
                    // packages.db path so we don't re-stat the disk each time.
                    if (cachedPkgDbPath == null && !pkgDbCheckedAndMissing)
                    {
                        string pkgDb = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(outJson) ?? ".", "..", "packages.db"));
                        if (File.Exists(pkgDb)) cachedPkgDbPath = pkgDb;
                        else pkgDbCheckedAndMissing = true;
                    }
                    var repo = new UpkFileRepository();
                    if (cachedPkgDbPath != null) repo.LoadPackageIndex(cachedPkgDbPath);
                    int code = await RunLevelInstAsync(upk, outJson, repo);
                    await stdout.WriteLineAsync(code == 0 ? "OK" : $"ERR exit {code}");
                }
                else
                {
                    await stdout.WriteLineAsync($"ERR unknown cmd '{cmd}'");
                }
            }
            catch (Exception ex)
            {
                await stdout.WriteLineAsync($"ERR {ex.GetType().Name}: {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}");
            }
        }
    }

    private static async Task<int> RunLevelInstAsync(string upkPath, string outJsonPath)
        => await RunLevelInstAsync(upkPath, outJsonPath, null);

    // Overload accepting a pre-built repository. Daemon mode reuses one
    // repo across invocations so packages.db is loaded once (the original
    // one-shot path constructs a fresh repo + reloads packages.db every
    // call, which dominates wall-clock on small cells).
    private static async Task<int> RunLevelInstAsync(string upkPath, string outJsonPath, UpkFileRepository sharedRepo)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }

        UpkFileRepository repo = sharedRepo;
        if (repo == null)
        {
            repo = new UpkFileRepository();
            // Load global package db if present so SMC.Materials[0] (which often
            // imports from a sibling .upk) resolves correctly to the host MIC.
            string pkgDbCandidate = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "..", "packages.db");
            pkgDbCandidate = Path.GetFullPath(pkgDbCandidate);
            if (File.Exists(pkgDbCandidate)) repo.LoadPackageIndex(pkgDbCandidate);
        }
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        // Marvel Heroes cooks levels with two placement patterns:
        //   (A) Top-level AStaticMeshActor / DynamicSMActor exports that own
        //       a StaticMeshComponent child — common in UE3 generally.
        //   (B) Top-level StaticMeshComponent exports whose Translation/
        //       Rotation/Scale3D ARE the world transform (the wrapping actor
        //       was baked away during cook). HighTown_Terminal_X0_Y0_A.upk is
        //       like this — 89 SMCs, 0 SMActors.
        // We handle both: actor exports through reflection on the
        // StaticMeshComponent property, and SMC exports directly.
        var actorClasses = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StaticMeshActor", "StaticMeshActorBase",
            "DynamicSMActor", "InterpActor",
        };
        var smcClasses = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StaticMeshComponent",
            "CoverMeshComponent", "InstancedStaticMeshComponent", "InteractiveFoliageComponent",
            "SplineMeshComponent", "FracturedStaticMeshComponent", "ImageBasedReflectionComponent",
        };

        var instances = new System.Collections.Generic.List<object>();
        int totalCandidates = 0, parsed = 0, withMesh = 0;
        // SMCs reached through an actor (pattern A) should NOT be emitted
        // again when their export is iterated directly. Track consumed indexes.
        // Pass 1: walk actors first, mark every SMC export they point at.
        var consumedSmc = new System.Collections.Generic.HashSet<int>();
        foreach (var e in header.ExportTable)
        {
            string c = e.ClassReferenceNameIndex?.Name;
            if (string.IsNullOrEmpty(c) || !actorClasses.Contains(c)) continue;
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject u) continue;
                if (u.UObject is not UpkManager.Models.UpkFile.Engine.UActor act) continue;
                var smcProp = act.GetType().GetProperty("StaticMeshComponent");
                var ref0 = smcProp?.GetValue(act) as UpkManager.Models.UpkFile.Tables.FObject;
                if (ref0?.TableEntry is UnrealExportTableEntry exp)
                    consumedSmc.Add(exp.TableIndex);
            }
            catch { }
        }

        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (string.IsNullOrEmpty(cls)) continue;

            bool isActor = actorClasses.Contains(cls);
            bool isSmc   = smcClasses.Contains(cls);
            if (!isActor && !isSmc) continue;
            // Skip SMCs whose actor already emitted them.
            if (isSmc && consumedSmc.Contains(e.TableIndex)) continue;
            totalCandidates++;

            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uo) continue;
                parsed++;

                UpkManager.Models.UpkFile.Engine.UActor actor = null;
                UpkManager.Models.UpkFile.Engine.Mesh.UStaticMeshComponent smc = null;

                if (isActor && uo.UObject is UpkManager.Models.UpkFile.Engine.UActor a)
                {
                    actor = a;
                    UpkManager.Models.UpkFile.Tables.FObject smcRef = null;
                    var smcProp = actor.GetType().GetProperty("StaticMeshComponent");
                    if (smcProp != null) smcRef = smcProp.GetValue(actor) as UpkManager.Models.UpkFile.Tables.FObject;
                    smc = smcRef?.LoadObject<UpkManager.Models.UpkFile.Engine.Mesh.UStaticMeshComponent>();
                }
                else if (isSmc && uo.UObject is UpkManager.Models.UpkFile.Engine.Mesh.UStaticMeshComponent s)
                {
                    smc = s;
                }
                else continue;

                if (smc == null) continue;
                withMesh++;

                // World transform sources, in priority order:
                //   - actor.Location + actor.Rotation + actor.DrawScale[3D]   (pattern A)
                //   - smc.Translation + smc.Rotation + smc.Scale[3D]          (pattern B)
                // Either can be null when the cooker stripped a default value
                // (origin / identity / 1.0). We emit raw + composed.
                var loc = actor?.Location ?? smc.Translation;
                var rot = actor?.Rotation ?? smc.Rotation;
                float drawScale = actor == null ? 1f : (actor.DrawScale == 0f ? 1f : actor.DrawScale);
                var drawScale3D = actor?.DrawScale3D;

                float smcScale = smc.Scale == 0f ? 1f : smc.Scale;
                var smcS3 = smc.Scale3D;

                float fx = (drawScale3D?.X ?? 1f) * (smcS3?.X ?? 1f) * drawScale * smcScale;
                float fy = (drawScale3D?.Y ?? 1f) * (smcS3?.Y ?? 1f) * drawScale * smcScale;
                float fz = (drawScale3D?.Z ?? 1f) * (smcS3?.Z ?? 1f) * drawScale * smcScale;

                // Mesh ref: prefer GetPathName() leaf over .Name so imports
                // pointing at sibling .upks resolve correctly (the local hint
                // name can differ from the actual export name in the host).
                string meshPath = smc.StaticMesh?.GetPathName();
                string meshName = string.IsNullOrEmpty(meshPath) ? smc.StaticMesh?.Name : Leaf(meshPath);
                var matNames = new System.Collections.Generic.List<string>();
                if (smc.Materials != null)
                    foreach (var m in smc.Materials)
                    {
                        // Use GetPathName() leaf — FObject.Name is the local hint
                        // (sometimes "asgardhubfloor_b_mat") which can differ from
                        // the actual export's leaf in the host .upk
                        // ("asgardhubfloor"). PathName resolves through the
                        // import chain to the real target.
                        string path = m?.GetPathName();
                        string leaf = string.IsNullOrEmpty(path) ? (m?.Name) : Leaf(path);
                        matNames.Add(leaf);
                    }

                // Per-instance Materials[0] override: resolve full PBR map set.
                // Many props reuse a mesh but override materials per instance
                // (different paint, dirt, decals). Without this, all instances
                // of the same mesh share the mesh's default material.
                string iD = "", iN = "", iS = "", iE = "";
                if (smc.Materials != null && smc.Materials.Count > 0 && smc.Materials[0] != null)
                {
                    try
                    {
                        var matObj = smc.Materials[0].LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>();
                        ResolveMapsFromMaterial(matObj, out iD, out iN, out iS, out iE, out _, out _, out _, out _);
                    }
                    catch { }
                }

                // Phase 4G: scan the SMC's decompressed serial buffer for a
                // LightMapTexture2D object reference (lives in LODData, deep
                // past the tagged-property block). Heuristic: any int32 in
                // the buffer whose value indexes a LightMapTexture2D export
                // is the per-instance baked lightmap. First hit wins.
                string lightmapName = ScanFirstClassRefWithScale(e, header, "LightMapTexture2D", out float lmR, out float lmG, out float lmB);
                string shadowmapName = ScanFirstClassRef(e, header, "ShadowMapTexture2D");
                // Per-instance scalar intensity for three.js lightMapIntensity.
                // Average of RGB scale recovered from FLightMap2D layout (Phase 4G probe).
                float lmIntensity = (lmR + lmG + lmB) / 3.0f;

                instances.Add(new
                {
                    name = e.ObjectNameIndex?.Name,
                    cls,
                    pattern = isActor ? "actor" : "smc",
                    loc = loc == null ? null : new[] { loc.X, loc.Y, loc.Z },
                    rot = rot == null ? null : new[] { rot.Pitch, rot.Yaw, rot.Roll },
                    drawScale,
                    drawScale3D = drawScale3D == null ? null : new[] { drawScale3D.X, drawScale3D.Y, drawScale3D.Z },
                    smcTranslation = smc.Translation == null ? null : new[] { smc.Translation.X, smc.Translation.Y, smc.Translation.Z },
                    smcRotation    = smc.Rotation == null    ? null : new[] { smc.Rotation.Pitch, smc.Rotation.Yaw, smc.Rotation.Roll },
                    smcScale,
                    smcScale3D = smcS3 == null ? null : new[] { smcS3.X, smcS3.Y, smcS3.Z },
                    finalScale3D = new[] { fx, fy, fz },
                    mesh = meshName,
                    materials = matNames,
                    lightmap = lightmapName,
                    lightmapScale = new[] { lmR, lmG, lmB },
                    lightmapIntensity = lmIntensity,
                    shadowmap = shadowmapName,
                    instDiffuse  = iD,
                    instNormal   = iN,
                    instSpecular = iS,
                    instEmissive = iE,
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"  warn {e.ObjectNameIndex?.Name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ── Particle systems (Phase 4P) — emit placement entries with
        //    template name so the viewer can render a colored sprite at the
        //    world position. Includes both top-level PSCs and PSCs hanging
        //    off Emitter actors.
        var particles = new System.Collections.Generic.List<object>();
        int pCand = 0, pParsed = 0, pSawPsc = 0;
        foreach (UnrealExportTableEntry e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name;
            if (string.IsNullOrEmpty(cls)) continue;
            bool isEmitter = string.Equals(cls, "Emitter", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(cls, "MarvelEmitter", StringComparison.OrdinalIgnoreCase);
            if (!isEmitter) continue;
            pCand++;
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uo) continue;
                pParsed++;
                // Standard UActor parse misses Location for many cooked
                // Emitters because the position lives on a child SceneComponent
                // (RootComponent), not on the actor itself. Scan the export's
                // raw serial buffer for tagged properties Location /
                // RelativeLocation / Translation. UE3 tagged-property wire
                // format: FName name (8B) + FName type (8B) + int32 size + int32
                // arrayIdx + value. StructProperty has an extra FName(8B) for
                // struct type before the value bytes.
                var rdr = e.UnrealObjectReader;
                if (rdr == null) continue;
                byte[] body = rdr.GetBytes();
                if (body == null || body.Length < 8) continue;
                float[] pos = ScanEmitterLocation(body, header);
                if (pos == null) continue;
                pSawPsc++;
                particles.Add(new
                {
                    name = e.ObjectNameIndex?.Name, cls,
                    loc = pos,
                    template = e.ObjectNameIndex?.Name ?? "",
                });
            }
            catch { }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(new { upk = Path.GetFileNameWithoutExtension(upkPath), instances, particles }, jsonOpts));

        Console.Error.WriteLine($"ok {totalCandidates} candidates / {parsed} parsed / {withMesh} with mesh / {particles.Count} particles (pCand={pCand} pParsed={pParsed} pSawPsc={pSawPsc}) -> {outJsonPath} ({new FileInfo(outJsonPath).Length} bytes)");
        return 0;
    }

    // ── animone — extract ONE AnimSequence by export name from a .upk to a
    //    compact MHAP. Resolves the parent AnimSet (via OuterReference) to
    //    map track index → bone name (TrackBoneNames[]). Track payload comes
    //    from UAnimSequence.TranslationData/RotationData which UpkManager has
    //    already decompressed from CompressedByteStream during ReadBuffer.
    private static async Task<int> RunAnimOneAsync(string upkPath, string seqName, string outPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        UnrealExportTableEntry seqHit = null;
        foreach (var e in header.ExportTable)
        {
            if (!string.Equals(e.ClassReferenceNameIndex?.Name, "AnimSequence", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(e.ObjectNameIndex?.Name, seqName, StringComparison.OrdinalIgnoreCase))
            { seqHit = e; break; }
        }
        if (seqHit == null) { Console.Error.WriteLine($"no AnimSequence '{seqName}' in {Path.GetFileName(upkPath)}"); return 3; }

        try
        {
            if (seqHit.UnrealObject == null) await header.ReadExportObjectAsync(seqHit, null);
            if (seqHit.UnrealObject == null) await seqHit.ParseUnrealObject(false, false);
            if (seqHit.UnrealObject is not IUnrealObject uo ||
                uo.UObject is not UpkManager.Models.UpkFile.Engine.Anim.UAnimSequence seq)
            { Console.Error.WriteLine("seq not parsable"); return 4; }

            // Walk OuterReference to find owning AnimSet for TrackBoneNames.
            UpkManager.Models.UpkFile.Engine.Anim.UAnimSet animSet = null;
            string outerName = seqHit.OuterReferenceNameIndex?.Name;
            if (!string.IsNullOrEmpty(outerName))
            {
                foreach (var e in header.ExportTable)
                {
                    if (!string.Equals(e.ClassReferenceNameIndex?.Name, "AnimSet", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(e.ObjectNameIndex?.Name, outerName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                    if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                    if (e.UnrealObject is IUnrealObject auo &&
                        auo.UObject is UpkManager.Models.UpkFile.Engine.Anim.UAnimSet aset)
                    { animSet = aset; break; }
                }
            }
            // Fallback: take the first AnimSet in this upk.
            if (animSet == null)
            {
                foreach (var e in header.ExportTable)
                {
                    if (!string.Equals(e.ClassReferenceNameIndex?.Name, "AnimSet", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                    if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                    if (e.UnrealObject is IUnrealObject auo &&
                        auo.UObject is UpkManager.Models.UpkFile.Engine.Anim.UAnimSet aset)
                    { animSet = aset; break; }
                }
            }

            string[] boneNames = Array.Empty<string>();
            if (animSet?.TrackBoneNames != null)
            {
                boneNames = new string[animSet.TrackBoneNames.Count];
                for (int i = 0; i < boneNames.Length; i++)
                    boneNames[i] = animSet.TrackBoneNames[i]?.Name ?? $"bone{i}";
            }

            var tData = seq.TranslationData;
            var rData = seq.RotationData;
            int trackCount = Math.Max(tData?.Count ?? 0, rData?.Count ?? 0);
            if (trackCount == 0) { Console.Error.WriteLine("no decoded tracks (codec missing or empty)"); return 3; }

            var tracks = new AnimTrack[trackCount];
            for (int i = 0; i < trackCount; i++)
            {
                var trk = new AnimTrack { BoneName = i < boneNames.Length ? boneNames[i] : $"bone{i}" };
                if (tData != null && i < tData.Count)
                {
                    var td = tData[i];
                    int n = td.PosKeys?.Count ?? 0;
                    bool hasTimes = td.Times != null && td.Times.Count == n;
                    trk.PosKeys = new (float, float, float, float)[n];
                    for (int k = 0; k < n; k++)
                    {
                        float t = hasTimes ? td.Times[k] : (n <= 1 ? 0f : (k * seq.SequenceLength / (n - 1)));
                        var v = td.PosKeys[k];
                        trk.PosKeys[k] = (t, v.X, v.Y, v.Z);
                    }
                }
                if (rData != null && i < rData.Count)
                {
                    var rd = rData[i];
                    int n = rd.RotKeys?.Count ?? 0;
                    bool hasTimes = rd.Times != null && rd.Times.Count == n;
                    trk.RotKeys = new (float, float, float, float, float)[n];
                    for (int k = 0; k < n; k++)
                    {
                        float t = hasTimes ? rd.Times[k] : (n <= 1 ? 0f : (k * seq.SequenceLength / (n - 1)));
                        var q = rd.RotKeys[k];
                        trk.RotKeys[k] = (t, q.X, q.Y, q.Z, q.W);
                    }
                }
                tracks[i] = trk;
            }

            var a = new AnimOut
            {
                Name = seqName,
                SequenceLength = seq.SequenceLength,
                NumFrames = (uint)Math.Max(0, seq.NumFrames),
                RateScale = seq.RateScale,
                Tracks = tracks,
            };
            AnimPack.Write(outPath, a);
            int posK = 0, rotK = 0;
            foreach (var t in tracks) { posK += t.PosKeys.Length; rotK += t.RotKeys.Length; }
            Console.WriteLine($"ok {seqName}: {trackCount} tracks, {posK} posKeys, {rotK} rotKeys, len={seq.SequenceLength:0.00}s -> {outPath}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"err {ex.GetType().Name}: {ex.Message}"); return 4; }
    }

    // ── animlist — JSON list of AnimSequence exports in a .upk.
    //    [{name, sequenceLength, numFrames, animSet}, ...]
    private static async Task<int> RunAnimListAsync(string upkPath, string outJsonPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        var list = new System.Collections.Generic.List<object>();
        foreach (var e in header.ExportTable)
        {
            if (!string.Equals(e.ClassReferenceNameIndex?.Name, "AnimSequence", StringComparison.OrdinalIgnoreCase)) continue;
            string nm = e.ObjectNameIndex?.Name ?? "";
            string outer = e.OuterReferenceNameIndex?.Name ?? "";
            float len = 0f; int frames = 0;
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is IUnrealObject uo &&
                    uo.UObject is UpkManager.Models.UpkFile.Engine.Anim.UAnimSequence seq)
                { len = seq.SequenceLength; frames = seq.NumFrames; }
            }
            catch { }
            list.Add(new { name = nm, animSet = outer, sequenceLength = len, numFrames = frames });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(new { upk = Path.GetFileNameWithoutExtension(upkPath), sequences = list }));
        Console.WriteLine($"ok {list.Count} sequences -> {outJsonPath}");
        return 0;
    }

    // ── indexanims — one-pass scan over cookedDir building animIndex.json
    //    and animSetIndex.json (sequenceName → upkBasename, animSetName →
    //    upkBasename). Same shape as meshIndex so the server handler can
    //    locate the source .upk by name.
    private static async Task<int> RunIndexAnimsAsync(string cookedDir, string outJsonPath)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks in {cookedDir} for anims");

        var animIndex    = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var animSetIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // animSet → list of sequence names (for /animsfor lookups)
        var animSetSequences = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string baseName = Path.GetFileNameWithoutExtension(path);
                foreach (var e in header.ExportTable)
                {
                    string cls = e.ClassReferenceNameIndex?.Name;
                    string n = e.ObjectNameIndex?.Name;
                    if (string.IsNullOrEmpty(cls) || string.IsNullOrEmpty(n)) continue;
                    if (string.Equals(cls, "AnimSequence", StringComparison.OrdinalIgnoreCase))
                    {
                        animIndex[n] = baseName;
                        string aset = e.OuterReferenceNameIndex?.Name;
                        if (!string.IsNullOrEmpty(aset))
                            animSetSequences.GetOrAdd(aset, _ => new()).Add(n);
                    }
                    else if (string.Equals(cls, "AnimSet", StringComparison.OrdinalIgnoreCase))
                        animSetIndex[n] = baseName;
                }
            }
            catch { }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 200 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s, {animIndex.Count} anims, {animSetIndex.Count} sets)");
        });
        sw.Stop();
        Console.WriteLine($"done. {done} upks in {sw.Elapsed.TotalSeconds:0}s, {animIndex.Count} anims, {animSetIndex.Count} sets");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(animIndex.ToDictionary(kv => kv.Key, kv => kv.Value), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(outJsonPath).Length} bytes -> {outJsonPath}");

        string animSetPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "animSetIndex.json");
        await File.WriteAllTextAsync(animSetPath,
            System.Text.Json.JsonSerializer.Serialize(animSetIndex.ToDictionary(kv => kv.Key, kv => kv.Value), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(animSetPath).Length} bytes -> {animSetPath}");

        string seqsByAsetPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "animSetSequences.json");
        await File.WriteAllTextAsync(seqsByAsetPath,
            System.Text.Json.JsonSerializer.Serialize(animSetSequences.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray()), jsonOpts));
        Console.WriteLine($"wrote {new FileInfo(seqsByAsetPath).Length} bytes -> {seqsByAsetPath}");
        return 0;
    }

    // ── indexmaterials — scans every upk for Material + MaterialInstanceConstant
    //    exports, walks each one's texture refs (diffuse/normal/spec/emissive),
    //    writes JSON keyed by material leaf name → { d, n, s, e } (texture leaf
    //    names without package path). The viewer falls back to this when a
    //    mesh slot has no diffuseName but the levelinst shipped materials[0].
    private sealed class MatResolveOut
    {
        public string d { get; set; } = "";
        public string n { get; set; } = "";
        public string s { get; set; } = "";
        public string e { get; set; } = "";
        // Phase 4G additions: full PathName forms (Package.ObjectName) for
        // each slot, plus the host .upk this material was resolved out of.
        // Backwards compatible — older consumers ignore unknown fields.
        public string dFull { get; set; } = "";
        public string nFull { get; set; } = "";
        public string sFull { get; set; } = "";
        public string eFull { get; set; } = "";
        public string matUpk { get; set; } = "";
    }

    private static async Task<int> RunIndexMaterialsAsync(string cookedDir, string outJsonPath)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks in {cookedDir} for materials");

        // Cross-upk loading is DISABLED here on purpose. With it on, every
        // upk's material walk recursively loads imports → mip bulk data fills
        // the heap → 30GB+ OOM at scale. The Phase 4M fallback reads
        // MaterialResource[0].UniformExpressionTextures which is LOCAL to the
        // upk being scanned, so cross-upk isn't needed for the index pass.
        // (Live matone shellouts still use it via packages.db, bounded to one
        // material per process.)
        string pkgDbPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "packages.db");
        bool xPkg = false;

        var matIndex = new System.Collections.Concurrent.ConcurrentDictionary<string, MatResolveOut>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Cross-upk loading blows memory if we keep imported upk bodies cached.
        // Lower parallelism + clear header cache after each material walked
        // keeps peak memory bounded.
        int parallelism = xPkg ? 4 : Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                if (xPkg) repo.LoadPackageIndex(pkgDbPath);
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                foreach (var e in header.ExportTable)
                {
                    string cls = e.ClassReferenceNameIndex?.Name ?? "";
                    string nm = e.ObjectNameIndex?.Name ?? "";
                    if (string.IsNullOrEmpty(nm)) continue;
                    bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
                    bool isMat = string.Equals(cls, "Material", StringComparison.OrdinalIgnoreCase);
                    if (!isMic && !isMat) continue;
                    try
                    {
                        if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                        if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                        if (e.UnrealObject is not IUnrealObject uo) continue;
                        var rec = new MatResolveOut();
                        if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                        {
                            // Walk MIC + its parent chain for any of the standard slot names.
                            object cur = mic;
                            for (int depth = 0; depth < 6 && cur is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant cm; depth++)
                            {
                                rec.d = NonEmpty(rec.d, FirstMicTex(cm, "Diffuse", "BaseColor", "Albedo", "Color"));
                                rec.n = NonEmpty(rec.n, FirstMicTex(cm, "Normal", "NormalMap", "Bump"));
                                rec.s = NonEmpty(rec.s, FirstMicTex(cm, "Specular", "SpecMap", "SpecRoughMetal"));
                                rec.e = NonEmpty(rec.e, FirstMicTex(cm, "Emissive", "Emit", "SelfIllum"));
                                try { cur = cm.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>(); }
                                catch { cur = null; }
                            }
                        }
                        else if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
                        {
                            rec.d = NonEmpty(rec.d, TryGetTextureFromInput(um.DiffuseColor));
                            if (um.Expressions != null)
                            {
                                foreach (var er in um.Expressions)
                                {
                                    UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample s = null;
                                    try { s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>(); } catch { }
                                    if (s?.Texture == null) continue;
                                    string n = s.Texture.GetPathName() ?? "";
                                    string ln = n.ToLowerInvariant();
                                    if (rec.d == "" && (ln.Contains("diff") || ln.EndsWith("_d") || ln.EndsWith("_color") || ln.EndsWith("_albedo"))) rec.d = n;
                                    if (rec.n == "" && (ln.EndsWith("_n") || ln.EndsWith("_norm") || ln.Contains("normal") || ln.Contains("bump"))) rec.n = n;
                                    if (rec.s == "" && (ln.EndsWith("_s") || ln.EndsWith("_spec") || ln.Contains("specular"))) rec.s = n;
                                    if (rec.e == "" && (ln.EndsWith("_e") || ln.Contains("emissive") || ln.Contains("emit") || ln.Contains("selfillum"))) rec.e = n;
                                }
                                // Falls back through TryGetTextureFromInput's
                                // graph walker (DiffuseColor → Multiply/Lerp/...
                                // → TextureSample). No name-based fallback —
                                // if the wire graph doesn't reach a sampler from
                                // the diffuse input, we leave it empty rather
                                // than guess a wrong texture.
                            }
                            // PHASE 4M — cooked-shader fallback for base
                            // UMaterials whose Expression graph was stripped
                            // at cook time. The textures still live in
                            // MaterialResource[].UniformExpressionTextures.
                            if (um.MaterialResource != null)
                            {
                                var pool = new System.Collections.Generic.List<string>();
                                foreach (var quality in um.MaterialResource)
                                {
                                    if (quality?.UniformExpressionTextures == null) continue;
                                    foreach (var t in quality.UniformExpressionTextures)
                                    {
                                        string p = t?.GetPathName();
                                        if (string.IsNullOrEmpty(p)) continue;
                                        string lp = p.ToLowerInvariant();
                                        if (lp.Contains("cube") || lp.Contains("reflection") || lp.EndsWith("_env") || lp.Contains("_refl")) continue;
                                        if (!pool.Contains(p)) pool.Add(p);
                                    }
                                    if (pool.Count > 0) break;
                                }
                                AssignFromPool(pool, rec);
                            }
                        }
                        else continue;

                        // Reduce full PathName "Pkg.Group.Texture" to bare leaf so it
                        // round-trips through /webapi/texbyname which uses leaf names.
                        rec.d = Leaf(rec.d);
                        rec.n = Leaf(rec.n);
                        rec.s = Leaf(rec.s);
                        rec.e = Leaf(rec.e);
                        if (rec.d.Length + rec.n.Length + rec.s.Length + rec.e.Length == 0) continue;
                        // First-write-wins for ambiguous names across upks.
                        matIndex.TryAdd(nm, rec);
                    }
                    catch { /* skip unparseable material */ }
                    // Free any imported upks loaded during this material's
                    // graph walk before moving on. Without this the LRU keeps
                    // 10 fully-parsed upks per parallel worker, each with mips
                    // and vertex buffers — 30+ GB peak when scanning all 15884.
                    if (xPkg) repo.ClearHeaderCache();
                }
            }
            catch { /* unreadable upk */ }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 200 == 0)
            {
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s, {matIndex.Count} materials)");
                GC.Collect();
            }
        });
        sw.Stop();
        Console.WriteLine($"done. {done} upks in {sw.Elapsed.TotalSeconds:0}s, {matIndex.Count} unique materials");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(matIndex.ToDictionary(kv => kv.Key, kv => kv.Value),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"wrote {new FileInfo(outJsonPath).Length} bytes -> {outJsonPath}");
        return 0;
    }

    // ── umatprobe — dumps each UMaterial export, lists which standard inputs
    //    (DiffuseColor/EmissiveColor/Normal/Specular/...) have a wired
    //    Expression. Also prints the runtime types of all Expression nodes so
    //    we know which classes the graph walker must handle.
    private static async Task<int> RunUMatProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        string pkgDb = Path.GetFullPath("Cache/packages.db");
        if (File.Exists(pkgDb)) { repo.LoadPackageIndex(pkgDb); Console.WriteLine("[packages.db loaded — cross-upk enabled]"); }
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        int matCount = 0;
        var nodeTypeHist = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(cls, "Material", StringComparison.OrdinalIgnoreCase)) continue;
            string nm = e.ObjectNameIndex?.Name ?? "?";
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo) continue;
                if (uo.UObject is not UpkManager.Models.UpkFile.Engine.Material.UMaterial um) continue;
                matCount++;
                if (matCount > 5) continue;  // dump only first 5 in detail
                Console.WriteLine($"== {nm} ==");
                // Reflect every property whose Type ends in "MaterialInput" — those are
                // the standard wired inputs (DiffuseColor, EmissiveColor, Normal, etc).
                foreach (var prop in um.GetType().GetProperties())
                {
                    if (!prop.PropertyType.Name.EndsWith("MaterialInput")) continue;
                    object iv = null; try { iv = prop.GetValue(um); } catch { }
                    if (iv == null) { Console.WriteLine($"  {prop.Name}: <null>"); continue; }
                    var expProp = iv.GetType().GetProperty("Expression");
                    var ex = expProp?.GetValue(iv);
                    string st = ex == null ? "<null>" : "WIRED";
                    if (ex is UpkManager.Models.UpkFile.Tables.FObject fo)
                    {
                        try {
                            var node = fo.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpression>();
                            st = node == null ? "<no-load>" : node.GetType().Name;
                        } catch (Exception ex2) { st = "<err:" + ex2.GetType().Name + ">"; }
                    }
                    Console.WriteLine($"  {prop.Name}: {st}");
                }
                // UniformExpressionTextures — the cooked per-material texture
                // array indexed by the compiled shader's TextureIndex.
                if (um.MaterialResource != null)
                {
                    for (int qi = 0; qi < um.MaterialResource.Length; qi++)
                    {
                        var mr = um.MaterialResource[qi];
                        if (mr?.UniformExpressionTextures == null) continue;
                        Console.WriteLine($"  MaterialResource[{qi}].UniformExpressionTextures[{mr.UniformExpressionTextures.Count}]:");
                        for (int i = 0; i < mr.UniformExpressionTextures.Count; i++)
                        {
                            var t = mr.UniformExpressionTextures[i];
                            Console.WriteLine($"    [{i}] {t?.GetPathName() ?? "<null>"}  name='{t?.Name}'");
                        }
                    }
                }
                // Expressions array.
                if (um.Expressions != null)
                {
                    Console.WriteLine($"  Expressions[{um.Expressions.Count}]:");
                    foreach (var er in um.Expressions)
                    {
                        // Look up the export entry directly to see what class
                        // it actually is — even when LoadObject<T> returns null.
                        string actualCls = "<no-table-entry>";
                        try
                        {
                            var te = er?.TableEntry;
                            if (te is UnrealExportTableEntry exp)
                                actualCls = exp.ClassReferenceNameIndex?.Name ?? "<no-class>";
                            else if (te is UnrealImportTableEntry imp)
                                actualCls = "Import:" + (imp.ClassNameIndex?.Name ?? "?");
                            else if (te == null)
                                actualCls = "<null-tableentry>";
                            else actualCls = "<other:" + te.GetType().Name + ">";
                        } catch { }
                        try {
                            var node = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpression>();
                            string t = node == null ? ("<load-null cls=" + actualCls + ">") : node.GetType().Name;
                            string detail = "";
                            if (node is UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample ts)
                                detail = " Texture=" + (ts.Texture?.GetPathName() ?? "<null>");
                            Console.WriteLine($"    {t}{detail}");
                        } catch (Exception ex3) { Console.WriteLine($"    <err:{ex3.Message}>"); }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  ERR {nm}: {ex.GetType().Name}: {ex.Message}"); }
        }
        // Also count Expression class types across ALL materials.
        foreach (var e in header.ExportTable)
        {
            string c = e.ClassReferenceNameIndex?.Name ?? "";
            if (!c.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)) continue;
            nodeTypeHist[c] = nodeTypeHist.TryGetValue(c, out int x) ? x + 1 : 1;
        }
        // Dump MaterialInstanceConstant TextureParameterValues — that's where
        // cooked Marvel parks the actual texture refs.
        Console.WriteLine("\n== MaterialInstanceConstant TextureParameterValues ==");
        int micCount = 0, micDumped = 0;
        var allParamNames = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) continue;
            micCount++;
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not IUnrealObject uo) continue;
                if (uo.UObject is not UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic) continue;
                if (micDumped < 8)
                {
                    Console.WriteLine($"-- {e.ObjectNameIndex?.Name} (parent={mic.Parent?.GetPathName() ?? "<none>"})");
                    if (mic.TextureParameterValues != null)
                        foreach (var tp in mic.TextureParameterValues)
                            Console.WriteLine($"     [{tp.ParameterName?.Name}] -> {tp.ParameterValue?.GetPathName() ?? "<null>"}");
                    micDumped++;
                }
                if (mic.TextureParameterValues != null)
                    foreach (var tp in mic.TextureParameterValues)
                    {
                        string pn = tp.ParameterName?.Name ?? "";
                        if (pn.Length == 0) continue;
                        allParamNames[pn] = allParamNames.TryGetValue(pn, out int xx) ? xx + 1 : 1;
                    }
            }
            catch { }
        }
        Console.WriteLine($"\n== MIC ParameterName histogram ({micCount} MICs) ==");
        foreach (var kv in allParamNames.OrderByDescending(k => k.Value).Take(30))
            Console.WriteLine($"  {kv.Value,4}x {kv.Key}");
        Console.WriteLine($"\n== expression-class histogram ==");
        foreach (var kv in nodeTypeHist.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,4}x {kv.Key}");
        Console.WriteLine($"\n{matCount} materials in this upk");
        return 0;
    }

    // ── matone — resolve ONE material by leaf name with full cross-upk
    //    expression walking. Lazy on-demand path: server calls this when its
    //    cached materialIndex.json doesn't have the entry. Fresh process per
    //    call = bounded memory (vs. the 19GB OOM at index-time).
    //
    //    Algorithm:
    //      1. Load packages.db so FObject.LoadObject() follows imports
    //      2. Scan all upks (parallel, header-only) to locate the export
    //         with this exact leaf name + class Material/MaterialInstanceConstant
    //      3. Load that host upk, walk the material with the cross-upk loader
    //      4. Emit {d, n, s, e} JSON
    private static async Task<int> RunMatOneAsync(string cookedDir, string matName, string outJsonPath)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        // Try packages.db in standard location.
        string pkgDbPath = Path.Combine(Path.GetDirectoryName(outJsonPath) ?? ".", "..", "packages.db");
        pkgDbPath = Path.GetFullPath(pkgDbPath);
        if (!File.Exists(pkgDbPath))
            pkgDbPath = Path.GetFullPath("Cache/packages.db");

        // Locate host upk by parallel header-only scan.
        string hostUpk = null;
        string hostCls = null;
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        var foundLock = new object();
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) },
            async (path, ct) =>
        {
            if (hostUpk != null) return;
            try
            {
                var rep = new UpkFileRepository();
                var hdr = await rep.LoadUpkFile(path);
                await hdr.ReadHeaderAsync(null);
                foreach (var e in hdr.ExportTable)
                {
                    if (!string.Equals(e.ObjectNameIndex?.Name, matName, StringComparison.OrdinalIgnoreCase)) continue;
                    string cls = e.ClassReferenceNameIndex?.Name ?? "";
                    string lc = cls.ToLowerInvariant();
                    if (lc != "material" && lc != "materialinstanceconstant") continue;
                    lock (foundLock) { if (hostUpk == null) { hostUpk = path; hostCls = cls; } }
                    break;
                }
            }
            catch { }
        });
        if (hostUpk == null)
        {
            Console.Error.WriteLine($"no host upk found for material '{matName}'");
            await File.WriteAllTextAsync(outJsonPath, "{\"d\":\"\",\"n\":\"\",\"s\":\"\",\"e\":\"\"}");
            return 3;
        }

        // Walk with cross-upk loader.
        var repo = new UpkFileRepository();
        if (File.Exists(pkgDbPath)) repo.LoadPackageIndex(pkgDbPath);
        var header = await repo.LoadUpkFile(hostUpk);
        await header.ReadHeaderAsync(null);
        var rec = new MatResolveOut();
        foreach (var e in header.ExportTable)
        {
            if (!string.Equals(e.ObjectNameIndex?.Name, matName, StringComparison.OrdinalIgnoreCase)) continue;
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            string lc = cls.ToLowerInvariant();
            if (lc != "material" && lc != "materialinstanceconstant") continue;
            if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
            if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
            if (e.UnrealObject is not IUnrealObject uo) break;
            if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
            {
                object cur = mic;
                for (int depth = 0; depth < 6 && cur is UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant cm; depth++)
                {
                    rec.d = NonEmpty(rec.d, FirstMicTex(cm, "Diffuse", "BaseColor", "Albedo", "Color"));
                    rec.n = NonEmpty(rec.n, FirstMicTex(cm, "Normal", "NormalMap", "Bump"));
                    rec.s = NonEmpty(rec.s, FirstMicTex(cm, "Specular", "SpecMap", "SpecRoughMetal"));
                    rec.e = NonEmpty(rec.e, FirstMicTex(cm, "Emissive", "Emit", "SelfIllum"));
                    try { cur = cm.Parent?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialInterface>(); }
                    catch { cur = null; }
                }
            }
            else if (uo.UObject is UpkManager.Models.UpkFile.Engine.Material.UMaterial um)
            {
                rec.d = NonEmpty(rec.d, TryGetTextureFromInput(um.DiffuseColor));
                if (um.Expressions != null)
                {
                    foreach (var er in um.Expressions)
                    {
                        UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample s = null;
                        try { s = er?.LoadObject<UpkManager.Models.UpkFile.Engine.Material.UMaterialExpressionTextureSample>(); } catch { }
                        if (s?.Texture == null) continue;
                        string n = s.Texture.GetPathName() ?? "";
                        string ln = n.ToLowerInvariant();
                        // Skip cube/env textures from the slot pickup.
                        if (ln.Contains("cube") || ln.Contains("reflection") || ln.Contains("_env")) continue;
                        if (rec.d == "" && (ln.Contains("diff") || ln.EndsWith("_d") || ln.EndsWith("_color") || ln.EndsWith("_albedo"))) rec.d = n;
                        if (rec.n == "" && (ln.EndsWith("_n") || ln.EndsWith("_norm") || ln.Contains("normal") || ln.Contains("bump"))) rec.n = n;
                        if (rec.s == "" && (ln.EndsWith("_s") || ln.EndsWith("_spec") || ln.Contains("specular"))) rec.s = n;
                        if (rec.e == "" && (ln.EndsWith("_e") || ln.Contains("emissive") || ln.Contains("emit") || ln.Contains("selfillum"))) rec.e = n;
                    }
                }
                // PHASE 4M: cooked-shader fallback. For base UMaterials whose
                // Expression graph is stripped at cook time, the per-material
                // texture array `MaterialResource[0].UniformExpressionTextures`
                // still holds the actual UTexture refs that the shader binds
                // at runtime. Walk it with the same name-based classifier and
                // fill any slots the Expression walk left blank.
                if (um.MaterialResource != null)
                {
                    var pool = new System.Collections.Generic.List<string>();
                    foreach (var quality in um.MaterialResource)
                    {
                        if (quality?.UniformExpressionTextures == null) continue;
                        foreach (var t in quality.UniformExpressionTextures)
                        {
                            string p = t?.GetPathName();
                            if (string.IsNullOrEmpty(p)) continue;
                            string lp = p.ToLowerInvariant();
                            if (lp.Contains("cube") || lp.Contains("reflection") || lp.EndsWith("_env") || lp.Contains("_refl")) continue;
                            if (!pool.Contains(p)) pool.Add(p);
                        }
                        if (pool.Count > 0) break;   // first non-empty quality level
                    }
                    AssignFromPool(pool, rec);
                }
            }
            break;
        }
        // Preserve full PathNames before collapsing to leaves. Server consumers
        // use the full form to disambiguate textures that share a leaf across
        // packages (TFC manifest is full-path keyed).
        rec.dFull = rec.d; rec.nFull = rec.n; rec.sFull = rec.s; rec.eFull = rec.e;
        rec.matUpk = Path.GetFileName(hostUpk ?? "");
        rec.d = Leaf(rec.d); rec.n = Leaf(rec.n); rec.s = Leaf(rec.s); rec.e = Leaf(rec.e);
        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        await File.WriteAllTextAsync(outJsonPath,
            System.Text.Json.JsonSerializer.Serialize(rec));
        Console.WriteLine($"ok {matName} [host={Path.GetFileNameWithoutExtension(hostUpk)} cls={hostCls}] d={rec.d} n={rec.n} s={rec.s} e={rec.e} dFull={rec.dFull} -> {outJsonPath}");
        return 0;
    }

    // ── indexpackages — one-time scan over every upk's ExportTable building a
    //    MessagePack-serialized database (UpkFilePackageSystem) that maps each
    //    export's full PathName to its (hostUpkFile, exportIndex). The repo's
    //    cross-upk LoadObject path (UnrealImportTableEntry.GetExportEntry →
    //    Repository.GetExportEntry → PackageIndex.GetFirstLocation) requires
    //    this db to follow imports. Without it, every imported FObject loads
    //    as null and material Expression graphs lose their texture nodes.
    private static async Task<int> RunIndexPackagesAsync(string cookedDir, string outDbPath)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks for package index");

        Directory.CreateDirectory(Path.GetDirectoryName(outDbPath) ?? ".");
        var db = new UpkManager.Indexing.UpkFilePackageSystem(outDbPath, createNew: true);
        var dbLock = new object();
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string upkFile = Path.GetFileName(path);
                long size = new FileInfo(path).Length;
                // The path passed at import resolution time is the path the
                // import declares — we mirror with `header.ExportTable[i]
                // .GetPathName()`. UnrealHeader.ExportTable index is 1-based
                // in UE3 (positive = export, negative = import); we store the
                // raw TableIndex as the cross-upk locator.
                foreach (var e in header.ExportTable)
                {
                    string p = e.GetPathName();
                    if (string.IsNullOrEmpty(p)) continue;
                    lock (dbLock) db.AddMapping(p, upkFile, e.TableIndex, size);
                }
            }
            catch { /* skip unreadable upk */ }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 500 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s)");
        });
        sw.Stop();
        db.Save();
        var (objs, total, files) = db.GetStatistics();
        Console.WriteLine($"done. {objs} object paths, {total} mappings, {files} upks in {sw.Elapsed.TotalSeconds:0}s -> {outDbPath} ({new FileInfo(outDbPath).Length} bytes)");
        return 0;
    }

    // ── indexcells — walks every .upk under cookedDir (one or more roots,
    //    semicolon-separated) and emits a JSON dictionary mapping every
    //    CellPrototype export's leaf name to the .upk basename that hosts it.
    //
    //    Region Builder's catalog uses this to recover ~2k cells that are
    //    inline-cooked into region master .upks (no standalone <leaf>.upk on
    //    disk) — without it those cells stay hidden in the palette even
    //    though the client ships them. Output schema:
    //      { "<CellLeafName>": "<UpkFileNameWithExt>", ... }
    //    The basename is preserved (not full path) so the consumer can re-resolve
    //    against its own root list (matches the existing animIndex pattern).
    private static async Task<int> RunIndexCellsAsync(string cookedDir, string outJsonPath)
    {
        var roots = new System.Collections.Generic.List<string>();
        foreach (string raw in (cookedDir ?? "").Split(';'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (!Directory.Exists(trimmed)) { Console.Error.WriteLine($"cookedDir not found, skipping: {trimmed}"); continue; }
            roots.Add(trimmed);
        }
        if (roots.Count == 0) { Console.Error.WriteLine("no usable roots"); return 2; }

        var upks = new System.Collections.Generic.List<string>();
        foreach (string r in roots)
            upks.AddRange(Directory.EnumerateFiles(r, "*.upk", SearchOption.TopDirectoryOnly));
        upks = upks.OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks across {roots.Count} root(s) for CellPrototype exports");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string upkFile = Path.GetFileName(path);
                foreach (var e in header.ExportTable)
                {
                    string cls = e.ClassReferenceNameIndex?.Name ?? "";
                    if (!string.Equals(cls, "CellPrototype", StringComparison.OrdinalIgnoreCase)) continue;
                    string leaf = e.ObjectNameIndex?.Name;
                    if (string.IsNullOrEmpty(leaf)) continue;
                    // First .upk wins for a given cell name; later collisions
                    // ignored to keep the index deterministic across runs.
                    map.TryAdd(leaf, upkFile);
                }
            }
            catch { /* swallow per-file parse errors — index is best-effort */ }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 500 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s)");
        });
        sw.Stop();
        var sorted = map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
        string json = System.Text.Json.JsonSerializer.Serialize(sorted,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(outJsonPath, json);
        Console.WriteLine($"done. {sorted.Count} CellPrototype exports in {sw.Elapsed.TotalSeconds:0}s -> {outJsonPath}");
        return 0;
    }

    // ── indexnames — walks every .upk under cookedDir(s) (semicolon-
    //    separated roots) and emits a JSON dictionary mapping each "interesting"
    //    name-table entry to the .upk basename that first contains it.
    //
    //    Rationale: UE3 streams sub-levels inside a parent district .upk.
    //    Cells like AvengersTower_0_0A have no standalone <leaf>.upk on disk
    //    AND no top-level CellPrototype export (which is what indexcells
    //    catches) — but the cell's name appears as a string inside the parent
    //    district .upk's name table (e.g. AvengersTower_HUB.upk). The catalog
    //    enrichment uses this as a third-chance probe so streaming sub-level
    //    cells stop appearing as "missing" in Region Builder's palette.
    //
    //    Filter: name must match ^[A-Za-z][A-Za-z0-9_]*$ AND length >= 4 AND
    //    contain at least one underscore. That keeps the index small (we don't
    //    want every UE3 builtin type name like "Vector" or "StaticMesh") while
    //    still catching every cell/level/region name (which always contain
    //    underscores: "Sinister_B_ES_A", "Asgardia_Hela_X3Y3", etc.).
    //
    //    Collision policy: FIRST .upk wins, mirroring indexcells. Callers only
    //    need "exists somewhere or not" — they don't enumerate hosts.
    //
    //    Output: { "<name>": "<upkBasenameWithExt>", ... }, written compact.
    private static async Task<int> RunIndexNamesAsync(string cookedDirsSemi, string outJsonPath)
    {
        var roots = new System.Collections.Generic.List<string>();
        foreach (string raw in (cookedDirsSemi ?? "").Split(';'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (!Directory.Exists(trimmed)) { Console.Error.WriteLine($"cookedDir not found, skipping: {trimmed}"); continue; }
            roots.Add(trimmed);
        }
        if (roots.Count == 0) { Console.Error.WriteLine("no usable roots"); return 2; }

        // Dedup .upk by basename across roots — same filename in two roots
        // means an older client copy + a newer one; we want the first to win
        // (matches CookedAssetResolver.FindUpk ordering).
        var byBase = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string r in roots)
        {
            foreach (string full in Directory.EnumerateFiles(r, "*.upk", SearchOption.TopDirectoryOnly))
            {
                string baseName = Path.GetFileNameWithoutExtension(full);
                if (!byBase.ContainsKey(baseName)) byBase[baseName] = full;
            }
        }
        var upks = byBase.Values.OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks across {roots.Count} root(s) for name-table entries");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rx = new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
        int done = 0;
        int upksWithCorruptHeader = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string upkFile = Path.GetFileName(path);
                int n = header.NameTable?.Count ?? 0;
                for (int i = 0; i < n; i++)
                {
                    string s = header.NameTable[i].Name?.String;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.Length < 4) continue;
                    if (s.IndexOf('_') < 0) continue;
                    if (!rx.IsMatch(s)) continue;
                    map.TryAdd(s, upkFile);
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref upksWithCorruptHeader);
                Console.WriteLine($"  WARN corrupt header {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 1000 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s, {map.Count} unique names)");
        });
        sw.Stop();
        var sorted = map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
        string json = System.Text.Json.JsonSerializer.Serialize(sorted,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(outJsonPath, json);
        Console.WriteLine($"done. {upks.Count} upks scanned in {sw.Elapsed.TotalSeconds:0}s, {sorted.Count} unique names, {upksWithCorruptHeader} corrupt header(s) skipped -> {outJsonPath} ({new FileInfo(outJsonPath).Length} bytes)");
        return 0;
    }

    // ── indeximports — walks every .upk under cookedDir(s) (semicolon-
    //    separated roots) and emits a JSON dictionary mapping each imported
    //    object name (filtered to cell-name shape) to the .upk basename that
    //    first contains the import.
    //
    //    Rationale: many cells (e.g. AvengersTower_0_0A) are streamed as UE3
    //    sub-level packages from a parent district .upk. Their leaf names do
    //    NOT appear in the parent's NAME table (so indexnames misses them) and
    //    they are NOT CellPrototype exports (so indexcells misses them) — but
    //    they DO appear in the parent's IMPORT table as Class=Package/Level/
    //    World/LevelStreamingKismet references. This index is the fourth-chance
    //    probe that recovers those cells in Region Builder.
    //
    //    Class filter: Package, Level, World, LevelStreaming*, anything whose
    //    class name contains "LevelStreaming". (LevelStreamingKismet,
    //    LevelStreamingDistance, LevelStreamingPersistent, etc.)
    //
    //    Object-name filter: same shape as indexnames — >=4 chars, alphanumeric+
    //    underscore, must contain at least one underscore.
    //
    //    Collision policy: FIRST .upk wins.
    //    Output: { "<importedName>": "<upkBasenameWithExt>", ... }, compact.
    private static async Task<int> RunIndexImportsAsync(string cookedDirsSemi, string outJsonPath)
    {
        var roots = new System.Collections.Generic.List<string>();
        foreach (string raw in (cookedDirsSemi ?? "").Split(';'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (!Directory.Exists(trimmed)) { Console.Error.WriteLine($"cookedDir not found, skipping: {trimmed}"); continue; }
            roots.Add(trimmed);
        }
        if (roots.Count == 0) { Console.Error.WriteLine("no usable roots"); return 2; }

        // Dedup .upk by basename across roots — same filename in two roots
        // means an older copy + newer one; first wins (matches FindUpk order).
        var byBase = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string r in roots)
        {
            foreach (string full in Directory.EnumerateFiles(r, "*.upk", SearchOption.TopDirectoryOnly))
            {
                string baseName = Path.GetFileNameWithoutExtension(full);
                if (!byBase.ContainsKey(baseName)) byBase[baseName] = full;
            }
        }
        var upks = byBase.Values.OrderBy(p => p).ToList();
        Console.WriteLine($"scanning {upks.Count} upks across {roots.Count} root(s) for import-table package/level refs");

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rx = new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
        int done = 0;
        int upksWithCorruptHeader = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);

        // Stash classes inline so the filter is a single Contains check; the
        // "LevelStreaming" substring already covers Kismet/Distance/Persistent
        // variants, so we just need exact equality for Package/Level/World.
        bool IsStreamedLevelClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (string.Equals(cls, "Package", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cls, "Level",   StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cls, "World",   StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.IndexOf("LevelStreaming", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                string upkFile = Path.GetFileName(path);
                var importTable = header.ImportTable;
                if (importTable == null) return;
                foreach (var imp in importTable)
                {
                    string cls = imp.ClassNameIndex?.Name;
                    if (!IsStreamedLevelClass(cls)) continue;
                    string name = imp.ObjectNameIndex?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.Length < 4) continue;
                    if (name.IndexOf('_') < 0) continue;
                    if (!rx.IsMatch(name)) continue;
                    map.TryAdd(name, upkFile);
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref upksWithCorruptHeader);
                Console.WriteLine($"  WARN corrupt header {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 1000 == 0)
                Console.WriteLine($"  {d}/{upks.Count} scanned ({sw.Elapsed.TotalSeconds:0}s, {map.Count} unique imports)");
        });
        sw.Stop();
        var sorted = map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
        string json = System.Text.Json.JsonSerializer.Serialize(sorted,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(outJsonPath, json);
        Console.WriteLine($"done. {upks.Count} upks scanned in {sw.Elapsed.TotalSeconds:0}s, {sorted.Count} unique imports, {upksWithCorruptHeader} corrupt header(s) skipped -> {outJsonPath} ({new FileInfo(outJsonPath).Length} bytes)");
        return 0;
    }

    // ── findmat — scans every upk for an export named <leafName> of class
    //    Material or MaterialInstanceConstant. Reports each hit.
    private static async Task<int> RunFindMatAsync(string cookedDir, string leafName)
    {
        if (!Directory.Exists(cookedDir)) { Console.Error.WriteLine($"cookedDir not found: {cookedDir}"); return 2; }
        var upks = Directory.EnumerateFiles(cookedDir, "*.upk", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
        int parallelism = Math.Max(2, Environment.ProcessorCount - 1);
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(upks, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (path, ct) =>
        {
            try
            {
                var repo = new UpkFileRepository();
                var header = await repo.LoadUpkFile(path);
                await header.ReadHeaderAsync(null);
                foreach (var e in header.ExportTable)
                {
                    if (!string.Equals(e.ObjectNameIndex?.Name, leafName, StringComparison.OrdinalIgnoreCase)) continue;
                    string cls = e.ClassReferenceNameIndex?.Name ?? "";
                    string lc = cls.ToLowerInvariant();
                    if (lc != "material" && lc != "materialinstanceconstant") continue;
                    hits.Add($"{Path.GetFileNameWithoutExtension(path)}  [{cls}]");
                }
            }
            catch { }
        });
        foreach (var h in hits.OrderBy(x => x)) Console.WriteLine(h);
        Console.WriteLine($"-- {hits.Count} hit(s)");
        return 0;
    }

    // ── smcaprobe — dumps the raw bytes of the StaticMeshCollectionActor
    //    export so we can identify the binary layout of its inlined SMC
    //    transform+mesh array.
    private static async Task<int> RunSmcaProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(cls, "StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"== {e.ObjectNameIndex?.Name} (TableIndex={e.TableIndex} serial=0x{e.SerialDataOffset:X} size={e.SerialDataSize}) ==");
            // Read raw bytes via the export's UnrealObjectReader.
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                var rdr = e.UnrealObjectReader;
                if (rdr == null) { Console.WriteLine("  no reader"); continue; }
                byte[] body = rdr.GetBytes();
                if (body == null) { Console.WriteLine("  no body"); continue; }
                Console.WriteLine($"  body length: {body.Length} bytes");
                // Dump first 256 bytes as hex + ascii
                int dump = Math.Min(256, body.Length);
                for (int i = 0; i < dump; i += 16)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"  {i:X4}: ");
                    for (int j = 0; j < 16 && i + j < dump; j++) sb.Append(body[i+j].ToString("X2") + " ");
                    for (int j = 16; j > 16 - (16 - Math.Min(16, dump - i)); j--) sb.Append("   ");
                    sb.Append(" ");
                    for (int j = 0; j < 16 && i + j < dump; j++)
                    {
                        byte b = body[i+j];
                        sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
                    }
                    Console.WriteLine(sb.ToString());
                }
                // Also dump the export's tail (last 128 bytes) since arrays often serialize last
                if (body.Length > 256)
                {
                    Console.WriteLine($"  ... [end of body, last 128 bytes] ...");
                    int tail = Math.Min(128, body.Length);
                    int start = body.Length - tail;
                    for (int i = 0; i < tail; i += 16)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"  {(start + i):X4}: ");
                        for (int j = 0; j < 16 && i + j < tail; j++) sb.Append(body[start+i+j].ToString("X2") + " ");
                        for (int j = 16 - Math.Min(16, tail - i); j > 0; j--) sb.Append("   ");
                        sb.Append(" ");
                        for (int j = 0; j < 16 && i + j < tail; j++)
                        {
                            byte b = body[start+i+j];
                            sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
                        }
                        Console.WriteLine(sb.ToString());
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message}"); }
            break;  // only first actor
        }
        return 0;
    }

    // ── smcouterprobe — dumps Outer chain for every StaticMeshComponent
    //    export whose StaticMesh is null. Reveals whether they live under a
    //    PrefabInstance, an Actor, or stand alone.
    private static async Task<int> RunSmcOuterProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        int dumped = 0;
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(cls, "StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            // Need to load the export to check StaticMesh field
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                if (e.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uo) continue;
                if (uo.UObject is not UpkManager.Models.UpkFile.Engine.Mesh.UStaticMeshComponent smc) continue;
                if (smc.StaticMesh != null) continue;
            }
            catch { continue; }
            if (dumped >= 5) break;
            dumped++;
            Console.WriteLine($"=== {e.ObjectNameIndex?.Name} (TableIndex={e.TableIndex}) ===");
            int outerRef = e.OuterReference;
            int depth = 0;
            while (outerRef != 0 && depth < 8)
            {
                var outer = header.GetObjectTableEntry(outerRef);
                if (outer == null) break;
                string ocls = "?";
                if (outer is UnrealExportTableEntry exo) ocls = exo.ClassReferenceNameIndex?.Name ?? "?";
                else if (outer is UnrealImportTableEntry imo) ocls = "Import:" + (imo.ClassNameIndex?.Name ?? "?");
                Console.WriteLine($"  outer[{depth}] cls={ocls} name={outer.ObjectNameIndex?.Name}");
                outerRef = (outer as UnrealImportTableEntry)?.OuterReference
                        ?? (outer as UnrealExportTableEntry)?.OuterReference
                        ?? 0;
                depth++;
            }
        }
        return 0;
    }

    // ── importprobe — dumps every ImportTable entry of class Material or
    //    MaterialInstanceConstant, showing the FULL chain: ObjectName +
    //    OuterReference chain (groups, then root Package). The root Package
    //    import's ObjectName is the source upk's basename. Lets us verify
    //    exactly what the level inst's Materials[0] FObject really points at.
    private static async Task<int> RunImportProbeAsync(string upkPath)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);

        Console.WriteLine($"== ImportTable for {Path.GetFileName(upkPath)} ==");
        Console.WriteLine($"   {header.ImportTable.Count} import entries");
        int idx = 0;
        foreach (var imp in header.ImportTable)
        {
            string cls = imp.ClassNameIndex?.Name ?? "";
            string lc = cls.ToLowerInvariant();
            if (lc != "material" && lc != "materialinstanceconstant" && lc != "staticmesh") { idx++; continue; }
            string nm = imp.ObjectNameIndex?.Name ?? "";
            string pkg = imp.PackageNameIndex?.Name ?? "";
            string pth = imp.GetPathName();
            Console.WriteLine($"[#{idx}] cls={cls} pkg={pkg}");
            Console.WriteLine($"        leaf= {nm}");
            Console.WriteLine($"        path= {pth}");
            // Walk outer chain.
            int outerRef = imp.OuterReference;
            int depth = 0;
            while (outerRef != 0 && depth < 8)
            {
                var outer = header.GetObjectTableEntry(outerRef);
                if (outer == null) break;
                string oc = (outer is UnrealImportTableEntry oi ? (oi.ClassNameIndex?.Name ?? "?") : "Export");
                Console.WriteLine($"        outer[{depth}] cls={oc} name={outer.ObjectNameIndex?.Name}");
                outerRef = (outer as UnrealImportTableEntry)?.OuterReference ?? 0;
                depth++;
            }
            idx++;
        }
        return 0;
    }

    // Classify a pool of texture path names into (diffuse, normal, specular,
    // emissive) using suffix patterns. Definitive suffixes win first; the
    // remaining no-suffix entry preferentially fills diffuse, then any
    // leftover *_diff* gets dropped into specular/emissive (Marvel base
    // shader packs spec+emit+reflect+height into a single map).
    private static void AssignFromPool(System.Collections.Generic.List<string> pool, MatResolveOut rec)
    {
        if (pool == null || pool.Count == 0) return;
        var used = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Pass 1: definitive suffixes — normal, spec, emissive.
        foreach (var p in pool)
        {
            string lp = p.ToLowerInvariant();
            if (rec.n == "" && (lp.EndsWith("_n") || lp.EndsWith("_nrm") || lp.EndsWith("_nrml") || lp.EndsWith("_norm") || lp.Contains("normal") || lp.Contains("bump")))
            { rec.n = p; used.Add(p); }
            else if (rec.s == "" && (lp.EndsWith("_s") || lp.EndsWith("_spec") || lp.Contains("specular")))
            { rec.s = p; used.Add(p); }
            else if (rec.e == "" && (lp.EndsWith("_e") || lp.Contains("emissive") || lp.Contains("emit") || lp.Contains("selfillum")))
            { rec.e = p; used.Add(p); }
        }
        // Pass 2: diffuse — prefer no-suffix base names over _diff (which in
        // Marvel often denotes a packed map masquerading as diffuse).
        if (rec.d == "")
        {
            foreach (var p in pool)
            {
                if (used.Contains(p)) continue;
                string lp = p.ToLowerInvariant();
                bool hasKnownSuffix = lp.EndsWith("_diff") || lp.Contains("_diffuse") || lp.EndsWith("_d") ||
                                      lp.EndsWith("_color") || lp.EndsWith("_albedo");
                if (!hasKnownSuffix) { rec.d = p; used.Add(p); break; }
            }
        }
        if (rec.d == "")
        {
            foreach (var p in pool)
            {
                if (used.Contains(p)) continue;
                string lp = p.ToLowerInvariant();
                if (lp.EndsWith("_diff") || lp.Contains("diffuse") || lp.EndsWith("_d") || lp.EndsWith("_color") || lp.EndsWith("_albedo"))
                { rec.d = p; used.Add(p); break; }
            }
        }
        // Pass 3: leftover _diff goes to spec or emissive (Marvel's packed map).
        foreach (var p in pool)
        {
            if (used.Contains(p)) continue;
            string lp = p.ToLowerInvariant();
            if (rec.s == "") { rec.s = p; used.Add(p); continue; }
            if (rec.e == "") { rec.e = p; used.Add(p); continue; }
            break;
        }
    }

    // ── emitterbytes — dumps tagged-property names + types of one Emitter
    //    export, to reveal whether Location/Translation/etc. live on the
    //    actor or on a child component.
    private static async Task<int> RunEmitterBytesAsync(string upkPath, string emitterClass)
    {
        if (!File.Exists(upkPath)) { Console.Error.WriteLine($"upk not found: {upkPath}"); return 2; }
        var repo = new UpkFileRepository();
        var header = await repo.LoadUpkFile(upkPath);
        await header.ReadHeaderAsync(null);
        var names = new string[header.NameTable.Count];
        int noneIdx = -1;
        for (int i = 0; i < names.Length; i++) {
            names[i] = header.NameTable[i].Name?.String ?? "";
            if (noneIdx < 0 && string.Equals(names[i], "None", StringComparison.OrdinalIgnoreCase)) noneIdx = i;
        }
        foreach (var e in header.ExportTable)
        {
            string cls = e.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(cls, emitterClass, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (e.UnrealObject == null) await header.ReadExportObjectAsync(e, null);
                if (e.UnrealObject == null) await e.ParseUnrealObject(false, false);
                var rdr = e.UnrealObjectReader;
                byte[] body = rdr?.GetBytes();
                if (body == null) { Console.WriteLine($"  no body for {e.ObjectNameIndex?.Name}"); continue; }
                Console.WriteLine($"== {e.ObjectNameIndex?.Name} (len={body.Length}) ==");
                Console.Write("  head32: ");
                for (int i = 0; i < Math.Min(32, body.Length); i++) Console.Write(body[i].ToString("X2") + " ");
                Console.WriteLine();
                // Walk tagged props.
                int off = 4;
                if (off + 4 <= body.Length && BitConverter.ToInt32(body, off) == -1) { Console.WriteLine("  [skipped 4-byte marker]"); off += 4; }
                int safety = 0;
                while (off + 16 <= body.Length && safety++ < 60)
                {
                    int nameIdx = BitConverter.ToInt32(body, off);
                    if (nameIdx < 0 || nameIdx >= names.Length) { Console.WriteLine($"  off=0x{off:X} BAD nameIdx={nameIdx}"); break; }
                    if (nameIdx == noneIdx) { Console.WriteLine("  None terminator"); break; }
                    string pn = names[nameIdx];
                    int typeIdx = BitConverter.ToInt32(body, off + 8);
                    string pt = (typeIdx >= 0 && typeIdx < names.Length) ? names[typeIdx] : "?";
                    int size = BitConverter.ToInt32(body, off + 16);
                    Console.WriteLine($"  off=0x{off:X} {pn} {pt} size={size}");
                    int valueStart = off + 24;
                    if (string.Equals(pt, "StructProperty", StringComparison.OrdinalIgnoreCase))
                    {
                        int sn = BitConverter.ToInt32(body, valueStart);
                        Console.WriteLine($"    structName={(sn>=0&&sn<names.Length?names[sn]:"?")}");
                        off = valueStart + 8 + size;
                    }
                    else if (string.Equals(pt, "BoolProperty", StringComparison.OrdinalIgnoreCase)) off = valueStart + 1;
                    else if (string.Equals(pt, "ByteProperty", StringComparison.OrdinalIgnoreCase)) off = valueStart + 8 + size;
                    else off = valueStart + size;
                }
            } catch (Exception ex) { Console.WriteLine($"  err: {ex.Message}"); }
            break;
        }
        return 0;
    }

    // Walks the UE3 tagged-property block in a cooked actor's serial buffer
    // looking for the first Location / RelativeLocation / Translation /
    // OffsetLocation Vector struct. Returns [x, y, z] or null if not found.
    // Independent reimplementation of the wire format — does NOT share code
    // with OAS but uses the same UE3 property layout knowledge.
    //
    // Wire format per tag:
    //   NameIndex (int32) + NameInstance (int32)     ← FName name
    //   NameIndex (int32) + NameInstance (int32)     ← FName type
    //   int32 size
    //   int32 arrayIndex
    //   value bytes (`size` long, except StructProperty prepends an FName
    //                struct-type which is counted in 'size' here per OAS
    //                convention; we account for that explicitly).
    // Terminator: when NameIndex points at "None" entry, stream ends.
    private static float[] ScanEmitterLocation(byte[] body, UpkManager.Models.UpkFile.UnrealHeader header)
    {
        if (header?.NameTable == null) return null;
        // Build target name → table indexes (multi-index in case of duplicates).
        var nameToIdx = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>(StringComparer.OrdinalIgnoreCase);
        var names = new string[header.NameTable.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = header.NameTable[i].Name?.String ?? "";
            if (!nameToIdx.TryGetValue(names[i], out var list)) { list = new(); nameToIdx[names[i]] = list; }
            list.Add(i);
        }
        // Build a set of candidate "Location"-property name indexes.
        var wantedNames = new[] { "Location", "RelativeLocation", "Translation", "OffsetLocation" };
        var wantedNameIdx = new System.Collections.Generic.HashSet<int>();
        foreach (var w in wantedNames)
            if (nameToIdx.TryGetValue(w, out var l)) foreach (var i in l) wantedNameIdx.Add(i);
        int structTypeIdx = nameToIdx.TryGetValue("StructProperty", out var spList) ? spList[0] : -1;
        int vectorStructIdx = nameToIdx.TryGetValue("Vector", out var vList) ? vList[0] : -1;
        if (wantedNameIdx.Count == 0 || structTypeIdx < 0 || vectorStructIdx < 0) return null;
        // Brute-force scan the body for the byte pattern:
        //   <wantedNameIdx int32> <int32 anyInst>
        //   <structTypeIdx int32> <int32 anyInst>
        //   <int32 size>          <int32 arrayIdx>
        //   <vectorStructIdx int32> <int32 anyInst>
        //   <float x> <float y> <float z>
        // Then sanity-check: read the 3 floats, return them if they're a
        // plausible world-space position (not all zero, no NaN/Inf).
        var wantedBytes = new System.Collections.Generic.List<byte[]>();
        foreach (var nameIdx in wantedNameIdx) wantedBytes.Add(BitConverter.GetBytes(nameIdx));
        var structTypeBytes = BitConverter.GetBytes(structTypeIdx);
        var vectorStructBytes = BitConverter.GetBytes(vectorStructIdx);
        for (int off = 0; off + 44 <= body.Length; off++)
        {
            bool nameHit = false;
            foreach (var wb in wantedBytes)
            {
                if (body[off] == wb[0] && body[off+1] == wb[1] && body[off+2] == wb[2] && body[off+3] == wb[3]) { nameHit = true; break; }
            }
            if (!nameHit) continue;
            // Verify structTypeIdx at off+8.
            if (body[off+8] != structTypeBytes[0] || body[off+9] != structTypeBytes[1] ||
                body[off+10] != structTypeBytes[2] || body[off+11] != structTypeBytes[3]) continue;
            // Verify Vector struct name at off+24.
            if (body[off+24] != vectorStructBytes[0] || body[off+25] != vectorStructBytes[1] ||
                body[off+26] != vectorStructBytes[2] || body[off+27] != vectorStructBytes[3]) continue;
            // Parse floats at off+32..off+43.
            float x = BitConverter.ToSingle(body, off + 32);
            float y = BitConverter.ToSingle(body, off + 36);
            float z = BitConverter.ToSingle(body, off + 40);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) continue;
            if (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) < 0.001f) continue;
            return new[] { x, y, z };
        }
        return null;
    }

    private static string Leaf(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        int i = fullPath.LastIndexOf('.');
        return i >= 0 ? fullPath.Substring(i + 1) : fullPath;
    }
}
