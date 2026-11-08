using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Gui.Controls;

namespace FileSearch.Gui.Tests;

public sealed class PdfPreviewOverlayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task NormalizedPolygonsFollowTheWholeImageThroughFitZoomAndScrolling() => OnDispatcherAsync(async () =>
    {
        var overlay = new PdfPreviewOverlay();
        var image = new Image { Source = WhitePage(), Stretch = Stretch.Fill };
        var page = new Grid { Width = 400, Height = 800 };
        page.Children.Add(image); page.Children.Add(overlay);
        var viewbox = new Viewbox { Width = 200, Height = 400, Stretch = Stretch.Uniform, Child = page };
        var scroll = new ScrollViewer
        {
            Width = 300,
            Height = 250,
            Content = viewbox,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = Show(scroll);
        try
        {
            overlay.Update([Box(.1, .1, .3, .2), Box(.5, .7, .8, .8)], 0);
            await LayoutAsync(window);
            Assert.False(overlay.IsHitTestVisible);
            Assert.False(overlay.Focusable);
            Assert.Equal(400, image.ActualWidth);
            Assert.Equal(800, image.ActualHeight);
            AssertBounds(new Rect(.1, .1, .2, .1), overlay.ActiveBounds);
            AssertSameImagePosition(image, overlay, scroll, new Point(80, 120));
            var fitted = Geometries(VisualTreeHelper.GetDrawing(overlay)).ToArray();
            Assert.NotEmpty(fitted);
            Assert.All(fitted, drawing => Assert.True(drawing.Geometry.Bounds.Top < 500,
                "The overlay constructed geometry for a match below the visible portion of the page."));

            viewbox.Width = 800; viewbox.Height = 1600;
            await LayoutAsync(window);
            overlay.SetCurrentMatch(1);
            scroll.ScrollToVerticalOffset(1100);
            scroll.ScrollToHorizontalOffset(200);
            await LayoutAsync(window);
            Assert.True(scroll.VerticalOffset > 1000);
            Assert.Equal(400, overlay.ActualWidth);
            Assert.Equal(800, overlay.ActualHeight);
            AssertSameImagePosition(image, overlay, scroll, new Point(260, 600));
            var late = Geometries(VisualTreeHelper.GetDrawing(overlay)).ToArray();
            Assert.NotEmpty(late);
            Assert.All(late, drawing => Assert.True(drawing.Geometry.Bounds.Top >= 500,
                "Scrolling retained offscreen geometry instead of the visible late occurrence."));
            AssertBounds(new Rect(.5, .7, .3, .1), overlay.ActiveBounds);
            Assert.Equal(400, image.Source.Width);
            Assert.Equal(800, image.Source.Height);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task UpdateOwnsPolygonsAndCurrentOccurrenceHasStrongerMarker() => OnDispatcherAsync(async () =>
    {
        var overlay = new PdfPreviewOverlay { Width = 400, Height = 800 };
        var points = new[] { new Point(.1, .1), new Point(.3, .1), new Point(.3, .2), new Point(.1, .2) };
        var resource = new SolidColorBrush(Colors.Magenta);
        overlay.Resources["Atlas.HitBrush"] = resource;
        overlay.Update([[points], Box(.5, .6, .8, .7)], 0);
        points[0] = new Point(.99, .99);
        var window = Show(overlay);
        try
        {
            await LayoutAsync(window);
            AssertBounds(new Rect(.1, .1, .2, .1), overlay.ActiveBounds);
            var drawings = Geometries(VisualTreeHelper.GetDrawing(overlay)).ToArray();
            Assert.NotEmpty(drawings);
            if (SystemParameters.HighContrast)
            {
                Assert.All(drawings, drawing => Assert.Null(drawing.Brush));
                Assert.All(drawings, drawing => Assert.NotNull(drawing.Pen));
            }
            else
            {
                var first = Render(overlay);
                var active = ColorAt(first, 80, 120);
                var ordinary = ColorAt(first, 260, 520);
                Assert.True(active.A > ordinary.A && ordinary.A > 0);
                Assert.False(resource.IsFrozen);
                resource.Color = Colors.Cyan;
                overlay.SetCurrentMatch(1);
                await LayoutAsync(window);
                var changed = ColorAt(Render(overlay), 80, 120);
                Assert.True(changed.G > 0 && changed.B > 0 && changed.R == 0,
                    "Existing matches did not use the current highlight resource.");
            }
            overlay.Clear(); await LayoutAsync(window);
            Assert.True(overlay.ActiveBounds.IsEmpty);
            Assert.Empty(Geometries(VisualTreeHelper.GetDrawing(overlay)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OcrRegionIsIndependentAndInvalidGeometryCannotReachThePage() => OnDispatcherAsync(async () =>
    {
        var overlay = new PdfPreviewOverlay { Width = 400, Height = 800 };
        overlay.Update([
            [new[] { new Point(double.NaN, .1), new Point(.2, .1), new Point(.2, .2) }],
            [new[] { new Point(-.1, .1), new Point(.2, .1), new Point(.2, .2) }],
            [new[] { new Point(.1, .1), new Point(.1, .1), new Point(.1, .1) }],
        ], 0);
        var ocr = new Rect(.2, .4, .5, .1);
        overlay.UpdateOcrRegion(ocr);
        var window = Show(overlay);
        try
        {
            await LayoutAsync(window);
            Assert.True(overlay.ActiveBounds.IsEmpty);
            Assert.Equal(ocr, overlay.OcrRegion);
            var before = Geometries(VisualTreeHelper.GetDrawing(overlay)).ToArray();
            Assert.NotEmpty(before);
            Assert.All(before, drawing => Assert.Null(drawing.Brush));
            Assert.Contains(before, drawing => drawing.Pen is { } pen && pen.DashStyle.Dashes.SequenceEqual(DashStyles.Dash.Dashes));

            overlay.Clear(); await LayoutAsync(window);
            Assert.Equal(ocr, overlay.OcrRegion);
            Assert.NotEmpty(Geometries(VisualTreeHelper.GetDrawing(overlay)));
            overlay.ClearOcrRegion(); await LayoutAsync(window);
            Assert.Null(overlay.OcrRegion);
            Assert.Empty(Geometries(VisualTreeHelper.GetDrawing(overlay)));

            overlay.UpdateOcrRegion(new Rect(-.01, .2, .3, .2));
            await LayoutAsync(window);
            Assert.Null(overlay.OcrRegion);
            Assert.Empty(Geometries(VisualTreeHelper.GetDrawing(overlay)));
        }
        finally { window.Close(); }
    });

    private static IReadOnlyList<IReadOnlyList<Point>> Box(double left, double top, double right, double bottom) =>
        [new[] { new Point(left, top), new Point(right, top), new Point(right, bottom), new Point(left, bottom) }];

    private static void AssertBounds(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 8); Assert.Equal(expected.Y, actual.Y, 8);
        Assert.Equal(expected.Width, actual.Width, 8); Assert.Equal(expected.Height, actual.Height, 8);
    }

    private static BitmapSource WhitePage()
    {
        var pixels = Enumerable.Repeat((byte)255, 400 * 800 * 4).ToArray();
        var image = BitmapSource.Create(400, 800, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
        image.Freeze();
        return image;
    }

    private static void AssertSameImagePosition(Image image, PdfPreviewOverlay overlay, Visual ancestor, Point point)
    {
        var expected = image.TransformToAncestor(ancestor).Transform(point);
        var actual = overlay.TransformToAncestor(ancestor).Transform(point);
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, .001);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, .001);
    }

    private static IEnumerable<GeometryDrawing> Geometries(Drawing? drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Geometries(child)) yield return descendant;
    }

    private static RenderTargetBitmap Render(PdfPreviewOverlay overlay)
    {
        var bitmap = new RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay);
        return bitmap;
    }

    private static Color ColorAt(RenderTargetBitmap bitmap, int x, int y)
    {
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Width = 460,
            Height = 900,
            Content = content,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        return window;
    }

    private static async Task LayoutAsync(Window window) =>
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);

    private static Task OnDispatcherAsync(Func<Task> action) =>
        StaTestDispatcher.RunAsync(action, TimeSpan.FromSeconds(60), Token);
}
