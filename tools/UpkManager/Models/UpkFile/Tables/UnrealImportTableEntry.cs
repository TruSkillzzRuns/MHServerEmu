using System;
using System.IO;
using System.Threading.Tasks;

using UpkManager.Helpers;


#nullable enable

namespace UpkManager.Models.UpkFile.Tables
{

    public sealed class UnrealImportTableEntry : UnrealObjectTableEntryBase
    {

        #region Constructor

        public UnrealImportTableEntry()
        {
            PackageNameIndex = new FName();
            ClassNameIndex = new FName();
            ObjectNameIndex = new FObject(this);
        }

        #endregion Constructor

        #region Properties

        public FName PackageNameIndex { get; }

        public FName ClassNameIndex { get; }
        //
        // OwnerReference in ObjectTableEntryBase
        //
        // NameTableIndex in ObjectTableEntryBase
        //
        #endregion Properties

        #region Unreal Properties

        public FName? OuterReferenceNameIndex { get; set; }

        #endregion Unreal Properties

        #region Unreal Methods

        // Set OuterReference for synthetic imports (constructed in-memory
        // rather than read from disk). Phase 2 character swap uses this to
        // synthesize import entries for source-only material exports that
        // can't be safely transplanted as exports; the synthetic import's
        // outer points at a target package export by ref. The deserializer
        // path uses the protected setter directly; this public setter is
        // the only callable path for in-memory construction.
        public void SetOuterReferenceForSynthesis(int outerReference)
        {
            OuterReference = outerReference;
        }

        public Task ReadImportTableEntry(ByteArrayReader reader, UnrealHeader header)
        {
            UnrealHeader = header;
            PackageNameIndex.ReadNameTableIndex(reader, header); // PackageName
            ClassNameIndex.ReadNameTableIndex(reader, header);   // ClassName
            OuterReference = reader.ReadInt32();                 // OuterIndex
            ObjectNameIndex.ReadNameTableIndex(reader, header);  // ObjectName

            return Task.CompletedTask;
        }

        public void ExpandReferences()
        {
            OuterReferenceNameIndex = UnrealHeader.GetObjectTableEntry(OuterReference)?.ObjectNameIndex;
        }

        #endregion Unreal Methods

        #region UnrealUpkBuilderBase Implementation

        public override int GetBuilderSize()
        {
            BuilderSize = PackageNameIndex.GetBuilderSize()
                        + ClassNameIndex.GetBuilderSize()
                        + sizeof(int)
                        + ObjectNameIndex.GetBuilderSize();

            return BuilderSize;
        }

        public override async Task WriteBuffer(ByteArrayWriter Writer, int CurrentOffset)
        {
            await PackageNameIndex.WriteBuffer(Writer, 0);

            await ClassNameIndex.WriteBuffer(Writer, 0);

            Writer.WriteInt32(OuterReference);

            await ObjectNameIndex.WriteBuffer(Writer, 0);
        }

        #endregion UnrealUpkBuilderBase Implementation

        public override string GetPathName()
        {
            var outer = UnrealHeader.GetObjectTableEntry(OuterReference);
            if (outer != null)
                return outer.GetPathName() + "." + base.GetPathName();
            else
                return base.GetPathName();
        }

        public UnrealObjectTableEntryBase? GetExportEntry()
        {
            var pathName = GetPathName();
            var root = Path.GetDirectoryName(UnrealHeader.FullFilename) ?? string.Empty;
            return UnrealHeader.Repository.GetExportEntry(pathName, root);
        }
    }

}

#nullable restore
