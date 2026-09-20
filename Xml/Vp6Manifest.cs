using System.Linq;
using System.Xml.Linq;
using TSGFileTool.Formats;
using System.Collections.Generic;

namespace TSGFileTool.Xml;

public static class Vp6Manifest
{
    public static void Write(List<Chunk> chunks, string xmlPath)
    {
        var root = new XElement("Vp6ChunkIndex",
            chunks.Select((c, i) => new XElement("Chunk",
                new XAttribute("index", i),
                new XAttribute("file", $"{i:D3}_{c.Tag}.bin"),
                new XAttribute("tag", c.Tag),
                new XAttribute("offset", c.Offset),
                new XAttribute("payloadSize", c.PayloadSize)
            ))
        );
        new XDocument(root).Save(xmlPath);
    }
}
