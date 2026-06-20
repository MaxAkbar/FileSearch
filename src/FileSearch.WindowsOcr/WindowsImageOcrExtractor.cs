using System.Runtime.CompilerServices;
using FileSearch.Core.Extractors;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace FileSearch.WindowsOcr;

public sealed class WindowsImageOcrExtractor : ITextExtractor
{
    private readonly WindowsImageOcrOptions _options;

    public WindowsImageOcrExtractor(WindowsImageOcrOptions? options = null)
    {
        _options = options ?? new WindowsImageOcrOptions();
    }

    public string ExtractorId => "filesearch.windows-image-ocr";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions => ImageOcrFileTypes.SupportedExtensions;

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            yield break;

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(path);
        }
        catch
        {
            yield break;
        }

        if (!fileInfo.Exists || IsTooLarge(fileInfo))
            yield break;

        var engine = WindowsOcrEngineFactory.TryCreate(_options);
        if (engine is null)
            yield break;

        OcrResult result;
        int imageWidth;
        int imageHeight;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = await file.OpenReadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var decoder = await BitmapDecoder.CreateAsync(stream);
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

        foreach (var line in EnumerateLines(result, imageWidth, imageHeight, cancellationToken))
            yield return line;
    }

    private static IEnumerable<TextLine> EnumerateLines(
        OcrResult result,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
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
                WindowsOcrAnchorBuilder.ImageRegion(line, imageWidth, imageHeight));
        }
    }

    private bool IsTooLarge(FileInfo fileInfo) =>
        _options.MaxImageBytes > 0 && fileInfo.Length > _options.MaxImageBytes;

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

}
