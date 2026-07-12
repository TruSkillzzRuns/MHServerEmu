using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TfcExtract;

/// <summary>
/// Parser for MarvelGame's TextureFileCacheManifest.bin
/// (per OAS TfcManifestReader.cs — uint32 count + per-entry records).
/// </summary>
internal sealed class TfcManifest
{
    public string SourceDirectory { get; }
    public List<TfcManifestEntry> Entries { get; } = new();

    private TfcManifest(string dir) { SourceDirectory = dir; }

    public static TfcManifest Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Manifest not found.", path);

        var doc = new TfcManifest(Path.GetDirectoryName(path) ?? "");
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        uint count = br.ReadUInt32();

        // Two on-disk layouts seen in MH manifests:
        //   1.34/1.52: (tfcName, textureFullName, GUID, mipCount)
        //   1.53:      (textureFullName, GUID, tfcName, mipCount)   [matches OAS's original order]
        // Detect by peeking the first record's first string: texture names
        // contain a '.' (Package.Object), TFC names don't.
        bool textureFirst = false;
        if (count > 0)
        {
            long detectStart = br.BaseStream.Position;
            try
            {
                string firstString = ReadUeString(br);
                textureFirst = firstString.IndexOf('.') >= 0;
            }
            catch { /* leave textureFirst = false */ }
            br.BaseStream.Position = detectStart;
        }

        for (uint i = 0; i < count; i++)
        {
            if (br.BaseStream.Position >= br.BaseStream.Length) break;

            long entryStart = br.BaseStream.Position;
            string fullName;
            byte[] guidBytes;
            string tfcName;
            uint mipCount;
            try
            {
                if (textureFirst)
                {
                    // 1.53 layout.
                    fullName = ReadUeString(br);
                    guidBytes = br.ReadBytes(16);
                    tfcName = ReadUeString(br);
                    mipCount = br.ReadUInt32();
                }
                else
                {
                    // 1.34/1.52 layout.
                    tfcName = ReadUeString(br);
                    fullName = ReadUeString(br);
                    guidBytes = br.ReadBytes(16);
                    mipCount = br.ReadUInt32();
                }
            }
            catch (EndOfStreamException)
            {
                // Tail of file truncated — stop parsing gracefully (matches OAS behavior).
                break;
            }
            Guid guid = guidBytes.Length == 16 ? new Guid(guidBytes) : Guid.Empty;

            var entry = new TfcManifestEntry
            {
                FullName = fullName,
                Guid = guid,
                TfcFileName = tfcName,
            };
            int dot = fullName.LastIndexOf('.');
            entry.PackageName = dot > 0 ? fullName[..dot] : "";
            entry.TextureName = dot >= 0 && dot + 1 < fullName.Length
                ? fullName[(dot + 1)..]
                : fullName;

            try
            {
                for (uint m = 0; m < mipCount; m++)
                {
                    int chunkIndex = br.ReadInt32();
                    uint offset = br.ReadUInt32();
                    uint size = br.ReadUInt32();
                    entry.Mips.Add(new TfcMip
                    {
                        Index = chunkIndex,
                        Offset = offset,
                        Size = size,
                    });
                }
            }
            catch (EndOfStreamException)
            {
                doc.Entries.Add(entry);
                break;
            }
            doc.Entries.Add(entry);
        }
        return doc;
    }

    // UE3 FString: int32 length. Positive = ASCII/UTF8 bytes, length INCLUDES trailing NUL.
    // Negative = UTF-16, abs(len) chars (each 2 bytes), includes trailing NUL.
    private static string ReadUeString(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len == 0) return string.Empty;
        if (len > 0)
        {
            byte[] bytes = br.ReadBytes(len);
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul >= 0) bytes = bytes[..nul];
            return Encoding.UTF8.GetString(bytes);
        }
        else
        {
            int chars = -len;
            byte[] bytes = br.ReadBytes(chars * 2);
            string s = Encoding.Unicode.GetString(bytes);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            return s;
        }
    }
}

internal sealed class TfcManifestEntry
{
    public string FullName { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string TextureName { get; set; } = "";
    public string TfcFileName { get; set; } = "";
    public Guid Guid { get; set; }
    public List<TfcMip> Mips { get; } = new();
}

internal sealed class TfcMip
{
    public int Index;
    public uint Offset;
    public uint Size;
}
