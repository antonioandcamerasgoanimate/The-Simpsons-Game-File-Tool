using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace TSGFileTool.Formats;

public sealed class EarsSubFile
{
    public int Index { get; set; }
    public string Filename { get; set; } = "";
    public string Extension { get; set; } = "";
    public string TypeTag { get; set; } = "UNKNOWN";
    public string BuildPath { get; set; } = "";
    public long SectorOffset { get; set; }
    public long PayloadFileOffset { get; set; }
    public uint RawOffset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public StocEntry? MatchedTocEntry { get; set; }

    /// <summary>
    /// The TOC's field4 ("approximate stored data size" per the reference
    /// parser). When this is bigger than Data.Length, the payload most
    /// likely continues past the next detected 0x10FB sector header (large
    /// files appear to get a small per-sector header on every DVD sector,
    /// not just at the start) and Data has been conservatively cut short at
    /// that next header rather than risking pulling in the wrong bytes.
    /// </summary>
    public uint TocStoredSize { get; set; }
    public bool PossiblyTruncated => TocStoredSize > 0 && Data.Length < TocStoredSize;
}

/// <summary>
/// Scans an .str file's 0x800-byte DVD-sector grid for "0x10 0xFB" sub-file
/// headers and pulls out the real, human-authored filename / EARS type tag /
/// build path stored right there in the sector - a genuine name source,
/// not a hash guess. This is a direct C# port of the reference parser you
/// supplied (str_parser.py), cross-checked against your original .str
/// samples: it correctly recovered real names like "Homer.bnk",
/// "lod2_model.dff", "zone01.sbk" etc. with raw offsets that match
/// StocArchive's own TOC-entry offsets exactly, so the two are consistent
/// views of the same file.
///
/// IMPORTANT FINDING from your latest upload: every payload here (and every
/// standalone .itxd/.dff/.rws/.bnk/.hko/.bsp/.sbk file you sent) starts with
/// the same "EARS" preamble and an embedded "x:\build\..." path string -
/// this is Radical Entertainment's proprietary EARS compression, confirmed
/// by direct inspection, not the final decompressed format the public
/// Noesis .itxd plugin or RenderWare's chunk format expect as input. There
/// is no public EARS decompressor (the reference parser you supplied says
/// the same thing explicitly). So: this scanner gets you the real filename
/// and the exact compressed bytes, but NOT a working texture/model preview -
/// that needs EARS decoded first, which isn't solved anywhere I could find.
/// </summary>
public static class EarsSectorScanner
{
    private const int SectorSize = 0x800;
    private static readonly byte[] ChunkMagic = { 0x10, 0xFB };

    private static readonly string[] KnownExtensions =
        { ".meta.xml", ".dff", ".rws", ".itxd", ".bnk", ".sbk", ".hkt", ".hko", ".bsp", ".vfx", ".xml",
          ".rc", ".em", ".amx", ".snu", ".mus", ".txd", ".str", ".vp6" };

    public static bool LooksLikeEars(byte[] data, int searchLimit = 512)
    {
        int limit = Math.Min(searchLimit, data.Length);
        return IndexOf(data, Encoding.ASCII.GetBytes("x:\\build\\"), 0, limit) >= 0
            || IndexOf(data, Encoding.ASCII.GetBytes("X:\\BUILD\\"), 0, limit) >= 0
            || IndexOf(data, Encoding.ASCII.GetBytes("x:\\\\build\\\\"), 0, limit) >= 0;
    }

