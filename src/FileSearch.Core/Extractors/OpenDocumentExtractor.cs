using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts text from OpenDocument files (.odt, .ods, .odp) by reading content.xml.
/// </summary>
public sealed class OpenDocumentExtractor : IContextualTextExtractor
{
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public OpenDocumentExtractor(IEmbeddedImageOcrService? embeddedImageOcr = null)
    {
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
    }

    public string ExtractorId => "filesearch.opendocument";

    public string ExtractorVersion => "2";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".odt", ".ods", ".odp" };

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
        var lineNumber = 0;
        var entry = archive.GetEntry("content.xml");
        if (entry is not null)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var text = MarkupText.FromXml(content);
            if (!string.IsNullOrEmpty(text))
                yield return new TextLine(++lineNumber, text);
        }

        if (!context.EnableOcr)
            yield break;

        foreach (var imageEntry in archive.Entries.Where(IsImageEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imageBytes = await ReadEntryBytesAsync(imageEntry, cancellationToken).ConfigureAwait(false);
            var request = new EmbeddedImageOcrRequest(
                SourceAnchorKind.OpenDocument,
                imageEntry.FullName,
                $"OpenDocument image {imageEntry.FullName}",
                Section: imageEntry.FullName);

            await foreach (var line in _embeddedImageOcr.ExtractAsync(imageBytes, request, cancellationToken).ConfigureAwait(false))
                yield return line with { Number = ++lineNumber };
        }
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
