using System.Collections.Generic;
namespace TSGFileTool.Formats;

public sealed class RwSection
{
    public uint TypeId { get; set; }
    public uint Size { get; set; }
    public uint LibraryId { get; set; }
    public long HeaderOffset { get; set; }
    public long DataOffset => HeaderOffset + 12;
    public int Depth { get; set; }
    public List<RwSection> Children { get; } = new();

    public string TypeName => RenderWareStream.NameFor(TypeId);
}

/// <summary>
/// RenderWare binary stream format, used by RenderWare-based titles for
/// models (.dff), worlds (.rws/.bsp-ish level streams) and texture
/// dictionaries (.txd). This is a real, long-publicly-documented format
/// (RenderWare shipped an SDK and the chunk layout has been community
/// reference material since the PS2/Xbox GTA III-era modding scene) - NOT
/// something reverse engineered from your files:
///
///   Each section ("chunk") is a 12-byte header + payload, all little-endian:
///     uint32 typeId
///     uint32 size        (payload size, header not included)
///     uint32 libraryId    (packed RW version / build number)
///   Sections nest: a Clump/Atomic/Geometry/etc. section's payload is itself
///   a sequence of child sections, almost always starting with a small
///   "Struct" (0x0001) section holding fixed-layout fields for the parent.
///
/// The TypeId -> name table below covers the common/stable IDs that have
/// been public for a long time; treat any "Unknown (0x..)" as exactly that -
/// this parser still reports its size/offset/nesting correctly either way,
/// which is what extraction and the XML export need.
/// </summary>
public static class RenderWareStream
{
    private static readonly Dictionary<uint, string> KnownTypes = new()
    {
        [0x0001] = "Struct",
        [0x0002] = "String",
        [0x0003] = "Extension",
        [0x0005] = "Camera",
        [0x0006] = "Texture",
        [0x0007] = "Material",
        [0x0008] = "MaterialList",
        [0x0009] = "AtomicSection",
        [0x000A] = "PlaneSection",
        [0x000B] = "World",
        [0x000C] = "Spline",
        [0x000D] = "Matrix",
        [0x000E] = "FrameList",
        [0x000F] = "Geometry",
        [0x0010] = "Clump",
        [0x0011] = "Light",
        [0x0012] = "UnicodeString",
        [0x0013] = "Atomic",
        [0x0014] = "TextureNative",
        [0x0015] = "TextureDictionary",
        [0x0016] = "AnimDatabase",
        [0x0017] = "Image",
        [0x0018] = "SkinAnimation",
        [0x0019] = "GeometryList",
        [0x001A] = "AnimAnimation",
        [0x001B] = "Team",
        [0x001C] = "Crowd",
        [0x001F] = "MorphPLG",
        [0x0027] = "SkyMipmapVal",
        [0x0050] = "SkinPLG",
        [0x0053] = "HAnimPLG",
        [0x0060] = "BinMeshPLG",
        [0x0116] = "UserDataPLG",
    };

    public static string NameFor(uint typeId) =>
        KnownTypes.TryGetValue(typeId, out var n) ? n : $"Unknown (0x{typeId:X4})";

    public static bool LooksLikeRwStream(byte[] data)
    {
        if (data.Length < 12) return false;
        uint size = BitConverter.ToUInt32(data, 4);
        // A valid top-level section's declared size should roughly match the file.
        return size > 0 && size <= data.Length && size >= data.Length - 12 - 4096;
    }

    public static List<RwSection> Parse(byte[] data)
    {
        var top = new List<RwSection>();
        long pos = 0;
        while (pos + 12 <= data.Length)
        {
            var section = ReadSection(data, pos, 0);
            if (section == null) break;
            top.Add(section);
            pos = section.HeaderOffset + 12 + section.Size;
        }
        return top;
    }

    private static RwSection? ReadSection(byte[] data, long offset, int depth)
    {
        if (offset + 12 > data.Length) return null;
        uint typeId = BitConverter.ToUInt32(data, (int)offset);
        uint size = BitConverter.ToUInt32(data, (int)offset + 4);
        uint lib = BitConverter.ToUInt32(data, (int)offset + 8);
        if (offset + 12 + size > data.Length) return null;

        var section = new RwSection { TypeId = typeId, Size = size, LibraryId = lib, HeaderOffset = offset, Depth = depth };

        // Recurse a bounded depth into container-like sections whose payload
        // is itself a run of child sections (Struct/String/Extension leaves
        // hold raw fixed-layout data, not children, so we don't descend there).
        if (depth < 8 && typeId != 0x0001 && typeId != 0x0002 && size >= 12)
        {
            long childPos = offset + 12;
            long end = offset + 12 + size;
            while (childPos + 12 <= end)
            {
                var child = ReadSection(data, childPos, depth + 1);
                if (child == null) break;
                section.Children.Add(child);
                childPos = child.HeaderOffset + 12 + child.Size;
            }
        }
        return section;
    }

    public static IEnumerable<RwSection> Flatten(IEnumerable<RwSection> sections)
    {
        foreach (var s in sections)
        {
            yield return s;
            foreach (var c in Flatten(s.Children)) yield return c;
        }
    }

    public static byte[] ExtractPayload(byte[] data, RwSection s) =>
        data.AsSpan((int)s.DataOffset, (int)s.Size).ToArray();
}
