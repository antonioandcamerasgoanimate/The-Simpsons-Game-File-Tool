using System.Text;
namespace TSGFileTool.Formats;

/// <summary>
/// Best-effort "what is this file" check against every magic/structure this
/// tool actually understands. Anything not matched stays honestly
/// "Unrecognized" rather than being forced into a guessed category.
/// </summary>
public static class FormatProbe
{
    public static string Probe(byte[] data)
    {
        if (EarsSectorScanner.LooksLikeEars(data))
            return "EARS-compressed payload";

        if (data.Length >= 4 && data[0] == 'S' && data[1] == 'T' && data[2] == 'o' && data[3] == 'c')
            return "SToc archive (.str) - open on the STR Archive tab.";

        if (data.Length >= 8 && data[0] == 'S' && data[1] == 'C' && data[2] == 'H' && data[3] == 'l')
            return "SCHl video container (.vp6) - open on the VP6 Video tab.";

        if (ItxdTexture.LooksLikeItxd(data))
            return "ITXD texture dictionary (.itxd) - open on the Texture tab.";

        if (data.Length >= 12 && RenderWareStream.LooksLikeRwStream(data))
        {
            uint typeId = BitConverter.ToUInt32(data, 0);
            return $"Looks like a RenderWare stream (top section: {RenderWareStream.NameFor(typeId)}) - open on the RenderWare tab.";
        }

        // Classic Havok packfile magic, widely repeated in community Havok tooling docs.
        if (data.Length >= 4 && data[0] == 0x57 && data[1] == 0xE0 && data[2] == 0xE0 && data[3] == 0x57)
            return "Looks like a classic Havok packfile magic (0x57 0xE0 0xE0 0x57). No structural parser here yet - use raw/hex tools.";

        if (data.Length >= 5 && data[0] == '<' && System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(5, data.Length)).StartsWith("<?xml"))
            return "Plain XML - open as text in any editor; this is metadata, not a binary resource.";

        if (RefPackDecoder.IsRefPack(data, 0))
            return "RefPack-compressed (EA's classic LZ77 codec) at offset 0 - try 'Try RefPack Decompress' on the Raw/Other Files tab.";

        if (ZlibDecoder.LooksLikeZlib(data, 0))
            return "Zlib-compressed at offset 0 - try 'Try Zlib Decompress' on the Raw/Other Files tab.";

        return "Unrecognized - no known magic matched. Raw/hex tools only.";
    }

    /// <summary>Best-guess file extension for a probe result string, used when
    /// exporting so extracted files land with a sensible extension instead of
    /// a bare .bin whenever the content is actually recognized.</summary>
    public static string GuessExtension(string probeResult) => probeResult switch
    {
        var s when s.Contains("SToc") => ".str",
        var s when s.Contains("SCHl") => ".vp6",
        var s when s.Contains("ITXD") => ".itxd",
        var s when s.Contains("RenderWare") => ".dff",
        var s when s.Contains("Havok") => ".hkx",
        var s when s.Contains("Plain XML") => ".xml",
        _ => ".bin"
    };
}
