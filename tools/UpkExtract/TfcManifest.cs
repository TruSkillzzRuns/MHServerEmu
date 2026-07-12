using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UpkExtract;

// Slim port of OmegaAssetStudio's TextureFileCacheManifest.bin reader.
// The manifest is the (TextureGuid, mipIndex) → (tfcFile, offset, size) table
// the engine uses to stream HD mips out of <CookedPCConsole>/*.tfc. The .upk
// itself stores -1 sentinels in the bulk header for TFC mips, so this manifest
// is the only place the real offsets live.
//
// Layout (mirrors OAS TfcManifestReader + TextureManifest):
//   uint32 entryCount
//   foreach entry:
//     string textureName      // length-prefixed UTF8, null-terminated inside
//     16 bytes Guid           // matches UTexture2D.TextureFileCacheGuid
//     string tfcFileName      // e.g. "Textures", "CharTextures"
//     uint32 mipCount
//     foreach mip:
//       uint32 mipIndex       // 0 = largest mip
//       uint32 offset         // absolute offset into the .tfc file
//       uint32 size           // compressed chunk bytes
//
// Lookup is by GUID first (canonical), falling back to texture name.
public sealed class TfcManifest
{
    public sealed class MipRef
    {
        public uint MipIndex;
        public uint Offset;
        public uint Size;
    }

    public sealed class Entry
    {
        public string TextureName = "";   // pkg.texture, lowercased
        public Guid Guid;
        public string TfcFileName = "";   // "Textures" (no extension)
        public List<MipRef> Mips = new();
    }

    private readonly Dictionary<Guid, Entry> _byGuid = new();
    private readonly Dictionary<string, Entry> _byNameLower = new();

    public int EntryCount => _byGuid.Count;
    public string ManifestDir { get; private set; } = "";

    public bool Load(string manifestPath, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(manifestPath))
            {
                error = $"manifest not found: {manifestPath}";
                return false;
            }
            ManifestDir = Path.GetDirectoryName(manifestPath) ?? "";

            using var fs = File.OpenRead(manifestPath);
            using var br = new BinaryReader(fs);

            uint count = br.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                if (fs.Position >= fs.Length) break;

                string rawName = ReadString(br);
                Guid guid = new(br.ReadBytes(16));
                string tfcName = ReadString(br);
                uint mipCount = br.ReadUInt32();

                var e = new Entry
                {
                    TextureName = rawName.ToLowerInvariant(),
                    Guid = guid,
                    TfcFileName = tfcName,
                };
                for (uint m = 0; m < mipCount; m++)
                {
                    e.Mips.Add(new MipRef
                    {
                        MipIndex = br.ReadUInt32(),
                        Offset = br.ReadUInt32(),
                        Size = br.ReadUInt32(),
                    });
                }

                if (guid != Guid.Empty)
                    _byGuid[guid] = e;
                if (!string.IsNullOrEmpty(e.TextureName))
                    _byNameLower[e.TextureName] = e;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // Lookup by GUID is the only safe match — name collisions exist across
    // packages. Name is the fallback for entries with Guid.Empty (rare).
    public Entry TryGet(Guid guid, string textureNameLowerOrNull)
    {
        if (guid != Guid.Empty && _byGuid.TryGetValue(guid, out var hit))
            return hit;
        if (!string.IsNullOrEmpty(textureNameLowerOrNull) && _byNameLower.TryGetValue(textureNameLowerOrNull, out var n))
            return n;
        return null;
    }

    // OAS-format length-prefixed UTF8 string (length INCLUDES the trailing
    // null byte; trim at first null).
    private static string ReadString(BinaryReader br)
    {
        uint len = br.ReadUInt32();
        byte[] buf = br.ReadBytes((int)len);
        int nul = Array.IndexOf(buf, (byte)0);
        if (nul >= 0) buf = buf[..nul];
        return Encoding.UTF8.GetString(buf);
    }
}
