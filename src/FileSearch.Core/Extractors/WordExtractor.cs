using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts paragraph text from Word .docx documents using OpenXml.
/// One <see cref="TextLine"/> per paragraph, with each paragraph's runs
/// concatenated into a single string.
/// </summary>
public sealed class WordExtractor : IContextualTextExtractor
{
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public WordExtractor(IEmbeddedImageOcrService? embeddedImageOcr = null)
    {
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
    }

    public string ExtractorId => "filesearch.word-openxml";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".docx" };

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
        await Task.Yield();

        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var mainDocumentPart = document.MainDocumentPart;
        if (mainDocumentPart is null) yield break;

        var body = mainDocumentPart.Document?.Body;
        if (body is null) yield break;

        int lineNumber = 0;
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrEmpty(text)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, text);
        }

        if (!context.EnableOcr)
            yield break;

        var imageParts = mainDocumentPart.ImageParts;
        if (imageParts is null)
            yield break;

        foreach (var imagePart in imageParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var memberPath = GetPackageMemberPath(mainDocumentPart, imagePart);
            var imageBytes = ReadPartBytes(imagePart);
            var request = new EmbeddedImageOcrRequest(
                SourceAnchorKind.Word,
                memberPath,
                $"image {memberPath}");
            await foreach (var line in _embeddedImageOcr.ExtractAsync(imageBytes, request, cancellationToken).ConfigureAwait(false))
                yield return line with { Number = ++lineNumber };
        }
    }

    private static byte[] ReadPartBytes(OpenXmlPart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string GetPackageMemberPath(OpenXmlPart ownerPart, OpenXmlPart part)
    {
        var partUri = part.Uri.ToString();
        if (partUri.StartsWith('/'))
            return partUri.TrimStart('/');

        var ownerUri = ownerPart.Uri.ToString().TrimStart('/');
        var slash = ownerUri.LastIndexOf('/');
        return slash >= 0
            ? $"{ownerUri[..slash]}/{partUri}".TrimStart('/')
            : partUri;
    }
}