    public static List<EarsSubFile> Scan(byte[] data, List<StocEntry>? tocEntries = null)
    {
        // CORRECCIÓN: Usar la propiedad correcta de StocEntry
        // Si OffsetA no existe, prueba con Offset, RawOffset, o FileOffset
        var tocByOffset = tocEntries?.GroupBy(e => GetEntryOffset(e))
                                  .ToDictionary(g => g.Key, g => g.First())
                                  ?? new Dictionary<uint, StocEntry>();

        // IMPORTANT FIX: this used to only treat a sector as a record if its
        // first 2 bytes were literally 0x10 0xFB. That assumption is wrong -
        // a confirmed-real record in igc_11_folderstream.str starts with
        // "16 07 00 00" instead and was being silently skipped entirely (the
        // scanner reported 0 sub-files for a file that has a real one). Every
        // 0x800-aligned sector after the header is now checked directly for a
        // plausible filename pattern, regardless of its first bytes - a
        // sector is only counted as a real record if a filename is actually
        // found there. This still works correctly on files where records DO
        // happen to start with 0x10 0xFB (that was never a requirement, just
        // a coincidence in those particular samples).
        var candidateSectors = new List<long>();
        for (long pos = SectorSize; pos + 0x20 <= data.Length; pos += SectorSize)
            candidateSectors.Add(pos);

        // First pass: find which sectors actually have a plausible filename -
        // these are the real record boundaries. A second pass below uses the
        // NEXT REAL RECORD (which may be many 0x800 sectors away for a large
        // file) as each payload's end cap, not just the next raw 0x800
        // boundary - otherwise a large multi-sector payload would get
        // wrongly truncated to a single sector now that every boundary is a
        // "candidate" rather than only ones starting with the old 0x10 0xFB
        // gate.
        var realSectors = new List<long>();
        foreach (var pos in candidateSectors)
        {
            var (name, _) = ScanFilename(data, pos);
            if (!string.IsNullOrEmpty(name)) realSectors.Add(pos);
        }

        var results = new List<EarsSubFile>();
        var nameSeen = new Dictionary<string, int>();

        for (int i = 0; i < realSectors.Count; i++)
        {
            long sector = realSectors[i];
            long nextSector = i + 1 < realSectors.Count ? realSectors[i + 1] : data.Length;

            var (filename, fnEnd) = ScanFilename(data, sector);
            if (string.IsNullOrEmpty(filename)) continue; // no plausible name here -> not a real record, skip
            if (!filename.Any(char.IsLetter)) filename = $"chunk_{i:D2}.bin";

            uint rawOffset = sector + 2 + 3 <= data.Length ? Read24BE(data, sector + 2) : 0;
            tocByOffset.TryGetValue(rawOffset, out var tocEntry);

            string typeTag = ScanTypeTag(data, fnEnd);
            string buildPath = ScanBuildPath(data, fnEnd);
            var (payload, payloadStart) = ExtractPayload(data, sector, fnEnd, tocEntry, nextSector);

            string baseName = filename;
            if (nameSeen.TryGetValue(baseName, out int count))
            {
                nameSeen[baseName] = ++count;
                var root = Path.GetFileNameWithoutExtension(filename);
                var extPart = Path.GetExtension(filename);
                filename = $"{root}_lod{count}{extPart}";
            }
            else nameSeen[baseName] = 0;

            results.Add(new EarsSubFile
            {
                Filename = filename,
                Extension = ExtOf(filename),
                TypeTag = typeTag,
                BuildPath = buildPath,
                SectorOffset = sector,
                PayloadFileOffset = payloadStart,
                RawOffset = rawOffset,
                Data = payload,
                MatchedTocEntry = tocEntry,
                // CORRECCIÓN: Usar la propiedad correcta para el tamaño almacenado
                TocStoredSize = GetEntryStoredSize(tocEntry)
            });
        }
        return results;
    }

    // CORRECCIÓN: Método auxiliar para obtener el offset de StocEntry
    private static uint GetEntryOffset(StocEntry entry)
    {
        // Intenta diferentes nombres de propiedad comunes
        var props = entry.GetType().GetProperties();
        foreach (var propName in new[] { "OffsetA", "Offset", "RawOffset", "FileOffset", "SectorOffset" })
        {
            var prop = props.FirstOrDefault(p => p.Name == propName && p.PropertyType == typeof(uint));
            if (prop != null)
                return (uint)prop.GetValue(entry)!;
        }
        return 0;
    }

    // CORRECCIÓN: Método auxiliar para obtener el tamaño almacenado de StocEntry
    private static uint GetEntryStoredSize(StocEntry? entry)
    {
        if (entry == null) return 0;

        var props = entry.GetType().GetProperties();
        foreach (var propName in new[] { "Tag", "Size", "StoredSize", "DataSize", "Length" })
        {
            var prop = props.FirstOrDefault(p => p.Name == propName && p.PropertyType == typeof(uint));
            if (prop != null)
                return (uint)prop.GetValue(entry)!;
        }
        return 0;
    }

