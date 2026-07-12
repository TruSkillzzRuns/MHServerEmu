using DDSLib.Constants;
using UpkManager.Models.UpkFile.Compression;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.Texture
{
    public class FTexture2DMipMap
    {
        public int SizeX { get; set; }
        public int SizeY { get; set; }
        public FileFormat OverrideFormat { get; set; }
        public byte[] Data { get; set; } // UntypedBulkData

        // TFC streaming metadata — populated when the mip's bulk data has the
        // StoreInSeparateFile flag set. The actual bytes live in the parent
        // texture's TextureFileCacheName.tfc at this offset.
        public uint BulkDataFlags { get; set; }
        public int BulkUncompressedSize { get; set; }
        public int BulkCompressedSize { get; set; }
        public int BulkCompressedOffset { get; set; }

        public static FTexture2DMipMap ReadMipMap(UBuffer buffer)
        {
            byte[] data = buffer.ReadBulkDataExposed(out UnrealCompressedChunkBulkData chunk);
            var mipMap = new FTexture2DMipMap
            {
                Data = data,
                BulkDataFlags = chunk.BulkDataFlags,
                BulkUncompressedSize = chunk.UncompressedSize,
                BulkCompressedSize = chunk.CompressedSize,
                BulkCompressedOffset = chunk.CompressedOffset,
                SizeX = buffer.Reader.ReadInt32(),
                SizeY = buffer.Reader.ReadInt32()
            };
            return mipMap;
        }
        public override string ToString()
        {
            return $"[{SizeX} x {SizeY}] [{Data?.Length}]";
        }
    }
}
