using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OpenMcdf;

namespace FileSearch.Core.Extractors;

/// <summary>Reads the MAPI streams defined by MS-OXMSG; never opens attachment payloads.</summary>
internal static class MsgFileReader
{
    private const int MaxPropertyBytes = 4 * 1024 * 1024;

    public static OutlookMailMessage Read(string path)
    {
        using var root = RootStorage.OpenRead(path);
        var codePage = GetInteger(root, 0x3FFD, 32) ?? GetInteger(root, 0x3FDE, 32) ?? 1252;
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(codePage); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        var to = new List<string>();
        var cc = new List<string>();
        var attachmentNames = new List<string>();
        foreach (var entry in root.EnumerateEntries().Where(entry => entry.Type == EntryType.Storage).Take(2000))
        {
            if (entry.Name.StartsWith("__recip_version1.0_", StringComparison.Ordinal))
            {
                var recipient = root.OpenStorage(entry.Name);
                var address = Address(GetString(recipient, 0x3001, encoding),
                    GetString(recipient, 0x39FE, encoding) ?? GetString(recipient, 0x3003, encoding));
                var type = GetInteger(recipient, 0x0C15, 8);
                if (type == 1) to.Add(address);
                else if (type == 2) cc.Add(address);
            }
            else if (entry.Name.StartsWith("__attach_version1.0_", StringComparison.Ordinal))
            {
                var attachment = root.OpenStorage(entry.Name);
                var name = GetString(attachment, 0x3707, encoding) ?? GetString(attachment, 0x3704, encoding);
                if (!string.IsNullOrWhiteSpace(name)) attachmentNames.Add(Clean(name));
            }
        }
        var metadata = new MailMessageMetadata("msg", Clean(GetString(root, 0x0037, encoding)),
            Address(GetString(root, 0x0C1A, encoding), GetString(root, 0x5D01, encoding) ?? GetString(root, 0x0C1F, encoding)),
            to.Count > 0 ? Clean(string.Join("; ", to)) : Clean(GetString(root, 0x0E04, encoding)),
            cc.Count > 0 ? Clean(string.Join("; ", cc)) : Clean(GetString(root, 0x0E03, encoding)),
            GetDate(root, 0x0039) ?? GetDate(root, 0x0E06), string.Empty);
        var body = GetString(root, 0x1000, encoding);
        if (string.IsNullOrWhiteSpace(body))
        {
            var html = GetBytes(root, 0x1013, 0x0102);
            if (html is not null)
            {
                // BOM detection takes precedence over the message's Internet code page.
                using var reader = new StreamReader(new MemoryStream(html), encoding, detectEncodingFromByteOrderMarks: true);
                body = OutlookMailText.FromHtml(reader.ReadToEnd());
            }
            else if (GetBytes(root, 0x1009, 0x0102) is { } compressed)
                body = OutlookMailText.FromHtml(RtfPipe.Rtf.ToHtml(encoding.GetString(OutlookRtfCompression.Decompress(compressed))));
        }
        return new OutlookMailMessage(metadata, body ?? string.Empty, attachmentNames);
    }

    private static string? GetString(Storage storage, ushort id, Encoding encoding)
    {
        var unicode = GetBytes(storage, id, 0x001F);
        if (unicode is not null) return Encoding.Unicode.GetString(unicode).TrimEnd('\0');
        var ansi = GetBytes(storage, id, 0x001E);
        return ansi is null ? null : encoding.GetString(ansi).TrimEnd('\0');
    }

    private static byte[]? GetBytes(Storage storage, ushort id, ushort type)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"__substg1.0_{id:X4}{type:X4}");
        if (!storage.TryOpenStream(name, out var stream)) return null;
        using (stream)
        {
            if (stream.Length > MaxPropertyBytes) throw new InvalidDataException("An Outlook message property exceeds the 4 MiB read limit.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    private static long? GetFixedProperty(Storage storage, ushort id, ushort type, int headerBytes)
    {
        if (!storage.TryOpenStream("__properties_version1.0", out var stream)) return null;
        using (stream)
        {
            if (stream.Length > MaxPropertyBytes) throw new InvalidDataException("The Outlook property table exceeds the read limit.");
            stream.Position = Math.Min(headerBytes, stream.Length);
            Span<byte> property = stackalloc byte[16];
            var tag = ((uint)id << 16) | type;
            while (stream.Length - stream.Position >= property.Length)
            {
                stream.ReadExactly(property);
                if (BinaryPrimitives.ReadUInt32LittleEndian(property) == tag)
                    return BinaryPrimitives.ReadInt64LittleEndian(property[8..]);
            }
            return null;
        }
    }

    private static int? GetInteger(Storage storage, ushort id, int headerBytes) =>
        GetFixedProperty(storage, id, 0x0003, headerBytes) is { } value ? unchecked((int)value) : null;

    private static DateTime? GetDate(Storage storage, ushort id)
    {
        var value = GetFixedProperty(storage, id, 0x0040, 32);
        if (value is null) return null;
        try { return DateTime.FromFileTimeUtc(value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string Clean(string? value) => MarkupText.Normalize(value is null ? string.Empty : value[..Math.Min(value.Length, 4096)]);
    private static string Address(string? name, string? address) =>
        string.IsNullOrWhiteSpace(address) ? Clean(name) : $"{Clean(name)} <{Clean(address)}>".Trim();
}
