using FileSearch.Core.Extractors;
using Windows.Media.Ocr;

namespace FileSearch.WindowsOcr;

internal static class WindowsOcrAnchorBuilder
{
    public readonly record struct OcrRegion(int X, int Y, int Width, int Height);

    public static SourceAnchor? ImageRegion(OcrLine line, int imageWidth, int imageHeight) =>
        BuildRegion(line, imageWidth, imageHeight, SourceAnchor.ImageOcrRegion);

    public static SourceAnchor? PdfRegion(OcrLine line, int page, int imageWidth, int imageHeight) =>
        BuildRegion(
            line,
            imageWidth,
            imageHeight,
            (x, y, width, height, sourceWidth, sourceHeight) =>
                SourceAnchor.PdfOcrRegion(page, x, y, width, height, sourceWidth, sourceHeight));

    public static OcrRegion? Region(OcrLine line, int imageWidth, int imageHeight)
    {
        if (line.Words.Count == 0)
            return null;

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            minX = Math.Min(minX, rect.X);
            minY = Math.Min(minY, rect.Y);
            maxX = Math.Max(maxX, rect.X + rect.Width);
            maxY = Math.Max(maxY, rect.Y + rect.Height);
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY))
            return null;

        var x = ClampToInt(Math.Floor(minX), 0, imageWidth);
        var y = ClampToInt(Math.Floor(minY), 0, imageHeight);
        var right = ClampToInt(Math.Ceiling(maxX), x, imageWidth);
        var bottom = ClampToInt(Math.Ceiling(maxY), y, imageHeight);
        return new OcrRegion(
            x,
            y,
            Math.Max(0, right - x),
            Math.Max(0, bottom - y));
    }

    private static SourceAnchor? BuildRegion(
        OcrLine line,
        int imageWidth,
        int imageHeight,
        Func<int, int, int, int, int, int, SourceAnchor> create)
    {
        var region = Region(line, imageWidth, imageHeight);
        if (region is null)
            return null;

        return create(
            region.Value.X,
            region.Value.Y,
            region.Value.Width,
            region.Value.Height,
            imageWidth,
            imageHeight);
    }

    private static int ClampToInt(double value, int min, int max)
    {
        if (double.IsNaN(value))
            return min;

        var clamped = Math.Clamp(value, min, max);
        return checked((int)clamped);
    }
}
