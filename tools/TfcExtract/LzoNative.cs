using System;
using System.Runtime.InteropServices;

namespace TfcExtract;

internal static class LzoNative
{
    private const string Dll = "lzo2_64.dll";

    [DllImport(Dll, EntryPoint = "__lzo_init_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int lzo_init(uint v, int s1, int s2, int s3, int s4, int s5,
                                       int s6, int s7, int s8, int s9);

    [DllImport(Dll, EntryPoint = "lzo1x_decompress_safe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int lzo1x_decompress_safe(byte[] src, int src_len,
                                                    byte[] dst, ref int dst_len,
                                                    IntPtr wrkmem);

    private static bool s_initialized;

    public static void EnsureInitialized()
    {
        if (s_initialized) return;
        int rc = lzo_init(1, -1, -1, -1, -1, -1, -1, -1, -1, -1);
        if (rc != 0)
            throw new InvalidOperationException($"lzo_init failed (rc={rc}).");
        s_initialized = true;
    }

    /// <summary>
    /// Decompress one LZO block. Throws on error or short read.
    /// </summary>
    public static byte[] Decompress(byte[] src, int srcLen, int dstLen)
    {
        EnsureInitialized();
        byte[] dst = new byte[dstLen];
        int outLen = dstLen;
        int rc = lzo1x_decompress_safe(src, srcLen, dst, ref outLen, IntPtr.Zero);
        if (rc != 0)
            throw new InvalidOperationException($"lzo1x_decompress_safe failed (rc={rc}).");
        if (outLen != dstLen)
            throw new InvalidOperationException(
                $"LZO produced {outLen} bytes, expected {dstLen}.");
        return dst;
    }
}
