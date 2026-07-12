using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Types;

namespace UpkManager.Models.UpkFile.Engine
{
    [UnrealClass("Font")]
    public class UFont : UObject
    {
        [PropertyField]
        public UArray<FFontCharacter> Characters { get; set; }

        [PropertyField]
        public UArray<UObject> Textures { get; set; } // UTexture2D

        [PropertyField]
        public int IsRemapped { get; set; }

        [PropertyField]
        public float EmScale { get; set; }

        [PropertyField]
        public float Ascent { get; set; }

        [PropertyField]
        public float Descent { get; set; }

        [PropertyField]
        public FFontImportOptionsData ImportOptions { get; set; }

        [StructField("UMap<Word, Word>")]
        public UMap<ushort, ushort> CharRemap { get; set; }

        public override void ReadBuffer(UBuffer buffer)
        {
            base.ReadBuffer(buffer);
            CharRemap = buffer.ReadMap(UBuffer.ReadUInt16, UBuffer.ReadUInt16);
        }
    }

    public enum EFontImportCharacterSet
    {
        FontICS_Default,
        FontICS_Ansi,
        FontICS_Symbol,
        FontICS_MAX
    }

    [UnrealStruct("FontImportOptionsData")]
    public class FFontImportOptionsData : IAtomicStruct
    {
        [StructField]
        public string FontName { get; set; }

        [StructField]
        public float Height { get; set; }

        [StructField]
        public bool bEnableAntialiasing { get; set; }

        [StructField]
        public bool bEnableBold { get; set; }

        [StructField]
        public bool bEnableItalic { get; set; }

        [StructField]
        public bool bEnableUnderline { get; set; }

        [StructField]
        public bool bAlphaOnly { get; set; }

        [StructField]
        public EFontImportCharacterSet CharacterSet { get; set; }

        [StructField]
        public string Chars { get; set; }

        [StructField]
        public string UnicodeRange { get; set; }

        [StructField]
        public string CharsFilePath { get; set; }

        [StructField]
        public string CharsFileWildcard { get; set; }

        [StructField]
        public bool bCreatePrintableOnly { get; set; }

        [StructField]
        public bool bIncludeASCIIRange { get; set; }

        [StructField]
        public FLinearColor ForegroundColor { get; set; }

        [StructField]
        public bool bEnableDropShadow { get; set; }

        [StructField]
        public int TexturePageWidth { get; set; }

        [StructField]
        public int TexturePageMaxHeight { get; set; }

        [StructField]
        public int XPadding { get; set; }

        [StructField]
        public int YPadding { get; set; }

        [StructField]
        public int ExtendBoxTop { get; set; }

        [StructField]
        public int ExtendBoxBottom { get; set; }

        [StructField]
        public int ExtendBoxRight { get; set; }

        [StructField]
        public int ExtendBoxLeft { get; set; }

        [StructField]
        public bool bEnableLegacyMode { get; set; }

        [StructField]
        public int Kerning { get; set; }

        [StructField]
        public bool bUseDistanceFieldAlpha { get; set; }

        [StructField]
        public int DistanceFieldScaleFactor { get; set; }

        [StructField]
        public float DistanceFieldScanRadiusScale { get; set; }

        public string Format => "";
    }


    [AtomicStruct("FontCharacter", true)]
    public class FFontCharacter : IAtomicStruct
    {
        [StructField]
        public int StartU { get; set; }

        [StructField]
        public int StartV { get; set; }

        [StructField]
        public int USize { get; set; }

        [StructField]
        public int VSize { get; set; }

        [StructField]
        public byte TextureIndex { get; set; }

        [StructField]
        public int VerticalOffset { get; set; }

        public string Format => $"[T:{TextureIndex}][{StartU}; {StartV}][{USize}; {VSize}][{VerticalOffset}]";

        public static FFontCharacter ReadData(UBuffer buffer)
        {
            var character = new FFontCharacter
            {
                StartU = buffer.ReadInt32(),
                StartV = buffer.ReadInt32(),
                USize = buffer.ReadInt32(),
                VSize = buffer.ReadInt32(),
                TextureIndex = buffer.ReadByte(),
                VerticalOffset = buffer.ReadInt32()
            };
            return character;
        }
    }
}
