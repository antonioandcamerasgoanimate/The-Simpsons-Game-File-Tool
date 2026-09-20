using System.IO.Compression;
using System.Collections.Generic;
using System.IO;

namespace TSGFileTool.Formats;

/// <summary>
/// Zlib decompression. .NET has had a real, standard-compliant zlib
/// implementation built in since .NET 6 (System.IO.Compression.ZLibStream),
/// so this wraps that directly rather than porting the zlib C sources in
/// Compression.zip (those were just the official prebuilt zlib binaries/
/// headers, not custom code - nothing to port, the framework's version is
/// the same well-tested codec).
///
/// TESTED AND RULED OUT for this game's EARS payloads: scanned the whole of
/// zone01_split4.itxd for a zlib header (0x78 followed by 0x01/0x5E/0x9C/
/// 0xDA, the standard CMF/FLG byte pairs) - zero matches anywhere in the
/// 71,610-byte file, not even a coincidental one. Kept here because it's a
/// real, useful codec for anything else in this project that turns out to
/// be genuinely zlib-compressed.
/// </summary>
public static class ZlibDecoder
{
    private static readonly (byte cmf, byte flg)[] ValidHeaders =
    {
        (0x78, 0x01), (0x78, 0x5E), (0x78, 0x9C), (0x78, 0xDA)
    };

    public static bool LooksLikeZlib(byte[] data, int offset = 0) =>
        offset + 1 < data.Length && ValidHeaders.Any(h => data[offset] == h.cmf && data[offset + 1] == h.flg);

    public static List<int> FindHeaders(byte[] data)
    {
        var hits = new List<int>();
        for (int i = 0; i < data.Length - 1; i++)
            if (LooksLikeZlib(data, i)) hits.Add(i);
        return hits;
    }

    public static byte[] Decompress(byte[] data, int offset = 0)
    {
        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
