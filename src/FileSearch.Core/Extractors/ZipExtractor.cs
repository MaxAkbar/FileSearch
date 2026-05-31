using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Reads text-like entries inside ZIP archives. v1 limitation: only entries
/// whose names look like text files (by extension) are searched; nested
/// archives, PDFs, or Office docs inside a zip are skipped.
/// Each entry is preceded by a "=== entry/path ===" marker line so callers
/// can identify which archive member matched.
/// </summary>
public sealed class ZipExtractor : ITextExtractor
{
    private static readonly HashSet<string> s_textExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".html", ".htm", ".css", ".js", ".ts",
        ".cs", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift", ".php",
        ".sh", ".bash", ".ps1", ".bat", ".cmd", ".sql", ".graphql", ".proto",
    };

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".zip" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        int lineNumber = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length == 0) continue; // directory or empty file
            if (!IsTextEntry(entry.Name)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, $"=== {entry.FullName} ===");

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                yield return new TextLine(lineNumber, line);
            }
        }
    }

    private static bool IsTextEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrEmpty(ext) && s_textExtensions.Contains(ext);
    }
}
