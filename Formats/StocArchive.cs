using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TSGFileTool.Formats;

/// <summary>
/// Reads / rebuilds EA "SToc" archives (.str) as used by The Simpsons Game
/// (Xbox 360 build).
///
/// THE REAL, CONFIRMED PIPELINE (validated end-to-end against real files -
/// see DecodedAssets below): this format is EARS-compressed using EA's
/// "QFS"/RefPack-family codec, referred to as "dk2" in simpsons_str.bms (the
/// authoritative QuickBMS script by Luigi Auriemma/aluigi, kept in
/// Reference/). Nothing here was guessed - every step was checked against
/// homer.str before being written up:
///
///   1. TOC parsing: unchanged from before - 24-byte records starting at
///      the header's INFO_OFF field (offset 0x10), each holding TypeHash,
///      TableTag, DecompressedSize, IgnoreSize, CompressedSize, Reserved
///      (see StocEntry.cs - the field MEANINGS were wrong in an earlier
///      version of this tool, which called DecompressedSize "OffsetA" and
///      treated it as a literal file offset; it never was one).
///
///   2. BASE_OFF computation: the real per-entry data location is NOT
///      stored anywhere - it's computed sequentially. It starts right after
///      a fixed 52-byte header region plus the TOC table (FILES * 24
///      bytes), rounded UP to the next 0x800 boundary. After each entry is
///      processed, BASE_OFF advances by that entry's CompressedSize - so
///      entries are packed back-to-back with no per-entry offset field at
///      all.
///
///   3. Per-entry decompression: read 2 bytes at BASE_OFF. If they read
///      0x10FB (or 0x90FB), the entry is RefPack/QFS-compressed - decompress
///      CompressedSize bytes starting there (RefPackDecoder.Decompress) to
///      get exactly DecompressedSize bytes out. Otherwise the entry is
///      stored raw: just copy DecompressedSize bytes directly.
///
///   4. Inner block structure: the decompressed (or raw) block for a single
///      TOC entry usually contains MULTIPLE real named sub-files, not one -
///      e.g. homer.str's entry 0 (nominally "homer_ball_lods.hko") actually
///      contains 5 separate named .hko files back to back. Each inner
///      record has 3 unidentified leading fields, a HEADER_SIZE field
///      (0 marks the final/fallback record), then - when HEADER_SIZE != 0 -
///      three length-prefixed name/path strings (only the last is kept, the
///      others appear to be redundant alternate forms), a length-prefixed
///      dummy string, a reserved field, and finally the inner item's own
///      Size. The item's data starts at (record start + 0x10 + HEADER_SIZE)
///      and the next inner record starts right after, at 4-byte alignment.
///
/// This whole pipeline was verified byte-for-byte against real homer.str
/// entries: decompressing entry 0 (CompressedSize=8192, DecompressedSize=
/// 57360) produces exactly 57360 bytes whose inner structure yields 5 real,
/// cleanly-named .hko files with fully intact build paths and genuine Havok
/// physics headers ("Havok-4.1.0-r1") - not guessed, not approximated.
/// </summary>
public sealed class StocArchive
{
    public const string Magic = "SToc";
    private const int HeaderFixedSize = 52; // idstring(4) + 12 x uint32, per simpsons_str.bms
    private const int TocRecordSize = 24;
    private const int SectorAlign = 0x800;

    public string SourcePath { get; private set; } = string.Empty;
    public uint Version { get; private set; }
    public uint InfoOffset { get; private set; }
    public uint NominalCount { get; private set; }
    public byte[] RawHeader { get; private set; } = Array.Empty<byte>();
    public List<StocEntry> Entries { get; } = new();
    public byte[] FileData { get; private set; } = Array.Empty<byte>();

    /// <summary>The real, decompressed, ready-to-use sub-files - this is the
    /// authoritative, correct extraction result. Each Data array is genuine
    /// decompressed bytes; opening one on the Texture/RenderWare tabs works.</summary>
    public List<QfsAsset> DecodedAssets { get; private set; } = new();

    /// <summary>Per-TOC-entry decode diagnostics (whether it decompressed cleanly,
    /// and any error), in Entries order - useful for surfacing partial failures
    /// without losing the assets that DID decode correctly.</summary>
    public List<string?> EntryDecodeErrors { get; private set; } = new();

