using System;
using System.IO;
using System.Text;

namespace TfcExtract;

internal enum DdsFmt
{
    DXT1,
    DXT3,
    DXT5,
    BGRA8,
}

/// <summary>
/// Minimal DDS header writer. Wraps raw mip-0 pixel bytes from a DXT-compressed
/// or BGRA8 surface. Mip count is 1 (we only get one TFC mip per call).
/// </summary>
internal static class DdsWriter
{
    private const uint MAGIC = 0x20534444;             // "DDS "
    private const uint DDSD_CAPS = 0x1;
    private const uint DDSD_HEIGHT = 0x2;
    private const uint DDSD_WIDTH = 0x4;
    private const uint DDSD_PIXELFORMAT = 0x1000;
    private const uint DDSD_LINEARSIZE = 0x80000;
    private const uint DDSD_PITCH = 0x8;
    private const uint DDPF_FOURCC = 0x4;
    private const uint DDPF_RGB = 0x40;
    private const uint DDPF_ALPHAPIXELS = 0x1;
    private const uint DDSCAPS_TEXTURE = 0x1000;

    public static void Write(string outPath, int width, int height, DdsFmt fmt, byte[] pixels)
    {
        using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(MAGIC);

        // DDS_HEADER (124 bytes)
        bw.Write((uint)124);                           // dwSize
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;

        bool fourcc = fmt != DdsFmt.BGRA8;
        if (fourcc) flags |= DDSD_LINEARSIZE;
        else flags |= DDSD_PITCH;

        bw.Write(flags);
        bw.Write((uint)height);
        bw.Write((uint)width);

        uint pitchOrLinearSize = fmt switch
        {
            DdsFmt.DXT1 => (uint)Math.Max(1, ((width + 3) / 4)) * (uint)Math.Max(1, (height + 3) / 4) * 8,
            DdsFmt.DXT3 or DdsFmt.DXT5
                       => (uint)Math.Max(1, ((width + 3) / 4)) * (uint)Math.Max(1, (height + 3) / 4) * 16,
            _          => (uint)width * 4u,
        };
        bw.Write(pitchOrLinearSize);
        bw.Write((uint)0);                             // depth
        bw.Write((uint)1);                             // mipMapCount
        for (int i = 0; i < 11; i++) bw.Write((uint)0); // reserved1

        // DDS_PIXELFORMAT (32 bytes)
        bw.Write((uint)32);                            // size
        if (fourcc)
        {
            bw.Write(DDPF_FOURCC);
            string cc = fmt switch
            {
                DdsFmt.DXT1 => "DXT1",
                DdsFmt.DXT3 => "DXT3",
                DdsFmt.DXT5 => "DXT5",
                _ => "    ",
            };
            bw.Write(Encoding.ASCII.GetBytes(cc));
            bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
            bw.Write((uint)0); bw.Write((uint)0);
        }
        else
        {
            bw.Write(DDPF_RGB | DDPF_ALPHAPIXELS);
            bw.Write((uint)0);                          // no fourcc
            bw.Write((uint)32);                         // bpp
            bw.Write((uint)0x00FF0000);                 // R mask (BGRA8 = B,G,R,A in memory)
            bw.Write((uint)0x0000FF00);                 // G mask
            bw.Write((uint)0x000000FF);                 // B mask
            bw.Write((uint)0xFF000000);                 // A mask
        }

        bw.Write(DDSCAPS_TEXTURE);                      // dwCaps
        bw.Write((uint)0);                              // dwCaps2
        bw.Write((uint)0);                              // dwCaps3
        bw.Write((uint)0);                              // dwCaps4
        bw.Write((uint)0);                              // dwReserved2

        bw.Write(pixels);
    }
}
