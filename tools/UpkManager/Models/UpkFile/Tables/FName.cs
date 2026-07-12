using System;
using System.Threading.Tasks;

using UpkManager.Helpers;


namespace UpkManager.Models.UpkFile.Tables
{
    public class FName : UnrealUpkBuilderBase
    {

        #region Properties

        public int Index { get; private set; }

        public int Numeric { get; private set; }

        #endregion Properties

        #region Unreal Properties

        public string Name { get; set; }

        #endregion Unreal Properties

        #region Public Methods

        public override string ToString() => Name;

        public void SetNameTableIndex(UnrealNameTableEntry nameTableEntry, int numeric = 0)
        {
            Index = nameTableEntry.TableIndex;

            Numeric = numeric;

            Name = nameTableEntry.Name.String;
        }

        public void ReadNameTableIndex(ByteArrayReader reader, UnrealHeader header)
        {
            int idxOffset = reader.CurrentOffset;
            Index = reader.ReadInt32();
            Numeric = reader.ReadInt32();

            if (Index < 0 || Index > header.NameTable.Count) throw new ArgumentOutOfRangeException(nameof(Index), $"Index ({Index:X8}) is out of range of the NameTable size.");

            Name = Numeric > 0 ? $"{header.NameTable[Index].Name.String}_{Numeric - 1}" : $"{header.NameTable[Index].Name.String}";

            // When a recorder is attached on the header, capture the byte
            // offset of every FName index read so a downstream rewriter can
            // translate source indices into target indices in place. Cheap
            // when null; only used by Character Swap's mesh re-serializer.
            header.RefRecorder?.Add(new UnrealRefRecord(idxOffset, UnrealRefKind.Name, Index));
        }

        #endregion Public Methods

        #region UnrealUpkBuilderBase Implementation

        public override int GetBuilderSize()
        {
            BuilderSize = sizeof(int) * 2;

            return BuilderSize;
        }

        public override async Task WriteBuffer(ByteArrayWriter Writer, int CurrentOffset)
        {
            await Task.Run(() =>
            {
                Writer.WriteInt32(Index);

                Writer.WriteInt32(Numeric);
            });
        }

        public bool IsNone()
        {
            return Name?.ToLower() == "none";
        }

        public bool IsNotBool()
        {
            return Name?.ToLower() != "boolproperty";
        }

        #endregion UnrealUpkBuilderBase Implementation

    }

}
