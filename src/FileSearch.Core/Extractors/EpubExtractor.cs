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
public sealed class EpubExtractor : IContextualTextExtractor
{
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public EpubExtractor(IEmbeddedImageOcrService? embeddedImageOcr = null)
    {
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
    }

    public string ExtractorId => "filesearch.epub";

    public string ExtractorVersion => "2";

    private static readonly HashSet<string> s_contentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xml"
    };

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".epub" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, new TextExtractionContext(), cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
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

        if (!context.EnableOcr)
            yield break;

        foreach (var entry in archive.Entries.Where(IsImageEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imageBytes = await ReadEntryBytesAsync(entry, cancellationToken).ConfigureAwait(false);
            var request = new EmbeddedImageOcrRequest(
                SourceAnchorKind.Epub,
                entry.FullName,
                $"EPUB image {entry.FullName}",
                Section: entry.FullName);

            await foreach (var line in _embeddedImageOcr.ExtractAsync(imageBytes, request, cancellationToken).ConfigureAwait(false))
                yield return line with { Number = ++lineNumber };
        }
    }

    private static bool IsContentEntry(ZipArchiveEntry entry)
    {
        if (entry.Length == 0) return false;

        var extension = Path.GetExtension(entry.Name);
        if (string.IsNullOrEmpty(extension) || !s_contentExtensions.Contains(extension)) return false;

        return !entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageEntry(ZipArchiveEntry entry)
    {
        if (entry.Length == 0) return false;

        var extension = Path.GetExtension(entry.Name);
        return !string.IsNullOrEmpty(extension) && s_imageExtensions.Contains(extension);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".tif",
        ".tiff",
    };
}