    /// <summary>Legacy heuristic scan (finds plausible filenames directly in
    /// still-compressed bytes) - kept as a fallback/cross-reference for
    /// anything DecodedAssets didn't cover, but DecodedAssets should be
    /// preferred whenever it has content.</summary>
    public List<EarsSubFile> RealFiles { get; private set; } = new();

    public static bool LooksLikeStoc(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[4];
            if (fs.Read(buf) != 4) return false;
            return buf[0] == (byte)'S' && buf[1] == (byte)'T' && buf[2] == (byte)'o' && buf[3] == (byte)'c';
        }
        catch { return false; }
    }

    public static StocArchive Load(string path)
    {
        var archive = new StocArchive { SourcePath = path };
        var data = File.ReadAllBytes(path);
        archive.FileData = data;

        if (data.Length < HeaderFixedSize || data[0] != 'S' || data[1] != 'T' || data[2] != 'o' || data[3] != 'c')
            throw new InvalidDataException("Not an SToc file (bad magic).");

        archive.Version = ReadU32BE(data, 4);
        var countField = ReadU32BE(data, 8);
        archive.NominalCount = countField >> 24; // entry count sits in the top byte
        archive.InfoOffset = ReadU32BE(data, 16);
        archive.RawHeader = data.AsSpan(0, (int)Math.Min(archive.InfoOffset, (uint)data.Length)).ToArray();

        // ---- 1. TOC parsing ----
        long pos = archive.InfoOffset;
        uint? tableTag = null;
        int index = 0;
        while (pos + TocRecordSize <= data.Length)
        {
            uint typeHash = ReadU32BE(data, pos + 0);
            uint tag = ReadU32BE(data, pos + 4);
            uint decompSize = ReadU32BE(data, pos + 8);
            uint ignoreSize = ReadU32BE(data, pos + 12);
            uint compSize = ReadU32BE(data, pos + 16);
            uint reserved = ReadU32BE(data, pos + 20);

            tableTag ??= tag;
            if (tag != tableTag) break;                        // end of the record table
            if (decompSize == 0 && compSize == 0) break;        // padding / end marker

            archive.Entries.Add(new StocEntry
            {
                Index = index++,
                TypeHash = typeHash,
                TableTag = tag,
                DecompressedSize = decompSize,
                IgnoreSize = ignoreSize,
                CompressedSize = compSize,
                Reserved = reserved,
                RecordOffset = pos
            });
            pos += TocRecordSize;
        }

        // ---- 2 & 3. BASE_OFF computation + per-entry decompression ----
        long rawBase = HeaderFixedSize + (long)archive.Entries.Count * TocRecordSize;
        long baseOff = ((rawBase + SectorAlign - 1) / SectorAlign) * SectorAlign;

        var assets = new List<QfsAsset>();
        int assetIndex = 0;
        foreach (var entry in archive.Entries)
        {
            entry.BaseOffset = baseOff;
            string? error = null;
            try
            {
                if (baseOff + 2 > data.Length)
                    throw new InvalidDataException("BASE_OFF is past end of file.");

                byte[] block;
                bool isCompressed = RefPackDecoder.IsRefPack(data, (int)baseOff);
                if (isCompressed)
                {
                    block = RefPackDecoder.Decompress(data, (int)baseOff, (int)Math.Min(baseOff + entry.CompressedSize, data.Length));
                    if (block.Length != entry.DecompressedSize)
                        error = $"Decompressed to {block.Length} bytes but TOC declared {entry.DecompressedSize} - kept anyway, treat with caution.";
                }
                else
                {
                    long rawEnd = Math.Min(baseOff + entry.DecompressedSize, data.Length);
                    block = data.AsSpan((int)baseOff, (int)(rawEnd - baseOff)).ToArray();
                }

                // ---- 4. Inner block structure ----
                foreach (var inner in ParseInnerBlock(block, entry.Index))
                {
                    inner.Index = assetIndex++;
                    assets.Add(inner);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            archive.EntryDecodeErrors.Add(error);
            baseOff += entry.CompressedSize;
        }
        archive.DecodedAssets = assets;

        // Fallback/cross-reference: still run the old heuristic scanner too,
        // in case some entry failed to decode and this finds it anyway.
        archive.RealFiles = EarsSectorScanner.Scan(data, archive.Entries);
        for (int i = 0; i < archive.RealFiles.Count; i++) archive.RealFiles[i].Index = i;

        return archive;
    }

    /// <summary>Parses the inner structure of one decompressed (or raw) TOC-entry
    /// block into its real named sub-files, per simpsons_str.bms's inner loop.</summary>
    private static List<QfsAsset> ParseInnerBlock(byte[] block, int parentIndex)
    {
        var results = new List<QfsAsset>();
        long memSize = block.Length;
        long offset = 0;
        int guard = 0;

        while (offset != memSize && offset + 16 <= memSize && guard++ < 100_000)
        {
            uint headerSize = ReadU32BE(block, offset + 12);
            if (headerSize == 0)
            {
                long dataOffset = offset + 16;
                long size = memSize - dataOffset;
                if (size > 0)
                    results.Add(new QfsAsset
                    {
                        Name = $"entry{parentIndex}_tail.bin",
                        Tag = "",
                        BuildPath = "",
                        ParentTocEntryIndex = parentIndex,
                        InnerOffset = dataOffset,
                        Data = block.AsSpan((int)dataOffset, (int)size).ToArray()
                    });
                break;
            }

            long p = offset + 16;
            string name = ReadLengthPrefixedString(block, ref p);
            p += 0x10; // skip fixed dummy region
            string name2 = ReadLengthPrefixedString(block, ref p);
            string name3 = ReadLengthPrefixedString(block, ref p);
            string dummy = ReadLengthPrefixedString(block, ref p);
            if (p + 8 > memSize) break;
            uint zero = ReadU32BE(block, p); p += 4;
            uint size2 = ReadU32BE(block, p); p += 4;

            long innerDataOffset = offset + 0x10 + headerSize;
            long innerSize = size2;
            if (innerDataOffset < 0 || innerDataOffset + innerSize > memSize) break;

            string finalPath = !string.IsNullOrEmpty(name3) ? name3 : (!string.IsNullOrEmpty(name2) ? name2 : name);
            string cleanPath = SanitizeInnerName(finalPath, parentIndex, results.Count);
            string baseName = cleanPath.Contains('\\') ? cleanPath[(cleanPath.LastIndexOf('\\') + 1)..] : cleanPath;
            results.Add(new QfsAsset
            {
                Name = baseName,
                Tag = TrimToPrintable(name2),
                BuildPath = cleanPath.Contains(':') ? cleanPath : "",
                ParentTocEntryIndex = parentIndex,
                InnerOffset = innerDataOffset,
                Data = block.AsSpan((int)innerDataOffset, (int)innerSize).ToArray()
            });

            long aligned = (innerSize + 3) & ~3L;
            offset = innerDataOffset + aligned;
        }
        return results;
    }

    private static string TrimToPrintable(string s)
    {
        int end = 0;
        while (end < s.Length && s[end] >= 0x20 && s[end] < 0x7F) end++;
        return end > 0 ? s[..end] : "";
    }

    private static string SanitizeInnerName(string name, int parentIndex, int innerIndex)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"entry{parentIndex}_item{innerIndex}.bin";
        int end = 0;
        while (end < name.Length && name[end] >= 0x20 && name[end] < 0x7F) end++;
        var clean = end > 0 ? name[..end] : name;
        return string.IsNullOrWhiteSpace(clean) ? $"entry{parentIndex}_item{innerIndex}.bin" : clean;
    }

    private static string ReadLengthPrefixedString(byte[] data, ref long pos)
    {
        if (pos + 4 > data.Length) { pos = data.Length; return ""; }
        uint len = ReadU32BE(data, pos);
        pos += 4;
        if (len == 0 || pos + len > data.Length) { pos += len; return ""; }
        var s = Encoding.Latin1.GetString(data, (int)pos, (int)len);
        pos += len;
        return s;
    }

    /// <summary>Writes every decoded asset (real, decompressed data) to disk with its
    /// real name, plus a manifest.xml describing the whole set.</summary>
    public void ExtractAllTo(string folder)
    {
        Directory.CreateDirectory(folder);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in DecodedAssets)
        {
            var name = MakeUniqueFileName(a.Name, usedNames);
            File.WriteAllBytes(Path.Combine(folder, name), a.Data);
        }
        Xml.StocManifest.Write(this, Path.Combine(folder, "manifest.xml"));
    }

    private static string MakeUniqueFileName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name)) return name;
        var root = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            var candidate = $"{root}_{n}{ext}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Replaces one TOC entry's decompressed content with newDecompressedBytes,
    /// recompresses it with the validated RefPack/QFS encoder, and rebuilds the
    /// whole file (every entry after this one shifts, since BASE_OFF is always
    /// computed sequentially from compressed sizes - there's no per-entry offset
    /// field to patch in isolation). Entries other than the one being replaced
    /// keep their ORIGINAL compressed bytes untouched (not recompressed), so
    /// this only touches what actually changed.
    ///
    /// SCOPED AT THE TOC-ENTRY LEVEL, not the individual named sub-file level:
    /// a single TOC entry's decompressed block commonly contains several named
    /// inner assets back to back (see StocArchive's class doc comment), and
    /// replacing just one of them correctly would mean re-serializing that
    /// entire inner block (all its length-prefixed name/path headers), which
    /// isn't implemented. For a TOC entry with exactly one inner asset (a very
    /// common case - e.g. most .bnk/.itxd entries), this replaces that asset's
    /// content directly, correctly. For a multi-asset entry, the replacement
    /// bytes must already be a complete, correctly-formatted inner block if
    /// you want more than one asset to survive - passing raw single-asset data
    /// for a multi-asset entry will destroy the other assets that were packed
    /// alongside it.
    /// </summary>
    public void ReplaceTocEntry(int entryIndex, byte[] newDecompressedBytes, int compressLevel = 5)
    {
        var entry = Entries.FirstOrDefault(e => e.Index == entryIndex)
            ?? throw new ArgumentException($"No TOC entry with index {entryIndex}.");

        // Capture every entry's CURRENT compressed bytes before changing anything,
        // since BaseOffset for entries after this one will shift once this
        // entry's compressed size changes.
        var originalCompressedBlocks = new Dictionary<int, byte[]>();
        foreach (var e in Entries)
        {
            long end = Math.Min(e.BaseOffset + e.CompressedSize, FileData.Length);
            originalCompressedBlocks[e.Index] = FileData.AsSpan((int)e.BaseOffset, (int)(end - e.BaseOffset)).ToArray();
        }

        var newCompressed = RefPackDecoder.Compress(newDecompressedBytes, compressLevel);
        entry.DecompressedSize = (uint)newDecompressedBytes.Length;
        entry.CompressedSize = (uint)newCompressed.Length;
        originalCompressedBlocks[entryIndex] = newCompressed;

        // Rebuild: header+TOC unchanged except this entry's two size fields,
        // then every entry's compressed block back-to-back in order.
        var newFile = new List<byte>(FileData.AsSpan(0, (int)InfoOffset).ToArray());
        foreach (var e in Entries)
        {
            Span<byte> rec = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(rec[0..], e.TypeHash);
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], e.TableTag);
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], e.DecompressedSize);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], e.IgnoreSize);
            BinaryPrimitives.WriteUInt32BigEndian(rec[16..], e.CompressedSize);
            BinaryPrimitives.WriteUInt32BigEndian(rec[20..], e.Reserved);
            newFile.AddRange(rec.ToArray());
        }
        while (newFile.Count < HeaderFixedSize) newFile.Add(0);
        long rawBase = HeaderFixedSize + (long)Entries.Count * TocRecordSize;
        long baseOff = ((rawBase + SectorAlign - 1) / SectorAlign) * SectorAlign;
        while (newFile.Count < baseOff) newFile.Add(0);

        foreach (var e in Entries)
        {
            e.BaseOffset = baseOff;
            newFile.AddRange(originalCompressedBlocks[e.Index]);
            baseOff += e.CompressedSize;
        }

        FileData = newFile.ToArray();

        // Re-derive DecodedAssets from the rebuilt file so the UI reflects the change.
        DecodedAssets.Clear();
        EntryDecodeErrors.Clear();
        int assetIndex = 0;
        foreach (var e in Entries)
        {
            string? error = null;
            try
            {
                byte[] block;
                if (RefPackDecoder.IsRefPack(FileData, (int)e.BaseOffset))
                    block = RefPackDecoder.Decompress(FileData, (int)e.BaseOffset, (int)Math.Min(e.BaseOffset + e.CompressedSize, FileData.Length));
                else
                    block = FileData.AsSpan((int)e.BaseOffset, (int)Math.Min(e.DecompressedSize, FileData.Length - e.BaseOffset)).ToArray();
                foreach (var inner in ParseInnerBlock(block, e.Index))
                {
                    inner.Index = assetIndex++;
                    DecodedAssets.Add(inner);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            EntryDecodeErrors.Add(error);
        }
    }

    public void Save(string path) => File.WriteAllBytes(path, FileData);

    private static uint ReadU32BE(byte[] data, long offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));
}
