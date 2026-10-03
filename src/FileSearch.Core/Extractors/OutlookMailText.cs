using System.Globalization;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Extractors;

internal static class OutlookMailText
{
    private static readonly Regex s_blocks = new(@"<\s*/?\s*(?:br|p|div|li|tr|h[1-6])\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string FromHtml(string html) =>
        string.Join('\n', s_blocks.Replace(MarkupText.WithoutScriptsAndComments(html), "\n").Split('\n').Select(MarkupText.FromHtml));

    public static IEnumerable<string> Lines(OutlookMailMessage message)
    {
        var metadata = message.Metadata;
        yield return "Subject: " + metadata.Subject;
        yield return "From: " + metadata.From;
        yield return "To: " + metadata.To;
        if (!string.IsNullOrWhiteSpace(metadata.Cc)) yield return "Cc: " + metadata.Cc;
        if (metadata.DateUtc is { } date) yield return "Date: " + date.ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(metadata.Folder)) yield return "Folder: " + metadata.Folder;
        foreach (var name in message.AttachmentNames) yield return "Attachment: " + name;
        using var reader = new StringReader(message.Body);
        while (reader.ReadLine() is { } raw)
        {
            // Bound line length even for HTML or minified bodies that lack newlines.
            for (var offset = 0; offset < raw.Length; offset += 4096)
            {
                var line = MarkupText.Normalize(raw.Substring(offset, Math.Min(4096, raw.Length - offset)));
                if (line.Length > 0) yield return line;
            }
        }
    }
}
