using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts cell text from .xlsx workbooks using ClosedXML.
/// Each non-empty row becomes one <see cref="TextLine"/>, prefixed with
/// "[Sheet] " for grep-ability, and cells joined with tabs.
/// </summary>
public sealed class ExcelExtractor : IContextualTextExtractor
{
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public ExcelExtractor(IEmbeddedImageOcrService? embeddedImageOcr = null)
    {
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
    }

    public string ExtractorId => "filesearch.excel-closedxml";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".xlsx" };

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
        // ClosedXML loads synchronously, so open on the thread pool. The
        // iterator still runs between yields on the consumer's thread — UI
        // callers must consume from a background task (FilePreviewService does).
        int lineNumber = 0;
        var workbook = await Task.Run(() => new XLWorkbook(path), cancellationToken).ConfigureAwait(false);
        using (workbook)
        {
            foreach (var sheet in workbook.Worksheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sheetName = sheet.Name;

                foreach (var row in sheet.RowsUsed())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cells = row.CellsUsed().Select(c => c.GetString());
                    var joined = string.Join('\t', cells);
                    if (string.IsNullOrEmpty(joined)) continue;

                    lineNumber++;
                    yield return new TextLine(lineNumber, $"[{sheetName}] {joined}");
                }
            }
        }

        if (!context.EnableOcr)
            yield break;

        foreach (var imagePart in EnumerateWorkbookImageParts(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imageBytes = ReadPartBytes(imagePart.Part);
            var request = new EmbeddedImageOcrRequest(
                SourceAnchorKind.Excel,
                imagePart.MemberPath,
                $"workbook image {imagePart.MemberPath}");
            await foreach (var line in _embeddedImageOcr.ExtractAsync(imageBytes, request, cancellationToken).ConfigureAwait(false))
                yield return line with { Number = ++lineNumber };
        }
    }

    private static IEnumerable<(ImagePart Part, string MemberPath)> EnumerateWorkbookImageParts(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart is null)
            yield break;

        foreach (var worksheetPart in workbookPart.WorksheetParts)
            foreach (var imagePart in worksheetPart.ImageParts)
                yield return (imagePart, GetPackageMemberPath(worksheetPart, imagePart));
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
