using System;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine.GFx
{
    [UnrealClass("SwfMovie")]
    public class USwfMovie : UObject
    {
        /// <summary>
        /// Raw decompressed SWF bytes extracted from the bulk data block.
        /// This is the data that begins with the SWF magic signature (FWS / CWS / ZWS).
        /// </summary>
        public byte[] RawSwfBytes { get; private set; }

        /// <summary>
        /// Byte offset within the export's serial data where the bulk data block starts.
        /// Everything before this offset is the UObject property prefix (NetIndex + tagged properties).
        /// </summary>
        public int BulkDataOffset { get; private set; }

        public override void ReadBuffer(UBuffer buffer)
        {
            base.ReadBuffer(buffer); // reads NetIndex + tagged property stream up to None terminator

            BulkDataOffset = buffer.Reader.CurrentOffset;

            if (buffer.Reader.Remaining >= 16)
            {
                RawSwfBytes = buffer.ReadBulkData();
            }

            if (RawSwfBytes == null)
            {
                // Scan the full serial data (covers inline raw data past property stream).
                RawSwfBytes = FindGfxSignature(buffer.Reader.GetBytes());
            }

            if (RawSwfBytes == null)
            {
                // GFx/SWF data stored as a large TArray<byte> tagged property.
                // After base.ReadBuffer() those bytes live in the property's DataReader.
                foreach (var prop in Properties)
                {
                    if (prop.Value?.PropertyValue is byte[] bytes && bytes.Length > 8)
                    {
                        var found = FindGfxSignature(bytes);
                        if (found != null) { RawSwfBytes = found; break; }
                    }
                }
            }
        }

        // SWF magic bytes (standard Flash) + Scaleform GFx-specific magic bytes.
        // GFX = uncompressed Scaleform GFx, CFX = compressed Scaleform GFx.
        private static readonly byte[][] GfxMagics =
        [
            [(byte)'F', (byte)'W', (byte)'S'],   // uncompressed SWF
            [(byte)'C', (byte)'W', (byte)'S'],   // zlib-compressed SWF
            [(byte)'Z', (byte)'W', (byte)'S'],   // LZMA-compressed SWF
            [(byte)'G', (byte)'F', (byte)'X'],   // uncompressed Scaleform GFx
            [(byte)'C', (byte)'F', (byte)'X'],   // compressed Scaleform GFx
        ];

        private static byte[] FindGfxSignature(byte[] data)
        {
            for (int i = 0; i <= data.Length - 8; i++)
            {
                foreach (var magic in GfxMagics)
                {
                    if (data[i] != magic[0] || data[i + 1] != magic[1] || data[i + 2] != magic[2])
                        continue;

                    byte ver = data[i + 3];
                    if (ver < 1 || ver > 30) continue;

                    uint fileSize = BitConverter.ToUInt32(data, i + 4);
                    if (fileSize < 8) continue;

                    int end = (int)Math.Min((long)i + fileSize, data.Length);
                    return data[i..end];
                }
            }
            return null;
        }
    }
}
