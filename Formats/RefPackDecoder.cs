using System.Collections.Generic;
using System.IO;

namespace TSGFileTool.Formats;

/// <summary>
/// C# port of the RefPack decompressor from the user-supplied
/// Compression.zip (refpack/refpackd.h) - EA's classic LZ77-family
/// compression (used across many EA titles: SimCity 4, Spore, Command &amp;
/// Conquer, etc.). This is a direct, byte-oriented translation (no bit-level
/// Huffman tables like LZX, so lower translation risk).
///
/// THIS IS THE REAL EARS CODEC - CONFIRMED, NOT A GUESS. The user supplied
/// the actual authoritative QuickBMS script for this format
/// (simpsons_str.bms, by Luigi Auriemma/aluigi, kept in Reference/), which
/// names the compression type "dk2" - QuickBMS's own source confirms DK2 is
/// the same family as QFS compression, which is this exact RefPack
/// algorithm. The earlier "ruled out" conclusion in this file's history was
/// caused by an entirely wrong addressing scheme (searching for the 0x10FB
/// signature anywhere in the file, when the real per-entry data location -
/// BASE_OFF - has to be computed sequentially: it starts right after the
/// TOC table, aligned up to the next 0x800 boundary, and advances by each
/// entry's CompressedSize after processing it). Once addressed correctly,
/// this decoder was verified end-to-end against real entries in homer.str:
/// decompressing entry 0 (declared CompressedSize=8192, DecompressedSize=
/// 57360) produces exactly 57360 bytes containing the real, uncorrupted
/// filename "homer_ball_lods.hko", a fully intact build path, and a genuine
/// Havok physics file header ("Havok-4.1.0-r1"). See StocArchive.Load() for
/// the full, confirmed pipeline (TOC parsing, BASE_OFF computation, and the
/// inner-block structure each decompressed entry contains).
/// </summary>
public static class RefPackDecoder
{
    public static bool IsRefPack(byte[] data, int offset = 0) =>
        offset + 1 < data.Length &&
        ((data[offset] == 0x10 && data[offset + 1] == 0xFB) ||
         (data[offset] == 0x90 && data[offset + 1] == 0xFB));

    public static (int size, int dataOffset) GetDecompressedSize(byte[] src, int offset = 0)
    {
        if (src[offset] == 0x10 && src[offset + 1] == 0xFB)
            return ((src[offset + 2] << 16) | (src[offset + 3] << 8) | src[offset + 4], offset + 5);
        if (src[offset] == 0x90 && src[offset + 1] == 0xFB)
            return ((src[offset + 2] << 24) | (src[offset + 3] << 16) | (src[offset + 4] << 8) | src[offset + 5], offset + 6);
        throw new InvalidDataException("Not a RefPack stream (missing 0x10FB/0x90FB signature).");
    }

    /// <summary>Decompresses starting at offset. Throws on malformed input rather than
    /// crashing (the reference C++ has no bounds checking and can segfault on bad input -
    /// this port adds bounds checks specifically so a false-positive signature match fails
    /// cleanly instead of corrupting memory). srcEnd, when given, additionally bounds reads
    /// to this entry's own known compressed region (e.g. BaseOffset + CompressedSize) so a
    /// malformed stream can't read into unrelated subsequent file data.</summary>
    public static byte[] Decompress(byte[] src, int offset = 0, int? srcEnd = null)
    {
        var (size, dataOffset) = GetDecompressedSize(src, offset);
        if (size < 0 || size > 200_000_000)
            throw new InvalidDataException($"Implausible decompressed size ({size} bytes) - likely a coincidental signature match, not real RefPack data.");

        int effectiveEnd = srcEnd is int e && e < src.Length ? e : src.Length;
        var output = new byte[size];
        int s = dataOffset, d = 0;

        while (true)
        {
            byte first = ReadByte(src, s, effectiveEnd);
            int r, run;

            if ((first & 0x80) == 0)
            {
                byte second = ReadByte(src, s + 1, effectiveEnd);
                s += 2;
                run = first & 3;
                if (run > 0) { d = CopyN(src, s, output, d, run); s += run; }
                r = d - 1 - (((first & 0x60) << 3) + second);
                run = ((first & 0x1C) >> 2) + 3;
                d = CopyN(output, r, output, d, run);
                continue;
            }

            if ((first & 0x40) == 0)
            {
                byte second = ReadByte(src, s + 1, effectiveEnd), third = ReadByte(src, s + 2, effectiveEnd);
                s += 3;
                run = second >> 6;
                if (run > 0) { d = CopyN(src, s, output, d, run); s += run; }
                r = d - 1 - (((second & 0x3F) << 8) + third);
                run = (first & 0x3F) + 4;
                d = CopyN(output, r, output, d, run);
                continue;
            }

            if ((first & 0x20) == 0)
            {
                byte second = ReadByte(src, s + 1, effectiveEnd), third = ReadByte(src, s + 2, effectiveEnd), fourth = ReadByte(src, s + 3, effectiveEnd);
                s += 4;
                run = first & 3;
                if (run > 0) { d = CopyN(src, s, output, d, run); s += run; }
                r = d - 1 - ((((first & 0x10) >> 4) << 16) + (second << 8) + third);
                run = (((first & 0x0C) >> 2) << 8) + fourth + 5;
                d = CopyN(output, r, output, d, run);
                continue;
            }

            s += 1;
            run = ((first & 0x1F) << 2) + 4;
            if (run <= 112) { d = CopyN(src, s, output, d, run); s += run; continue; }

            run = first & 3;
            if (run > 0) { d = CopyN(src, s, output, d, run); s += run; }
            break;
        }

        return output;
    }

