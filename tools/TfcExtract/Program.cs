using System;
using System.IO;
using System.Linq;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TfcExtract;

internal static class Program
{
    private const int OK = 0;
    private const int ERR = 1;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();

            // --list mode
            if (args.Length >= 2 && args[1].Equals("--list", StringComparison.OrdinalIgnoreCase))
            {
                string manifestPath = args[0];
                string? filter = args.Length >= 3 ? args[2] : null;
                return ListMode(manifestPath, filter);
            }

            // --has mode: fast existence check.
            // Exit 0 + one tab-separated line "<tfc>\t<mip0Size>\t<mipCount>" if found.
            // Exit 1 + nothing on stdout if not found. Used by GroundTexWebHandler
            // to probe a list of candidate texture names cheaply before deciding
            // which one to extract.
            if (args.Length >= 3 && args[1].Equals("--has", StringComparison.OrdinalIgnoreCase))
                return HasMode(args[0], args[2], prefix: false);

            // --has-prefix mode: matches any entry whose FullName starts with the
            // given prefix (case-insensitive). Useful when material refs are partial.
            if (args.Length >= 3 && args[1].Equals("--has-prefix", StringComparison.OrdinalIgnoreCase))
                return HasMode(args[0], args[2], prefix: true);

            if (args.Length < 3) return Usage();

            string manifest = args[0];
            string texturePath = args[1];
            string outFile = args[2];

            DdsFmt fmt = DdsFmt.DXT5;
            int? overrideW = null, overrideH = null;
            bool rawBin = outFile.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
            string? pngOut = null;

