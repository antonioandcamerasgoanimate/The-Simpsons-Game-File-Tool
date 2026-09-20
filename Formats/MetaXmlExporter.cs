using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using System.Collections.Generic;

namespace TSGFileTool.Formats;

/// <summary>
/// Writes a small human-readable/editable "&lt;name&gt;.meta.xml" sidecar next to
/// an extracted file, in the same spirit as the .meta.xml files CodeWalker
/// writes for GTA V resources: a plain XML description of what the tool
/// currently knows about a file (kind, size, hash, and format-specific
/// fields you pass in), separate from the binary payload itself. Re-reading
/// one of these doesn't reconstruct the binary - it's metadata, matching
/// what "meta.xml" means in CodeWalker's own exports.
/// </summary>
public static class MetaXmlExporter
{
    public static void Write(string binPath, string metaXmlPath, string kind, byte[] data,
        IEnumerable<(string Key, string Value)>? fields = null)
    {
        var root = new XElement("TsgMeta",
            new XAttribute("kind", kind),
            new XAttribute("file", Path.GetFileName(binPath)),
            new XAttribute("size", data.Length),
            new XAttribute("sha1", Convert.ToHexString(SHA1.HashData(data))));

        if (fields != null)
            foreach (var (key, value) in fields)
                root.Add(new XElement(key, value));

        new XDocument(root).Save(metaXmlPath);
    }
}
