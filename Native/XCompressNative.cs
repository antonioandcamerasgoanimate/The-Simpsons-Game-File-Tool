using System.Runtime.InteropServices;

namespace TSGFileTool.Native;

/// <summary>
/// Wraps the uploaded xcompress.dll. Its real exports (confirmed by reading
/// the DLL's export table) are:
///
///   XMemCreateDecompressionContext / XMemDestroyDecompressionContext
///   XMemResetDecompressionContext
///   XMemDecompress / XMemDecompressStream
///   XMemCreateCompressionContext / XMemDestroyCompressionContext
///   XMemResetCompressionContext / XMemGetCompressionContextSize
///   XMemCompress / XMemCompressStream / XMemGetDecompressionContextSize
///
/// IMPORTANT - this is Microsoft's XDK general-purpose LZX data compressor
/// (the same codec Xbox 360 uses to shrink STFS packages and streamed game
/// data). It is NOT an audio codec: it cannot turn XMA2 bitstreams into PCM.
/// If a .str/.snu/.mus payload is a raw LZX-compressed block (common for
/// streamed Xbox 360 resources), this class can decompress it. Actual XMA2
/// -> PCM decoding needs a real audio codec (e.g. ffmpeg's XMA2 decoder,
/// or vgmstream) - point one of those at the extracted stream instead.
///
/// Because xcompress.dll is a 32-bit PE, the whole application must run x86
/// (already set in the .csproj) or these calls will throw BadImageFormatException.
/// </summary>
public static class XCompressNative
{
    private const string Dll = "xcompress.dll";

    public enum XMemCodecType : uint
    {
        Default = 0,
        Lzx = 1,
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemCreateDecompressionContext(
        XMemCodecType codecType, IntPtr codecParams, uint flags, out IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemDestroyDecompressionContext(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemResetDecompressionContext(IntPtr context, IntPtr codecParams, uint flags);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemDecompress(
        IntPtr context,
        byte[] destination, ref uint destSize,
        byte[] source, ref uint sourceSize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemCreateCompressionContext(
        XMemCodecType codecType, IntPtr codecParams, uint flags, out IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemDestroyCompressionContext(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int XMemCompress(
        IntPtr context,
        byte[] destination, ref uint destSize,
        byte[] source, ref uint sourceSize);

    /// <summary>
    /// Attempts an LZX decompression of the given block. destSizeHint should
    /// be your best guess at the decompressed size (e.g. a value read from a
    /// neighbouring header field) - the buffer is grown and retried once if
    /// it turns out to be too small.
    /// </summary>
    public static byte[] TryDecompressLzx(byte[] source, uint destSizeHint)
    {
        IntPtr ctx = IntPtr.Zero;
        try
        {
            int hr = XMemCreateDecompressionContext(XMemCodecType.Lzx, IntPtr.Zero, 0, out ctx);
            if (hr != 0) throw new InvalidOperationException($"XMemCreateDecompressionContext failed (HRESULT 0x{hr:X8}).");

            uint destSize = Math.Max(destSizeHint, (uint)source.Length * 4);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var dest = new byte[destSize];
                uint srcSize = (uint)source.Length;
                uint outSize = destSize;
                hr = XMemDecompress(ctx, dest, ref outSize, source, ref srcSize);
                if (hr == 0)
                    return dest.AsSpan(0, (int)outSize).ToArray();
                destSize *= 4; // grow and retry - HRESULT for "buffer too small" varies by XDK version
            }
            throw new InvalidOperationException($"XMemDecompress failed (HRESULT 0x{hr:X8}).");
        }
        finally
        {
            if (ctx != IntPtr.Zero) XMemDestroyDecompressionContext(ctx);
        }
    }

    public static byte[] CompressLzx(byte[] source)
    {
        IntPtr ctx = IntPtr.Zero;
        try
        {
            int hr = XMemCreateCompressionContext(XMemCodecType.Lzx, IntPtr.Zero, 0, out ctx);
            if (hr != 0) throw new InvalidOperationException($"XMemCreateCompressionContext failed (HRESULT 0x{hr:X8}).");

            uint destSize = (uint)source.Length + 4096;
            var dest = new byte[destSize];
            uint srcSize = (uint)source.Length;
            uint outSize = destSize;
            hr = XMemCompress(ctx, dest, ref outSize, source, ref srcSize);
            if (hr != 0) throw new InvalidOperationException($"XMemCompress failed (HRESULT 0x{hr:X8}).");
            return dest.AsSpan(0, (int)outSize).ToArray();
        }
        finally
        {
            if (ctx != IntPtr.Zero) XMemDestroyCompressionContext(ctx);
        }
    }
}
