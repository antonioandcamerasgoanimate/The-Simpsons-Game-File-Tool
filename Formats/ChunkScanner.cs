using System.Text;
using System.Collections.Generic;

namespace TSGFileTool.Formats;

public sealed class Chunk
{
    public string Tag { get; init; } = "????";
    public long Offset { get; init; }
    public int PayloadSize { get; init; }
    public long PayloadOffset => Offset + 8;
}

/// <summary>
/// EA's Xbox 360 movie files (.vp6) wrap a VP6 bitstream in a "SCHl" chunk
/// container: 4-byte ASCII tag + 4-byte little-endian payload size, repeated
/// (SCHl -> MVhd -> SCCl -> SCDl ... one SCDl per frame). This was confirmed
/// against ealogo_sd.vp6 byte-for-byte (SCHl@0 size 40, MVhd@40 size 32,
/// SCCl@72 size 12, SCDl@168 size 2036, ...). The scanner below walks
/// sequentially and, when a chunk's declared size doesn't lead to another
/// valid tag, resyncs by scanning forward for the next ASCII 4CC - this
/// makes it tolerant of chunk kinds/nesting this tool doesn't know about yet.
/// </summary>
public static class ChunkScanner
{
    public static List<Chunk> Scan(byte[] data)
    {
        var chunks = new List<Chunk>();
        long pos = 0;
        while (pos + 8 <= data.Length)
        {
            if (IsAsciiTag(data, pos))
            {
                string tag = Encoding.ASCII.GetString(data, (int)pos, 4);
                int size = BitConverter.ToInt32(data, (int)pos + 4);
                if (size >= 0 && pos + 8 + size <= data.Length)
                {
                    chunks.Add(new Chunk { Tag = tag, Offset = pos, PayloadSize = size });
                    long next = pos + 8 + size;
                    // chunks are 4-byte aligned in this container
                    next = (next + 3) & ~3L;
                    if (next > pos) { pos = next; continue; }
                }
            }
            pos += 4; // resync: keep looking for the next plausible tag
        }
        return chunks;
    }

    private static bool IsAsciiTag(byte[] data, long pos)
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = data[pos + i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    public static byte[] ExtractPayload(byte[] data, Chunk c)
        => data.AsSpan((int)c.PayloadOffset, c.PayloadSize).ToArray();
}