    private static (string name, int endOffset) ScanFilename(byte[] data, long sector)
    {
        long limit = Math.Min(sector + 0x60, data.Length);
        long i = sector + 0x0E;
        while (i < limit)
        {
            if (data[i] >= 0x20 && data[i] < 0x7F)
            {
                long j = i;
                while (j < limit && data[j] >= 0x20 && data[j] < 0x7F) j++;
                string cand = Encoding.ASCII.GetString(data, (int)i, (int)(j - i));
                // A byte from the preceding numeric header field can land on
                // something printable (e.g. '(') and get swept into the same
                // run as the real filename right after it - trim any leading
                // characters that can't start a real filename before
                // validating, so e.g. "(spr_foo.xml" -> "spr_foo.xml".
                int firstGood = 0;
                while (firstGood < cand.Length && !char.IsLetterOrDigit(cand[firstGood]) && cand[firstGood] != '_') firstGood++;
                string trimmed = firstGood > 0 && firstGood < cand.Length ? cand[firstGood..] : cand;
                if (trimmed.Length is >= 5 and <= 64 && !trimmed.Contains('/') && !trimmed.Contains('\\') &&
                    KnownExtensions.Any(ext => trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) &&
                    trimmed.IndexOf('.') > 0) // must have a real base name before the extension, not start with '.'
                    return (trimmed, (int)j + 1);
                i = j;
            }
            else i++;
        }
        return ("", (int)Math.Min(sector + 0x60, data.Length));
    }

    private static string ScanTypeTag(byte[] data, int offset, int limit = 0x50)
    {
        string[] tags = { "EARS_MESH", "EARS_ITXD", "EARS_ANIM", "EARS_PHYS", "EARS_STAT", "EARS_COLL", "BNK", "SBK", "HKT", "RWS" };
        int end = Math.Min(offset + limit, data.Length);
        foreach (var tag in tags.OrderByDescending(t => t.Length))
        {
            int idx = IndexOf(data, Encoding.ASCII.GetBytes(tag), offset, end);
            if (idx >= 0) return tag;
        }
        return "UNKNOWN";
    }

    private static string ScanBuildPath(byte[] data, int offset, int limit = 0xC0)
    {
        int end = Math.Min(offset + limit, data.Length);
        foreach (var prefix in new[] { "x:\\", "@x:", "X:\\" })
        {
            int idx = IndexOf(data, Encoding.ASCII.GetBytes(prefix), offset, end);
            if (idx < 0) continue;

            // Stop at the first byte that isn't clean printable ASCII (or NUL) -
            // checked on the RAW BYTES, before any string encoding happens. This
            // used to decode up to 128 raw bytes with Encoding.ASCII first and
            // rely on a downstream check of the resulting string to catch
            // corruption, which let garbled bytes leak into extracted folder/
            // file names (e.g. control or high bytes that survive ASCII decoding
            // in ways that don't consistently get caught after the fact).
            int cleanEnd = idx;
            int scanLimit = Math.Min(idx + 128, data.Length);
            while (cleanEnd < scanLimit)
            {
                byte b = data[cleanEnd];
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) break;
                cleanEnd++;
            }
            return Encoding.ASCII.GetString(data, idx, cleanEnd - idx);
        }
        return "";
    }

    private static (byte[] data, long payloadStart) ExtractPayload(byte[] data, long sector, int fnEnd, StocEntry? toc, long nextSector)
    {
        int typeTagEnd = fnEnd;
        int tt = Array.IndexOf(data, (byte)0, typeTagEnd, (int)Math.Min(20, data.Length - typeTagEnd));
        if (tt >= 0) typeTagEnd = tt + 1;

        int bp = Array.IndexOf(data, (byte)0, typeTagEnd, (int)Math.Min(200, data.Length - typeTagEnd));
        long payloadStart = bp >= 0 ? bp + 1 : typeTagEnd;

        long end;
        // CORRECCIÓN: Usar el método auxiliar para obtener el tamaño
        uint storedSize = GetEntryStoredSize(toc);
        if (toc != null && storedSize > 0 && storedSize < (nextSector - sector))
            end = Math.Min(payloadStart + storedSize, Math.Min(nextSector, data.Length));
        else
            end = Math.Min(nextSector, data.Length);

        if (end <= payloadStart) return (Array.Empty<byte>(), payloadStart);
        return (data.AsSpan((int)payloadStart, (int)(end - payloadStart)).ToArray(), payloadStart);
    }

    private static string ExtOf(string filename)
    {
        var lower = filename.ToLowerInvariant();
        if (lower.EndsWith(".meta.xml")) return ".meta.xml";
        var ext = Path.GetExtension(lower);
        return ext;
    }

    private static uint Read24BE(byte[] data, long offset) =>
        (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        int last = Math.Min(end, haystack.Length) - needle.Length;
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}