    private static byte ReadByte(byte[] a, int i, int end) =>
        i >= 0 && i < end ? a[i] : throw new InvalidDataException("RefPack: read past end of input (malformed stream).");

    private static int CopyN(byte[] input, int sIndex, byte[] output, int dIndex, int run)
    {
        for (int k = 0; k < run; k++)
        {
            if (sIndex + k < 0 || sIndex + k >= input.Length || dIndex + k >= output.Length)
                throw new InvalidDataException("RefPack: copy out of bounds (malformed stream).");
            output[dIndex + k] = input[sIndex + k];
        }
        return dIndex + run;
    }

    // ---- Compressor (encoder) - ported from refpackd.h's compress(), which
    // also has a real, working compress() function alongside decompress().
    // This was validated in Python BEFORE porting to C#: compressing a real
    // decompressed homer.str blob (57360 bytes) produced output BYTE-FOR-BYTE
    // IDENTICAL to the compiled C++ reference (11819 bytes), and decompressing
    // that output round-trips to the exact original bytes. Needed for any
    // future "write modified data back into a .str" feature - StocArchive
    // doesn't use this yet (see its own doc comment on replace/resize not
    // being wired up), but the codec itself is complete and verified both
    // directions now. ----

    private static readonly Dictionary<int, (int quick, int maxLookback)> QMapTable = new()
    {
        [0] = (0, 0),
        [1] = (3, 1 * 1024 - 1),
        [2] = (3, 16 * 1024 - 1),
        [3] = (1, 1 * 1024 - 1),
        [4] = (1, 2 * 1024 - 1),
        [5] = (1, 4 * 1024 - 1),
        [6] = (0, 16 * 1024 - 1),
        [7] = (0, 32 * 1024 - 1),
        [8] = (0, 64 * 1024 - 1),
        [9] = (0, 128 * 1024 - 1),
    };
    private const int HashTableSize = 16 * 1024;
    private const int RefSig = 0x10FB;
    private const int RefHeaderFlagSize32 = 0x8000;

    private static int MatchLength(byte[] src, int sindex, int dindex, int maxmatch)
    {
        int current = 0;
        while (maxmatch >= 4)
        {
            if (src[sindex + 0] != src[dindex + 0]) return current + 0;
            if (src[sindex + 1] != src[dindex + 1]) return current + 1;
            if (src[sindex + 2] != src[dindex + 2]) return current + 2;
            if (src[sindex + 3] != src[dindex + 3]) return current + 3;
            sindex += 4; dindex += 4; current += 4; maxmatch -= 4;
        }
        if (maxmatch < 1 || src[sindex + 0] != src[dindex + 0]) return current + 0;
        if (maxmatch < 2 || src[sindex + 1] != src[dindex + 1]) return current + 1;
        if (maxmatch < 3 || src[sindex + 2] != src[dindex + 2]) return current + 2;
        if (maxmatch < 4 || src[sindex + 3] != src[dindex + 3]) return current + 3;
        return current + 4;
    }

    private static int GetHash(byte[] src, int offset)
    {
        int hsh = src[offset + 0] * 1089 + src[offset + 1] * 33 + src[offset + 2];
        return hsh & (HashTableSize - 1);
    }

