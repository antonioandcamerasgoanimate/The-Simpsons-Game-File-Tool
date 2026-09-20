using System.Linq;
using System.Xml.Linq;
using TSGFileTool.Formats;
using System.Collections.Generic;

namespace TSGFileTool.Xml;

public static class RwManifest
{
    public static void Write(List<RwSection> flatSections, string xmlPath)
    {
        var root = new XElement("RwSectionIndex",
            flatSections.Select((s, i) => new XElement("Section",
                new XAttribute("index", i),
                new XAttribute("file", $"{i:D3}_{Sanitize(s.TypeName)}.bin"),
                new XAttribute("typeId", $"0x{s.TypeId:X4}"),
                new XAttribute("typeName", s.TypeName),
                new XAttribute("depth", s.Depth),
                new XAttribute("offset", s.HeaderOffset),
                new XAttribute("size", s.Size),
                new XAttribute("libraryId", $"0x{s.LibraryId:X8}")
            ))
        );
        new XDocument(root).Save(xmlPath);
    }

    private static string Sanitize(string s) => new string(s.Where(c => !char.IsWhiteSpace(c) && c != '(' && c != ')').ToArray());
}