            // Allow flags after position 3 (including the last arg, since --png-out
            // takes a value — loop bound used to be args.Length-1 which dropped the
            // last value-pair).
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i].Equals("--fmt", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out fmt))
                    {
                        Console.Error.WriteLine($"Unknown --fmt '{args[i]}'.");
                        return ERR;
                    }
                }
                else if (args[i].Equals("--w", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    overrideW = int.Parse(args[++i]);
                else if (args[i].Equals("--h", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    overrideH = int.Parse(args[++i]);
                else if (args[i].Equals("--png-out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    pngOut = args[++i];
            }

            return ExtractMode(manifest, texturePath, outFile, fmt, overrideW, overrideH, rawBin, pngOut);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return ERR;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  TfcExtract.exe <manifestPath> <textureFullPath> <out.dds|.bin> [--fmt DXT1|DXT3|DXT5|BGRA8] [--w N --h N] [--png-out <path.png>]");
        Console.Error.WriteLine("  TfcExtract.exe <manifestPath> --list [filter]");
        Console.Error.WriteLine("  TfcExtract.exe <manifestPath> --has <textureFullName>");
        Console.Error.WriteLine("  TfcExtract.exe <manifestPath> --has-prefix <namePrefix>");
        return ERR;
    }

    private static int HasMode(string manifestPath, string needle, bool prefix)
    {
        var doc = TfcManifest.Load(manifestPath);
        TfcManifestEntry? hit = null;
        foreach (var e in doc.Entries)
        {
            bool match = prefix
                ? e.FullName.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                : (string.Equals(e.FullName, needle, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(e.TextureName, needle, StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            // Prefer entries that actually carry mip0 data.
            if (hit == null || (e.Mips.Count > 0 && (hit.Mips.Count == 0 || e.Mips[0].Size > hit.Mips[0].Size)))
                hit = e;
            if (!prefix && hit != null && hit.Mips.Count > 0) break;
        }
        if (hit == null) return ERR;
        uint mip0 = hit.Mips.Count > 0 ? hit.Mips[0].Size : 0u;
        Console.WriteLine($"{hit.TfcFileName}\t{mip0}\t{hit.Mips.Count}\t{hit.FullName}");
        return OK;
    }

    private static int ListMode(string manifestPath, string? filter)
    {
        var doc = TfcManifest.Load(manifestPath);
        Console.WriteLine($"Manifest: {manifestPath}");
        Console.WriteLine($"Entries:  {doc.Entries.Count}");
        Console.WriteLine();
        Console.WriteLine($"{"#",6} {"TFC",-22} {"Mips",4} {"Off(mip0)",12} {"Size(mip0)",12}  Name");

        int shown = 0;
        for (int i = 0; i < doc.Entries.Count && shown < 50; i++)
        {
            var e = doc.Entries[i];
            if (filter != null && e.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var m = e.Mips.Count > 0 ? e.Mips[0] : null;
            Console.WriteLine(
                $"{i,6} {e.TfcFileName,-22} {e.Mips.Count,4} " +
                $"{(m?.Offset ?? 0),12} {(m?.Size ?? 0),12}  {e.FullName}");
            shown++;
        }
        if (shown == 0 && filter != null)
            Console.WriteLine($"(no entries matched '{filter}')");
        return OK;
    }

    private static int ExtractMode(string manifestPath, string textureName, string outFile,
                                   DdsFmt fmt, int? overrideW, int? overrideH, bool rawBin,
                                   string? pngOut = null)
    {
        var doc = TfcManifest.Load(manifestPath);

        var hits = doc.Entries
            .Where(e => string.Equals(e.FullName, textureName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.TextureName, textureName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count == 0)
        {
            Console.Error.WriteLine($"Texture '{textureName}' not found in manifest.");
            return ERR;
        }
        if (hits.Count > 1)
            Console.Error.WriteLine($"WARN: {hits.Count} matches for '{textureName}', using first ({hits[0].FullName}).");

        var entry = hits[0];
        if (entry.Mips.Count == 0)
        {
            Console.Error.WriteLine("Entry has zero mips.");
            return ERR;
        }

        var mip = entry.Mips[0];
        string tfcPath = TfcReader.ResolveTfcPath(doc.SourceDirectory, entry.TfcFileName);
        Console.Error.WriteLine($"Source: {tfcPath}  offset=0x{mip.Offset:X} size=0x{mip.Size:X}");

        byte[] raw = TfcReader.ReadChunk(tfcPath, mip.Offset, mip.Size);
        byte[] decoded = LzoChunked.DecompressAll(raw);
        Console.Error.WriteLine($"Decompressed {decoded.Length} bytes from {raw.Length} compressed.");

        if (rawBin)
        {
            File.WriteAllBytes(outFile, decoded);
            Console.Error.WriteLine($"Wrote raw payload to {outFile}.");
            return OK;
        }

        // Manifest doesn't store dims/format. Caller passes a guess via --w/--h/--fmt,
        // but it's frequently wrong (e.g. 1024x1024 DXT5 default for a 512x512 DXT1
        // diffuse). Probe what (size, fmt) combination actually matches the decoded
        // byte length and use that instead. Prefer DXT1 first (most common for
        // diffuse), then DXT5, then DXT3, then BGRA8.
        int dataLen = decoded.Length;
        DdsFmt inferredFmt = fmt;
        int inferredW = overrideW ?? 0, inferredH = overrideH ?? 0;
        bool inferred = false;
        foreach (var tryFmt in new[] { DdsFmt.DXT1, DdsFmt.DXT5, DdsFmt.DXT3, DdsFmt.BGRA8 })
        {
            int bytesPerPixel = tryFmt switch
            {
                DdsFmt.DXT1 => 0, // 8 bytes per 4x4 block = 0.5 byte/pixel
                DdsFmt.DXT3 => 1,
                DdsFmt.DXT5 => 1,
                DdsFmt.BGRA8 => 4,
                _ => 0
            };
            long pixelCount = tryFmt == DdsFmt.DXT1 ? (long)dataLen * 2 : (long)dataLen / Math.Max(1, bytesPerPixel);
            if (pixelCount <= 0) continue;
            int side = (int)Math.Round(Math.Sqrt(pixelCount));
            if (side < 4 || side > 4096) continue;
            if (side * side != pixelCount) continue;
            // power of 2?
            if ((side & (side - 1)) != 0) continue;
            inferredFmt = tryFmt;
            inferredW = side;
            inferredH = side;
            inferred = true;
            break;
        }
        if (inferred && (inferredFmt != fmt || inferredW != overrideW || inferredH != overrideH))
        {
            Console.Error.WriteLine($"Inferred {inferredFmt} {inferredW}x{inferredH} from {dataLen} bytes (overrides --fmt={fmt} --w={overrideW} --h={overrideH})");
            fmt = inferredFmt;
            overrideW = inferredW;
            overrideH = inferredH;
        }
        else if (!overrideW.HasValue || !overrideH.HasValue)
        {
            Console.Error.WriteLine($"DDS output requires --w and --h (mip dimensions). TFC manifest does not store size. Inference failed for {dataLen} bytes.");
            Console.Error.WriteLine("Falling back to writing raw bytes alongside as .bin.");
            string bin = Path.ChangeExtension(outFile, ".bin");
            File.WriteAllBytes(bin, decoded);
            Console.Error.WriteLine($"Wrote raw payload to {bin}.");
            return OK;
        }

        DdsWriter.Write(outFile, overrideW.Value, overrideH.Value, fmt, decoded);
        Console.Error.WriteLine($"Wrote DDS ({fmt} {overrideW}x{overrideH}) to {outFile}.");

        // Optional PNG output — decode the raw compressed mip in-process and
        // emit a ready-to-serve PNG. Used by the WebFrontend ground-tex pipeline
        // so it doesn't need its own DDS→PNG step.
        if (!string.IsNullOrEmpty(pngOut))
        {
            try
            {
                byte[]? rgba = DecodeMipToRgba(decoded, overrideW.Value, overrideH.Value, fmt);
                if (rgba == null)
                {
                    Console.Error.WriteLine($"--png-out: unsupported fmt {fmt} for PNG decode");
                    return ERR;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(pngOut) ?? ".");
                using var img = Image.LoadPixelData<Rgba32>(rgba, overrideW.Value, overrideH.Value);
                img.SaveAsPng(pngOut);
                Console.Error.WriteLine($"Wrote PNG ({overrideW}x{overrideH}) to {pngOut}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"--png-out failed: {ex.GetType().Name}: {ex.Message}");
                return ERR;
            }
        }
        return OK;
    }

    private static byte[]? DecodeMipToRgba(byte[] data, int width, int height, DdsFmt fmt)
    {
        var decoder = new BcDecoder();
        ColorRgba32[]? pixels;
        try
        {
            pixels = fmt switch
            {
                DdsFmt.DXT1 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc1),
                DdsFmt.DXT3 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc2),
                DdsFmt.DXT5 => decoder.DecodeRaw(data, width, height, CompressionFormat.Bc3),
                DdsFmt.BGRA8 => null,
                _ => null,
            };
        }
        catch { return null; }

        if (fmt == DdsFmt.BGRA8)
        {
            // Cooked UE3 A8R8G8B8 → repack to RGBA.
            var rgba = new byte[width * height * 4];
            int n = Math.Min(rgba.Length, data.Length);
            for (int i = 0; i + 3 < n; i += 4)
            {
                byte a = data[i + 0], r = data[i + 1], g = data[i + 2], b = data[i + 3];
                rgba[i + 0] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
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
}
