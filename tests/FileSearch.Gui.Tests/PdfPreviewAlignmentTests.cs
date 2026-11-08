using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class PdfPreviewAlignmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchPdfAlignment-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public PdfPreviewAlignmentTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(90, false, false)]
    [InlineData(180, false, false)]
    [InlineData(270, false, false)]
    [InlineData(0, true, false)]
    [InlineData(90, true, false)]
    [InlineData(180, true, false)]
    [InlineData(270, true, false)]
    [InlineData(0, false, true)]
    [InlineData(90, false, true)]
    [InlineData(180, false, true)]
    [InlineData(270, false, true)]
    [InlineData(0, true, true)]
    [InlineData(90, true, true)]
    [InlineData(180, true, true)]
    [InlineData(270, true, true)]
    public async Task NativeGlyphOverlayEnclosesActualRenderedInkAtEveryPageRotation(int rotation, bool crop, bool rotateText)
    {
        string path = PdfFindFixtures.Write(Path.Combine(_root, "alignment.pdf"), rotation, crop, rotateText);
        var preview = await DocumentPreviewService.LoadPdfPageAsync(path, 1, Token);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(preview.Image);
        var page = PdfPreviewText.Read(path, 1, preview.PdfPageSize.Width, preview.PdfPageSize.Height, Token);
        Assert.True(page.CanFind, page.UnavailableReason);
        var result = PdfPreviewText.Find(page, "H", matchCase: true, Token);
        var match = Assert.Single(result.Matches);
        var region = Assert.Single(match.Regions);
        Assert.Equal(4, region.Count);
        Assert.All(region, point =>
        {
            Assert.InRange(point.X, 0, 1);
            Assert.InRange(point.Y, 0, 1);
        });

        var pixels = Pixels(bitmap);
        var ink = new List<Point>();
        for (int y = 0; y < bitmap.PixelHeight; y++)
            for (int x = 0; x < bitmap.PixelWidth; x++)
            {
                int offset = (y * bitmap.PixelWidth + x) * 4;
                // The fixture's sole black glyph is H; its other text and corner
                // fiducials use saturated colors and cannot satisfy this predicate.
                if (pixels[offset + 3] > 200 && pixels[offset] < 80 && pixels[offset + 1] < 80 && pixels[offset + 2] < 80)
                    ink.Add(new Point(x + .5, y + .5));
            }
        Assert.True(ink.Count > 200, "The independent Windows PDF raster did not contain the fixture's dark glyph ink.");
        var projected = region.Select(point => new Point(point.X * bitmap.PixelWidth, point.Y * bitmap.PixelHeight)).ToArray();
        var bounds = Bounds(projected);
        var inkBounds = Bounds(ink);
        var tolerance = bounds;
        tolerance.Inflate(4, 4);
        double coverage = ink.Count(point => tolerance.Contains(point)) / (double)ink.Count;
        Assert.True(coverage >= .99, $"Projected glyph misses rendered ink: rotation={rotation}, crop={crop}, text90={rotateText}, coverage={coverage:P2}, overlay={bounds}, ink={inkBounds}.");
        Assert.InRange(bounds.Width * bounds.Height / (inkBounds.Width * inkBounds.Height), .5, 3);
        Assert.InRange(Math.Abs(bounds.X + bounds.Width / 2 - inkBounds.X - inkBounds.Width / 2), 0, Math.Max(6, inkBounds.Width * .3));
        Assert.InRange(Math.Abs(bounds.Y + bounds.Height / 2 - inkBounds.Y - inkBounds.Height / 2), 0, Math.Max(6, inkBounds.Height * .3));

        // A matching text operator entirely beyond CropBox must not produce an
        // invisible match or distort the location of the visible H occurrence.
        if (crop) Assert.Empty(PdfPreviewText.Find(page, "OUTSIDE", matchCase: true, Token).Matches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task VisibleCropRetainsItsFourDistinctCornerFiducials(int rotation)
    {
        string path = PdfFindFixtures.Write(Path.Combine(_root, "corners.pdf"), rotation);
        var preview = await DocumentPreviewService.LoadPdfPageAsync(path, 1, Token);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(preview.Image);
        double expectedAspect = rotation is 90 or 270
            ? PdfFindFixtures.CropHeight / PdfFindFixtures.CropWidth
            : PdfFindFixtures.CropWidth / PdfFindFixtures.CropHeight;
        Assert.InRange(Math.Abs(bitmap.PixelWidth / (double)bitmap.PixelHeight - expectedAspect), 0, .005);
        var pixels = Pixels(bitmap);
        foreach (var (x, y, color) in new[]
        {
            (8d, 8d, Colors.Red),
            (8d, PdfFindFixtures.CropHeight - 8, Colors.Lime),
            (PdfFindFixtures.CropWidth - 8, 8d, Colors.Blue),
            (PdfFindFixtures.CropWidth - 8, PdfFindFixtures.CropHeight - 8, Colors.Magenta),
        })
        {
            double rawX = x / PdfFindFixtures.CropWidth, rawY = y / PdfFindFixtures.CropHeight;
            // Independent expected page rotation: raw PDF coordinates are bottom-up;
            // these are top-left image coordinates after clockwise /Rotate.
            var point = rotation switch
            {
                90 => new Point(rawY, rawX),
                180 => new Point(1 - rawX, rawY),
                270 => new Point(1 - rawY, 1 - rawX),
                _ => new Point(rawX, 1 - rawY),
            };
            int column = Math.Clamp((int)(point.X * bitmap.PixelWidth), 0, bitmap.PixelWidth - 1);
            int row = Math.Clamp((int)(point.Y * bitmap.PixelHeight), 0, bitmap.PixelHeight - 1);
            int offset = (row * bitmap.PixelWidth + column) * 4;
            Assert.InRange(Math.Abs(pixels[offset] - color.B), 0, 8);
            Assert.InRange(Math.Abs(pixels[offset + 1] - color.G), 0, 8);
            Assert.InRange(Math.Abs(pixels[offset + 2] - color.R), 0, 8);
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static Rect Bounds(IReadOnlyCollection<Point> points)
    {
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        double right = points.Max(point => point.X), bottom = points.Max(point => point.Y);
        return new Rect(left, top, right - left, bottom - top);
    }
}
