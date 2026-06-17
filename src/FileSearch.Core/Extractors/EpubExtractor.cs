using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts visible text from XHTML/HTML content entries inside .epub books.
/// </summary>
public sealed class EpubExtractor : ITextExtractor
{
    public string ExtractorId => "filesearch.epub";

    public string ExtractorVersion => "1";

    private static readonly HashSet<string> s_contentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xml"
    };

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".epub" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        int lineNumber = 0;

        foreach (var entry in archive.Entries.Where(IsContentEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var text = MarkupText.FromHtml(content);
            if (string.IsNullOrEmpty(text)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, $"[{entry.FullName}] {text}");
        }
    }

    private static bool IsContentEntry(ZipArchiveEntry entry)
    {
        if (entry.Length == 0) return false;

        var extension = Path.GetExtension(entry.Name);
        if (string.IsNullOrEmpty(extension) || !s_contentExtensions.Contains(extension)) return false;

        return !entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase);
    }
}
