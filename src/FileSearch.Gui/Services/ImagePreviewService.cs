using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using ImagingDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace FileSearch.Gui.Services;

public static class ImagePreviewService
{
    private const int AbortHResult = unchecked((int)0x80004004);
    private const int OutOfMemoryHResult = unchecked((int)0x8007000E);
    private const int CancelledHResult = unchecked((int)0x800704C7);

    public static Task<DocumentPreview> LoadAsync(string path, int page, CancellationToken token) =>
        LoadAsync(path, page, HasWebpDecoder, token);

    internal static bool HasWebpDecoder() => ImagingDecoder.GetDecoderInformationEnumerator()
        .Any(codec => codec.FileExtensions.Contains(".webp", StringComparer.OrdinalIgnoreCase));

    internal static Task<DocumentPreview> LoadAsync(string path, int page, Func<bool> hasWebpDecoder,
        CancellationToken token) => LoadAsync(path, page, hasWebpDecoder, CreateDecoderAsync, token);

    private static Task<ImagingDecoder> CreateDecoderAsync(IRandomAccessStream stream, CancellationToken token) =>
        ImagingDecoder.CreateAsync(stream).AsTask(token);

    internal static Task<DocumentPreview> LoadAsync(string path, int page, Func<bool> hasWebpDecoder,
        Func<IRandomAccessStream, CancellationToken, Task<ImagingDecoder>> createDecoder,
        CancellationToken token) => Task.Run(async () =>
    {
        if (new FileInfo(path).Length > DocumentPreviewService.MaxImageBytes) throw new InvalidDataException("Image previews are limited to 32 MB.");
        bool isWebp = Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase);
        if (isWebp && !hasWebpDecoder())
        {
            token.ThrowIfCancellationRequested();
            return new DocumentPreview(DocumentPreviewKind.Details, "WEBP · Windows decoder unavailable",
                "A Windows WebP decoder is not available on this computer. Use Open to view the file in another application.");
        }
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token).ConfigureAwait(false);
        using var stream = await file.OpenReadAsync().AsTask(token).ConfigureAwait(false);
        ImagingDecoder decoder;
        try
        {
            decoder = await createDecoder(stream, token).ConfigureAwait(false);
        }
        catch (COMException error) when (isWebp && error.HResult is not AbortHResult and not OutOfMemoryHResult and not CancelledHResult)
        {
            token.ThrowIfCancellationRequested();
            return new DocumentPreview(DocumentPreviewKind.Details, "WEBP · Windows decoder could not open this file",
                $"The Windows WebP decoder could not open this file (0x{error.HResult:X8}). The decoder may be unusable, or the file may be damaged. Use Open to view the file in another application.");
        }
        if (decoder.FrameCount > 2000) throw new InvalidDataException("Image previews are limited to 2,000 frames.");
        int index = Math.Clamp(page, 1, Math.Max(1, checked((int)decoder.FrameCount))) - 1;
        var frame = await decoder.GetFrameAsync((uint)index).AsTask(token).ConfigureAwait(false);
        if ((long)frame.PixelWidth * frame.PixelHeight > 50_000_000) throw new InvalidDataException("The image exceeds the 50-megapixel preview limit.");
        double scale = Math.Min(1, Math.Min(1600d / frame.OrientedPixelWidth, 2000d / frame.OrientedPixelHeight));
        uint width = (uint)Math.Max(1, frame.OrientedPixelWidth * scale), height = (uint)Math.Max(1, frame.OrientedPixelHeight * scale);
        var pixels = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = (uint)Math.Max(1, frame.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, frame.PixelHeight * scale) }, ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);
        var bitmap = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Pbgra32, null, pixels.DetachPixelData(), (int)width * 4);
        bitmap.Freeze();
        bool animation = decoder.FrameCount > 1 && Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        var resource = animation ? new PreviewResource(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), "image/gif") : null;
        return new DocumentPreview(DocumentPreviewKind.Image, $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {decoder.FrameCount:n0} frames",
            Image: bitmap, Page: index + 1, PageCount: checked((int)decoder.FrameCount), Animation: resource);
    }, token);
}
