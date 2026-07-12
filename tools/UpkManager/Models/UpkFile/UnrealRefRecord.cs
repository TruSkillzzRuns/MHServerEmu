namespace UpkManager.Models.UpkFile
{
    public enum UnrealRefKind
    {
        // 8-byte FName: int32 Index + int32 Numeric (only Index needs
        // translation; Numeric is just an array-style suffix).
        Name,
        // 4-byte FObject: int32 ref (positive = export idx+1, negative =
        // import idx-1, zero = null).
        Object,
    }

    public readonly record struct UnrealRefRecord(int Offset, UnrealRefKind Kind, int RawValue);
}
