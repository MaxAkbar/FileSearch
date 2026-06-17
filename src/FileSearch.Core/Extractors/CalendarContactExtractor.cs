using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts searchable values from iCalendar (.ics) and vCard (.vcf) files.
/// </summary>
public sealed class CalendarContactExtractor : ITextExtractor
{
    public string ExtractorId => "filesearch.calendar-contact";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".ics", ".vcf" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        int lineNumber = 0;

        foreach (var line in UnfoldLines(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = ExtractValue(line);
            if (string.IsNullOrWhiteSpace(value)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, value);
        }
    }

    private static IEnumerable<string> UnfoldLines(string content)
    {
        using var reader = new StringReader(content.Replace("\r\n", "\n"));
        var current = new StringBuilder();

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                current.Append(line.AsSpan(1));
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(line);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string ExtractValue(string line)
    {
        var separator = line.IndexOf(':');
        if (separator < 0 || separator == line.Length - 1) return string.Empty;

        var name = line[..separator];
        if (name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("END", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("VERSION", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRODID", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return MarkupText.Normalize(UnescapeValue(line[(separator + 1)..]));
    }

    private static string UnescapeValue(string value)
    {
        return value
            .Replace(@"\n", " ", StringComparison.OrdinalIgnoreCase)
            .Replace(@"\,", ",", StringComparison.Ordinal)
            .Replace(@"\;", ";", StringComparison.Ordinal)
            .Replace(@"\\", @"\", StringComparison.Ordinal);
    }
}
