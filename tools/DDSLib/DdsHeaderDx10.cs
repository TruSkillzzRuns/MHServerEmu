using System.IO;

namespace DDSLib
{
    public sealed class DdsHeaderDx10
    {
        internal DdsHeaderDx10()
        {
        }

        internal DdsHeaderDx10(uint dxgiFormat, uint resourceDimension = 3, uint miscFlag = 0, uint arraySize = 1, uint miscFlags2 = 0)
        {
            DxgiFormat = dxgiFormat;
            ResourceDimension = resourceDimension;
            MiscFlag = miscFlag;
            ArraySize = arraySize;
            MiscFlags2 = miscFlags2;
        }

        public uint DxgiFormat { get; private set; }

        public uint ResourceDimension { get; private set; }

        public uint MiscFlag { get; private set; }

        public uint ArraySize { get; private set; }

        public uint MiscFlags2 { get; private set; }

        internal void Read(BinaryReader reader)
        {
            DxgiFormat = reader.ReadUInt32();
            ResourceDimension = reader.ReadUInt32();
            MiscFlag = reader.ReadUInt32();
            ArraySize = reader.ReadUInt32();
            MiscFlags2 = reader.ReadUInt32();
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(DxgiFormat);
            writer.Write(ResourceDimension);
            writer.Write(MiscFlag);
            writer.Write(ArraySize);
            writer.Write(MiscFlags2);
        }
    }
}
