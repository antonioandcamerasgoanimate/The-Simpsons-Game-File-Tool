namespace TSGFileTool.Formats;

/// <summary>
/// One 24-byte record from an EA "SToc" table of contents (.str files used by
/// The Simpsons Game on Xbox 360).
///
/// FIELD MEANINGS CONFIRMED against the real, authoritative QuickBMS script
/// (simpsons_str.bms, by Luigi Auriemma/aluigi) - this is a real, working
/// reference implementation, not a guess, and every field below has been
/// validated by successfully decompressing real entries end-to-end (see
/// StocArchive's doc comment for the full pipeline):
///
///   0x00  uint32  TypeHash          - hash identifying the resource kind.
///   0x04  uint32  TableTag          - constant for every record in a given
///                                     file; used to detect the end of the
///                                     TOC table.
///   0x08  uint32  DecompressedSize  - size of this entry's data AFTER
///                                     decompression (was misidentified as
///                                     "OffsetA", an offset, in an earlier
///                                     version of this tool - it never was
///                                     one).
///   0x0C  uint32  IgnoreSize        - present in the script under this exact
///                                     name; not consumed for extraction.
///   0x10  uint32  CompressedSize    - the actual compressed byte length to
///                                     read starting at this entry's BASE_OFF
///                                     (was misidentified as "Size", the
///                                     entry's on-disk size, which happens to
///                                     be a less confusing way to describe
///                                     the same value but obscured that it's
///                                     specifically the *compressed* length).
///   0x14  uint32  Reserved          - unconfirmed/unused by the script.
///
/// None of these fields are literal file offsets - the real per-entry
/// location (BASE_OFF) is computed sequentially: it starts right after the
/// TOC, aligned up to the next 0x800 boundary, and after each entry is
/// processed BASE_OFF advances by that entry's CompressedSize. See
/// StocArchive.Load() for that computation.
/// </summary>
public sealed class StocEntry
{
    public int Index { get; set; }
    public uint TypeHash { get; set; }
    public uint TableTag { get; set; }
    public uint DecompressedSize { get; set; }
    public uint IgnoreSize { get; set; }
    public uint CompressedSize { get; set; }
    public uint Reserved { get; set; }

    public long RecordOffset { get; set; }

    /// <summary>Computed by StocArchive.Load() while walking the TOC in order -
    /// the real, sequential start-of-data position for this entry (see the
    /// BASE_OFF computation in StocArchive's doc comment). Not stored in the
    /// TOC record itself.</summary>
    public long BaseOffset { get; set; }

    public string DisplayName => $"entry_{Index:D3}_{TypeHash:X8}";

    public override string ToString() => DisplayName;
}
