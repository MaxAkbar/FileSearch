using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ImagingDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace FileSearch.Gui.Services;

public static class ImagePreviewService
{
    public static Task<DocumentPreview> LoadAsync(string path, int page, CancellationToken token) => Task.Run(async () =>
    {
        if (new FileInfo(path).Length > DocumentPreviewService.MaxImageBytes) throw new InvalidDataException("Image previews are limited to 32 MB.");
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token).ConfigureAwait(false);
        using var stream = await file.OpenReadAsync().AsTask(token).ConfigureAwait(false);
        var decoder = await ImagingDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
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
