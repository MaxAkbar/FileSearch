using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileSearch.Gui.Services;

/// <summary>Shared limits for read-only ZIP-based document and archive previews.</summary>
public static class PreviewPackage
{
    public const int MaxEntries = 10_000;
    public const int MaxEntryBytes = 64 * 1024 * 1024;
    public const int MaxXmlBytes = 32 * 1024 * 1024;

    public static bool IsSafeMember(string name) => !string.IsNullOrWhiteSpace(name) &&
        !name.Replace('\\', '/').StartsWith('/') && !name.Contains(':') &&
        !name.Replace('\\', '/').Split('/').Any(part => part == "..");

    public static string ResolveMember(string parent, string relative)
    {
        if (relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\')) throw new InvalidDataException("External document resource.");
        var segments = new List<string>();
        var combined = parent.Contains('/') ? parent[..(parent.LastIndexOf('/') + 1)] + relative : relative;
        foreach (var part in Uri.UnescapeDataString(combined.Split('#')[0]).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (segments.Count == 0) throw new InvalidDataException("Resource leaves the package."); segments.RemoveAt(segments.Count - 1); }
            else segments.Add(part);
        }
        string resolved = string.Join('/', segments);
        if (!IsSafeMember(resolved)) throw new InvalidDataException("Invalid document resource path.");
        return resolved;
    }

    public static ZipArchive Open(string path)
    {
        var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count <= MaxEntries) return archive;
        archive.Dispose();
        throw new InvalidDataException("The preview is limited to 10,000 package entries.");
    }

    public static byte[] Read(ZipArchiveEntry entry, int limit = MaxEntryBytes, CancellationToken token = default)
    {
        if (entry.Length > limit || (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 1000))
            throw new InvalidDataException("The package member exceeds the preview size or expansion limit.");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(buffer);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("The package member exceeds the preview limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static XDocument ReadXml(ZipArchiveEntry entry, CancellationToken token = default) => ParseXml(Read(entry, MaxXmlBytes, token));
    public static XDocument ParseXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using (var depthReader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes, CloseInput = false }))
            while (depthReader.Read())
                if (depthReader.Depth > 128) throw new InvalidDataException("Document XML exceeds the preview nesting limit.");
        stream.Position = 0;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
        return XDocument.Load(reader);
    }

    public static string ReadText(string path, int limit, CancellationToken token)
    {
        if (new FileInfo(path).Length > limit) throw new InvalidDataException("The document exceeds the preview size limit.");
        token.ThrowIfCancellationRequested();
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var value = reader.ReadToEnd();
        token.ThrowIfCancellationRequested();
        return value;
    }
}
