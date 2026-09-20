using System.IO;

namespace TSGFileTool.Formats;

/// <summary>
/// One real sub-file recovered from inside a decompressed TOC-entry block.
/// Unlike the earlier EarsSubFile (found by heuristically scanning for
/// plausible-looking filenames in still-compressed bytes), everything here
/// comes from the confirmed-correct, byte-exact structure documented in
/// simpsons_str.bms: Data is genuinely decompressed and ready to use - open
/// it directly on the Texture/RenderWare tabs and it will parse for real.
/// </summary>
public sealed class QfsAsset
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public string BuildPath { get; set; } = "";
    public int ParentTocEntryIndex { get; set; }
    public long InnerOffset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public string Extension => Path.GetExtension(Name);
}
