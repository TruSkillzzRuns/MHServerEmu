using System;
using System.IO;

namespace TfcExtract;

/// <summary>
/// Parser for UE3 "UnrealCompressedChunkHeader" layout:
///   uint32 magic   (0x9E2A83C1)
///   uint32 blockSize
///   uint32 totalCompressed
///   uint32 totalUncompressed
///   N x (uint32 compSize, uint32 uncompSize)   N = ceil(totalUncompressed / blockSize)
///   then for each block: compSize bytes of LZO
/// Concatenates all decompressed blocks into one byte[].
/// </summary>
internal static class LzoChunked
{
    public const uint Magic = 0x9E2A83C1;

    public static byte[] DecompressAll(byte[] raw)
    {
        using var ms = new MemoryStream(raw);
        using var br = new BinaryReader(ms);
        return DecompressAll(br);
    }

    public static byte[] DecompressAll(BinaryReader br)
    {
        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"Bad chunk magic: 0x{magic:X8} (expected 0x{Magic:X8}).");

        int blockSize = br.ReadInt32();
        int totalComp = br.ReadInt32();
        int totalUncomp = br.ReadInt32();

        if (blockSize <= 0 || totalUncomp < 0 || totalComp < 0)
            throw new InvalidDataException(
                $"Invalid header sizes blockSize={blockSize} comp={totalComp} uncomp={totalUncomp}.");

        int blockCount = (totalUncomp + blockSize - 1) / blockSize;

        var blocks = new (int comp, int uncomp)[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            int c = br.ReadInt32();
            int u = br.ReadInt32();
            blocks[i] = (c, u);
        }

        byte[] outBuf = new byte[totalUncomp];
        int writePos = 0;

        for (int i = 0; i < blockCount; i++)
        {
            var (c, u) = blocks[i];
            byte[] srcChunk = br.ReadBytes(c);
            if (srcChunk.Length != c)
                throw new EndOfStreamException(
                    $"Block {i}: wanted {c} compressed bytes, got {srcChunk.Length}.");
            byte[] dec = LzoNative.Decompress(srcChunk, c, u);
            Buffer.BlockCopy(dec, 0, outBuf, writePos, u);
            writePos += u;
        }

        if (writePos != totalUncomp)
            throw new InvalidDataException(
                $"Decompressed {writePos} bytes, header said {totalUncomp}.");

        return outBuf;
    }
}
