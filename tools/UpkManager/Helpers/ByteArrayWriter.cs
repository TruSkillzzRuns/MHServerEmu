using System;
using System.Threading.Tasks;


namespace UpkManager.Helpers
{

    public class ByteArrayWriter
    {

        #region Private Fields

        private byte[] data;

        private int index;

        #endregion Private Fields

        #region Public Methods

        public byte[] GetBytes()
        {
            return data;
        }

        public int Index => index;

        public static ByteArrayWriter CreateNew(int Length)
        {
            ByteArrayWriter writer = new ByteArrayWriter();

            byte[] data = new byte[Length];

            writer.Initialize(data, 0);

            return writer;
        }

        public void Initialize(byte[] Data, int Index)
        {
            data = Data;

            if (Index < 0 || Index > data.Length) throw new ArgumentOutOfRangeException(nameof(Index), "Index value is outside the bounds of the byte array.");

            index = Index;
        }

        public void Seek(int Offset)
        {
            if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset), "Index value is outside the bounds of the byte array.");

            EnsureCapacity(Offset - index);

            index = Offset;
        }

        public void WriteByte(byte Value)
        {
            EnsureCapacity(sizeof(byte));
            data[index] = Value; ++index;
        }

        public void WriteInt16(short Value)
        {
            EnsureCapacity(sizeof(short));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(short)); index += sizeof(short);
        }

        public void WriteUInt16(ushort Value)
        {
            EnsureCapacity(sizeof(ushort));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(ushort)); index += sizeof(ushort);
        }

        public void WriteInt32(int Value)
        {
            EnsureCapacity(sizeof(int));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(int)); index += sizeof(int);
        }

        public void WriteUInt32(uint Value)
        {
            EnsureCapacity(sizeof(uint));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(uint)); index += sizeof(uint);
        }

        public void WriteInt64(long Value)
        {
            EnsureCapacity(sizeof(long));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(long)); index += sizeof(long);
        }

        public void WriteUInt64(ulong Value)
        {
            EnsureCapacity(sizeof(ulong));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(ulong)); index += sizeof(ulong);
        }

        public void WriteSingle(float Value)
        {
            EnsureCapacity(sizeof(float));
            byte[] bytes = BitConverter.GetBytes(Value);

            Array.ConstrainedCopy(bytes, 0, data, index, sizeof(float)); index += sizeof(float);
        }

        public async Task WriteBytes(byte[] Bytes)
        {
            if (Bytes == null || Bytes.Length == 0) return;

            EnsureCapacity(Bytes.Length);

            await Task.Run(() => Array.ConstrainedCopy(Bytes, 0, data, index, Bytes.Length));

            index += Bytes.Length;
        }

        private void EnsureCapacity(int requiredBytes)
        {
            if (requiredBytes <= 0) return;

            int requiredSize = index + requiredBytes;
            if (requiredSize <= data.Length) return;

            int newSize = data.Length == 0 ? requiredSize : data.Length;
            while (newSize < requiredSize)
                newSize = Math.Max(newSize * 2, requiredSize);

            Array.Resize(ref data, newSize);
        }

        #endregion Public Methods

    }

}
