using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TSGFileTool.Formats;

namespace TSGFileTool.Xml;

/// <summary>
/// XML export for an .str archive's real, decompressed asset list
/// (StocArchive.DecodedAssets) - the actual, correct extraction result now
/// that EARS/QFS decompression works. Each entry here is genuinely
/// decompressed data with a real name, not a heuristic guess.
/// </summary>
public static class StocManifest
{
    public static void Write(StocArchive archive, string xmlPath)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = new XElement("StocArchive",
            new XAttribute("source", Path.GetFileName(archive.SourcePath)),
            new XAttribute("version", archive.Version),
            new XAttribute("tocEntryCount", archive.Entries.Count),
            new XAttribute("decodedAssetCount", archive.DecodedAssets.Count),
            archive.Entries.Select((e, i) => new XElement("TocEntry",
                new XAttribute("index", e.Index),
                new XAttribute("typeHash", $"0x{e.TypeHash:X8}"),
                new XAttribute("decompressedSize", e.DecompressedSize),
                new XAttribute("compressedSize", e.CompressedSize),
                new XAttribute("baseOffset", e.BaseOffset),
                new XAttribute("decodeError", archive.EntryDecodeErrors.ElementAtOrDefault(i) ?? "")
            )),
            archive.DecodedAssets.Select(a => new XElement("Asset",
                new XAttribute("index", a.Index),
                new XAttribute("file", UniqueName(a.Name, usedNames)),
                new XAttribute("tag", a.Tag),
                new XAttribute("buildPath", a.BuildPath),
                new XAttribute("parentTocEntry", a.ParentTocEntryIndex),
                new XAttribute("innerOffset", a.InnerOffset),
                new XAttribute("size", a.Data.Length)
            ))
        );
        new XDocument(root).Save(xmlPath);
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var root = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            var candidate = $"{root}_{n}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
