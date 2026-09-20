using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace TSGFileTool.Formats;

/// <summary>
/// The Simpsons Game (NewGen: PS3/Xbox 360) string-label hash function.
/// This is a real, publicly documented reverse-engineering result (Edness,
/// reimplemented from the function at 0x827451C0 on Xbox 360 / 0x00522090
/// on PS3): https://github.com/EdnessP/scripts/blob/main/simpsons-game/tsg_hash.py
///
///   hash = 0
///   for ch in lowercase(str): hash = (0x1003F * hash + ord(ch)) mod 2^32
///
/// The game uses this to turn resource/string labels into the 32-bit hashes
/// stored on disc (this is very likely the same family of hash behind the
/// TypeHash/tag fields StocArchive finds in .str tables, though EA never
/// published which specific strings feed which fields). TryResolve() below
/// hashes a candidate word list with this function and matches it against
/// whatever hash you give it, so entries can get a real name instead of a
/// raw hex value wherever a match is found.
/// </summary>
public static class TsgHash
{
    public static uint Compute(string value)
    {
        uint hash = 0;
        foreach (char c in value.ToLowerInvariant())
            hash = unchecked(0x1003F * hash + c);
        return hash;
    }
}

/// <summary>
/// Asset-index -> name resolution for real, decompressed .str sub-files
/// (StocArchive.DecodedAssets). Each asset already has a real name recovered
/// during decompression (see StocArchive's doc comment) - this only stores
/// manual overrides, keyed by QfsAsset.Index (a stable per-archive-load
/// sequential index, unique per sub-file). Persisted to a
/// "&lt;archive&gt;.names.xml" sidecar next to the .str file and loaded
/// automatically the next time that archive opens.
///
/// HISTORY: earlier versions of this tool keyed manual names by TypeHash
/// (a resource *category* id shared by many sub-files - renaming one entry
/// silently renamed every other entry sharing that hash), then by
/// EarsSubFile.SectorOffset (from the old heuristic-scan sub-file list).
/// Both key schemes are obsolete now that real decompression replaces the
/// heuristic scan; old sidecars using either key are skipped rather than
/// mis-applied to the wrong entries.
/// </summary>
public sealed class NameDatabase
{
    private readonly Dictionary<long, string> _manual = new();

    public string? ResolveManualOnly(long assetIndex) => _manual.TryGetValue(assetIndex, out var m) ? m : null;

    public void SetManualName(long assetIndex, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) _manual.Remove(assetIndex);
        else _manual[assetIndex] = name;
    }

    public static string SidecarPathFor(string archivePath) => archivePath + ".names.xml";

    public void Load(string archivePath)
    {
        var path = SidecarPathFor(archivePath);
        if (!File.Exists(path)) return;
        var doc = System.Xml.Linq.XDocument.Load(path);
        foreach (var el in doc.Root?.Elements("Name") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            // Back-compat: sidecars keyed by "hash" (TypeHash) or "sectorOffset"
            // (the old heuristic-scan key) are from an obsolete scheme and are
            // skipped rather than mis-applied to the wrong entries.
            var attr = el.Attribute("assetIndex");
            if (attr == null) continue;
            var key = long.Parse(attr.Value, System.Globalization.CultureInfo.InvariantCulture);
            _manual[key] = el.Attribute("value")!.Value;
        }
    }

    public void Save(string archivePath)
    {
        var path = SidecarPathFor(archivePath);
        var root = new System.Xml.Linq.XElement("TsgNames",
            _manual.Select(kv => new System.Xml.Linq.XElement("Name",
                new System.Xml.Linq.XAttribute("assetIndex", kv.Key),
                new System.Xml.Linq.XAttribute("value", kv.Value))));
        new System.Xml.Linq.XDocument(root).Save(path);
    }
}
