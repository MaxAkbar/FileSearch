using System.Runtime.CompilerServices;
using FileSearch.Core.Extractors;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FileSearch.WindowsOcr;

public sealed class WindowsEmbeddedImageOcrService : IEmbeddedImageOcrService
{
    private readonly WindowsImageOcrOptions _options;

    public WindowsEmbeddedImageOcrService(WindowsImageOcrOptions? options = null)
    {
        _options = options ?? new WindowsImageOcrOptions();
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        byte[] imageBytes,
        EmbeddedImageOcrRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsEnabled() || imageBytes.Length == 0 || IsTooLarge(imageBytes.Length))
            yield break;

        var engine = WindowsOcrEngineFactory.TryCreate(_options);
        if (engine is null)
            yield break;

        OcrResult result;
        int imageWidth;
        int imageHeight;
        try
        {
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await using (var writable = randomAccessStream.AsStreamForWrite())
            {
                await writable.WriteAsync(imageBytes, cancellationToken).ConfigureAwait(false);
                await writable.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            randomAccessStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            if (IsTooManyPixels(decoder))
                yield break;

            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            using var converted = NeedsConversion(bitmap)
                ? SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                : null;
            var ocrBitmap = converted ?? bitmap;
            imageWidth = ocrBitmap.PixelWidth;
            imageHeight = ocrBitmap.PixelHeight;
            cancellationToken.ThrowIfCancellationRequested();
            result = await engine.RecognizeAsync(ocrBitmap);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            yield break;
        }

        var lineNumber = 0;
        foreach (var line in result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Join(" ", line.Words.Select(word => word.Text)).Trim();
            if (text.Length == 0)
                continue;

            yield return new TextLine(
                ++lineNumber,
                text,
                BuildAnchor(line, request, imageWidth, imageHeight));
        }
    }

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

    private bool IsTooLarge(long length) =>
        _options.MaxImageBytes > 0 && length > _options.MaxImageBytes;

    private bool IsTooManyPixels(BitmapDecoder decoder) =>
        _options.MaxImagePixels > 0 &&
        decoder.PixelHeight > 0 &&
        decoder.PixelWidth > (ulong)_options.MaxImagePixels / decoder.PixelHeight;

    private static bool NeedsConversion(SoftwareBitmap bitmap) =>
        bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
        bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied;

    private static SourceAnchor? BuildAnchor(
        OcrLine line,
        EmbeddedImageOcrRequest request,
        int imageWidth,
        int imageHeight)
    {
        var region = WindowsOcrAnchorBuilder.Region(line, imageWidth, imageHeight);
        if (region is null)
            return null;

        return SourceAnchor.EmbeddedOcrRegion(
            request.AnchorKind,
            request.Label,
            request.MemberPath,
            region.Value.X,
            region.Value.Y,
            region.Value.Width,
            region.Value.Height,
            imageWidth,
            imageHeight,
            request.Page,
            request.Section,
            request.Sheet);
    }
}
