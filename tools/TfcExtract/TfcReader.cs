using System.IO;

namespace TfcExtract;

internal static class TfcReader
{
    public static string ResolveTfcPath(string dir, string tfcName)
    {
        string n = (tfcName ?? string.Empty).Trim();
        if (!n.EndsWith(".tfc", System.StringComparison.OrdinalIgnoreCase))
            n += ".tfc";
        return Path.Combine(dir, n);
    }

    /// <summary>Read (offset, size) raw compressed chunk header+payload from a .tfc.</summary>
    public static byte[] ReadChunk(string tfcPath, uint offset, uint size)
    {
        using var fs = File.Open(tfcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset + size > fs.Length)
            throw new EndOfStreamException(
                $"Chunk [{offset:X}+{size:X}] exceeds .tfc length {fs.Length:X} ({tfcPath}).");
        fs.Seek(offset, SeekOrigin.Begin);
        byte[] buf = new byte[size];
        int read = fs.Read(buf, 0, (int)size);
        if (read != size)
            throw new EndOfStreamException(
                $"Short read on {tfcPath} at {offset:X}: wanted {size}, got {read}.");
        return buf;
    }
}
