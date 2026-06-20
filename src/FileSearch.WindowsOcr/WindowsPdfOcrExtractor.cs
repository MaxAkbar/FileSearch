using System.Runtime.CompilerServices;
using FileSearch.Core.Extractors;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileSearch.WindowsOcr;

public sealed class WindowsPdfOcrExtractor : IContextualTextExtractor
{
    private readonly WindowsImageOcrOptions _options;
    private readonly PdfExtractor _primary;

    public WindowsPdfOcrExtractor(WindowsImageOcrOptions? options = null, PdfExtractor? primary = null)
    {
        _options = options ?? new WindowsImageOcrOptions();
        _primary = primary ?? new PdfExtractor();
    }

    public string ExtractorId => "filesearch.windows-pdf-ocr";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
        _primary.ExtractAsync(path, cancellationToken);

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.EnableOcr || !IsEnabled())
        {
            await foreach (var line in _primary.ExtractAsync(path, cancellationToken).ConfigureAwait(false))
                yield return line;
            yield break;
        }

        var nativeLines = await ReadNativeLinesAsync(path, cancellationToken).ConfigureAwait(false);

        var document = await TryLoadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            foreach (var line in nativeLines)
                yield return line;
            yield break;
        }

        var engine = WindowsOcrEngineFactory.TryCreate(_options);
        if (engine is null)
        {
            foreach (var line in nativeLines)
                yield return line;
            yield break;
        }

        var pagesWithNativeText = nativeLines
            .Select(line => line.Anchor?.Page)
            .Where(page => page is > 0)
            .Select(page => page!.Value)
            .ToHashSet();
        var nativeLinesByPage = nativeLines
            .Where(line => line.Anchor?.Page is > 0)
            .GroupBy(line => line.Anchor!.Page!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var ocrPages = GetOcrPageNumbers(pagesWithNativeText, document.PageCount, _options.ResolveMaxPdfPages())
            .ToHashSet();

        var lineNumber = 0;
        foreach (var line in nativeLines.Where(line => line.Anchor?.Page is not > 0))
            yield return Renumber(line, ++lineNumber);

        for (uint pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nativeLinesByPage.TryGetValue(checked((int)pageNumber), out var pageNativeLines))
            {
                foreach (var line in pageNativeLines)
                    yield return Renumber(line, ++lineNumber);
            }

            if (!ocrPages.Contains(checked((int)pageNumber)))
                continue;

            var pageLines = await TryRecognizePageAsync(document, pageNumber - 1u, engine, cancellationToken)
                .ConfigureAwait(false);
            foreach (var line in pageLines)
            {
                yield return Renumber(line, ++lineNumber);
            }
        }
    }

    internal static IReadOnlyList<int> GetOcrPageNumbers(
        IReadOnlySet<int> pagesWithNativeText,
        uint pageCount,
        int maxPdfPages)
    {
        if (pageCount == 0)
            return Array.Empty<int>();

        var effectivePageCount = maxPdfPages > 0
            ? Math.Min(pageCount, (uint)maxPdfPages)
            : pageCount;
        var pages = new List<int>();
        for (uint pageNumber = 1; pageNumber <= effectivePageCount; pageNumber++)
        {
            var page = checked((int)pageNumber);
            if (!pagesWithNativeText.Contains(page))
                pages.Add(page);
        }

        return pages;
    }

    private async Task<List<TextLine>> ReadNativeLinesAsync(string path, CancellationToken cancellationToken)
    {
        var lines = new List<TextLine>();
        await foreach (var line in _primary.ExtractAsync(path, cancellationToken).ConfigureAwait(false))
            lines.Add(line);
        return lines;
    }

    private async Task<PdfDocument?> TryLoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || IsTooLarge(fileInfo))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<TextLine>> TryRecognizePageAsync(
        PdfDocument document,
        uint pageIndex,
        OcrEngine engine,
        CancellationToken cancellationToken)
    {
        try
        {
            using var page = document.GetPage(pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (IsTooManyPixels(decoder))
                return [];

            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            using var converted = NeedsConversion(bitmap)
                ? SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                : null;
            var ocrBitmap = converted ?? bitmap;
            cancellationToken.ThrowIfCancellationRequested();

            var ocrResult = await engine.RecognizeAsync(ocrBitmap);
            return BuildLines(
                ocrResult,
                checked((int)pageIndex + 1),
                ocrBitmap.PixelWidth,
                ocrBitmap.PixelHeight,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private List<TextLine> BuildLines(
        OcrResult result,
        int pageNumber,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        var lines = new List<TextLine>();
        var lineNumber = 0;
        foreach (var line in result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Join(" ", line.Words.Select(word => word.Text)).Trim();
            if (text.Length == 0)
                continue;

            lines.Add(new TextLine(
                ++lineNumber,
                text,
                WindowsOcrAnchorBuilder.PdfRegion(line, pageNumber, imageWidth, imageHeight)));
        }

        return lines;
    }

    private bool IsTooLarge(FileInfo fileInfo) =>
        _options.MaxPdfBytes > 0 && fileInfo.Length > _options.MaxPdfBytes;

    private bool IsEnabled()
    {
        try
        {
            return _options.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private bool IsTooManyPixels(BitmapDecoder decoder) =>
        _options.MaxImagePixels > 0 &&
        decoder.PixelHeight > 0 &&
        decoder.PixelWidth > (ulong)_options.MaxImagePixels / decoder.PixelHeight;

    private static bool NeedsConversion(SoftwareBitmap bitmap) =>
        bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
        bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied;

    private static TextLine Renumber(TextLine line, int lineNumber) =>
        line.Number == lineNumber ? line : line with { Number = lineNumber };
}