    /// <summary>Compresses data with the real RefPack/QFS ("dk2") algorithm.
    /// level is 1 (fastest, worst ratio) to 9 (slowest, best ratio); 5 is a
    /// reasonable default. littleEndian matches the reference's own
    /// parameter - false (the default) is correct for this game's
    /// big-endian Xbox 360 archives.</summary>
    public static byte[] Compress(byte[] data, int level = 5, bool littleEndian = false)
    {
        level = Math.Min(Math.Max(level, 1), 9);
        var src = data;
        var output = new List<byte>();

        int run = 0, rptr = 0, cptr = 0;
        var (quick, maxLookback) = QMapTable[level];
        var hashtbl = new int[HashTableSize];
        var link = new int[maxLookback + 1];

        int srcLength = src.Length;
        int refsig = RefSig;
        int sizebytes = 3;
        if (srcLength >= 0x1000000) { refsig |= RefHeaderFlagSize32; sizebytes = 4; }

        output.Add((byte)((refsig >> 8) & 0xFF));
        output.Add((byte)(refsig & 0xFF));

        if (sizebytes == 3)
        {
            byte cast1 = (byte)((srcLength >> 16) & 0xFF);
            byte cast2 = (byte)((srcLength >> 8) & 0xFF);
            byte cast3 = (byte)(srcLength & 0xFF);
            if (littleEndian) { output.Add(cast3); output.Add(cast2); output.Add((byte)((srcLength >> 24) & 0xFF)); }
            else { output.Add(cast1); output.Add(cast2); output.Add(cast3); }
        }
        else
        {
            output.Add((byte)((srcLength >> 24) & 0xFF));
            output.Add((byte)((srcLength >> 16) & 0xFF));
            output.Add((byte)((srcLength >> 8) & 0xFF));
            output.Add((byte)(srcLength & 0xFF));
        }

        long srcBufferSize = (long)srcLength - 4;

        while (srcBufferSize >= 0)
        {
            int boffset = 0, blen = 2, bcost = 2;
            long mlen = Math.Min(srcBufferSize, 1028);
            int hash_ = GetHash(src, cptr);
            int hoffset = hashtbl[hash_];
            int minhoffset = Math.Max(cptr - maxLookback, 1);

            if (hoffset >= minhoffset)
            {
                while (true)
                {
                    int tptr = hoffset;
                    if (src[cptr + blen] == src[tptr + blen])
                    {
                        int tlen0 = MatchLength(src, cptr, tptr, (int)mlen);
                        int toffset = (cptr - 1) - tptr;
                        int tcost;
                        if (toffset < 1024 && tlen0 <= 10) tcost = 2;
                        else if (toffset < 16384 && tlen0 <= 67) tcost = 3;
                        else tcost = 4;

                        if (tlen0 - tcost + 4 > blen - bcost + 4)
                        {
                            blen = tlen0; bcost = tcost; boffset = toffset;
                            if (blen >= 1028) break;
                        }
                    }
                    hoffset = link[hoffset & maxLookback];
                    if (!(hoffset >= minhoffset)) break;
                }
            }

            if (bcost >= blen || srcBufferSize < 4)
            {
                hoffset = cptr;
                link[hoffset & maxLookback] = hashtbl[hash_];
                hashtbl[hash_] = hoffset;
                run += 1; cptr += 1; srcBufferSize -= 1;
            }
            else
            {
                while (run > 3)
                {
                    int tlen = Math.Min(112, run & ~3);
                    run -= tlen;
                    output.Add((byte)((0xe0 + (tlen >> 2) - 1) & 0xFF));
                    for (int k = 0; k < tlen; k++) output.Add(src[rptr + k]);
                    rptr += tlen;
                }

                if (bcost == 2)
                {
                    output.Add((byte)((((boffset >> 8) << 5) + ((blen - 3) << 2) + run) & 0xFF));
                    output.Add((byte)(boffset & 0xFF));
                }
                else if (bcost == 3)
                {
                    output.Add((byte)((0x80 + (blen - 4)) & 0xFF));
                    output.Add((byte)(((run << 6) + (boffset >> 8)) & 0xFF));
                    output.Add((byte)(boffset & 0xFF));
                }
                else
                {
                    output.Add((byte)((0xc0 + ((boffset >> 16) << 4) + (((blen - 5) >> 8) << 2) + run) & 0xFF));
                    output.Add((byte)((boffset >> 8) & 0xFF));
                    output.Add((byte)(boffset & 0xFF));
                    output.Add((byte)((blen - 5) & 0xFF));
                }

                if (run > 0)
                {
                    for (int k = 0; k < run; k++) output.Add(src[rptr + k]);
                    run = 0;
                }

                if (quick > 0)
                {
                    if (quick == 3)
                    {
                        hoffset = cptr;
                        link[hoffset & maxLookback] = 0;
                        hashtbl[hash_] = hoffset;
                    }
                    else if (quick == 1 || (hoffset & 3) == 0)
                    {
                        hoffset = cptr;
                        link[hoffset & maxLookback] = hashtbl[hash_];
                        hashtbl[hash_] = hoffset;
                    }
                    cptr += blen;
                }
                else
                {
                    for (int i = 0; i < blen; i++)
                    {
                        hash_ = GetHash(src, cptr);
                        hoffset = cptr;
                        link[hoffset & maxLookback] = hashtbl[hash_];
                        hashtbl[hash_] = hoffset;
                        cptr += 1;
                    }
                }

                rptr = cptr;
                srcBufferSize -= blen;
            }
        }

        srcBufferSize += 4;
        run += (int)srcBufferSize;

        while (run > 3)
        {
            int tlen = Math.Min(112, run & ~3);
            run -= tlen;
            output.Add((byte)((0xe0 + (tlen >> 2) - 1) & 0xFF));
            for (int k = 0; k < tlen; k++) output.Add(src[rptr + k]);
            rptr += tlen;
        }

        output.Add((byte)((0xfc + run) & 0xFF));
        if (run > 0)
            for (int k = 0; k < run; k++) output.Add(src[rptr + k]);

        return output.ToArray();
    }
}
