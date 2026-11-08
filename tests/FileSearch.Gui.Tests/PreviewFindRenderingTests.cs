using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace FileSearch.Gui.Tests;

public sealed class PreviewFindRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task WrappedLongLineHighlightsVisibleAndLateMatchesWithoutPaintingCappedOutHits() => OnDispatcherAsync(async () =>
    {
        // One logical line, over 100,000 characters, with more hits than Find retains.
        const string unit = "hit abcde ";
        string source = string.Concat(Enumerable.Repeat(unit, PreviewTextFind.MaxMatches + 1));
        var result = PreviewTextFind.Find(source, "hit", matchCase: true, Token);
        Assert.Equal(PreviewTextFind.MaxMatches, result.Matches.Count);
        Assert.Equal(PreviewTextFindLimits.MatchCount, result.Limits);
        var editor = CreateEditor(source);
        var renderer = new PreviewFindBackgroundRenderer(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
        var window = Show(editor);
        try
        {
            await LayoutAsync(window, editor);
            renderer.Update(result.Matches);
            var first = Render(renderer, editor);
            AssertPainted(first, editor, result.Matches[0].Start + 1);
            AssertVisibleGeometry(first, editor, maximumRectangles: 256);

            int lateOffset = result.Matches[^1].Start;
            var location = editor.Document.GetLocation(lateOffset);
            editor.ScrollTo(location.Line, location.Column);
            await LayoutAsync(window, editor);
            Assert.True(editor.VerticalOffset > editor.TextArea.TextView.ActualHeight);
            var late = Render(renderer, editor);
            AssertPainted(late, editor, lateOffset + 1);
            AssertVisibleGeometry(late, editor, maximumRectangles: 256);

            // The next literal hit is still in the document and visible at its end,
            // but the bounded result explicitly excludes it from highlighting.
            AssertUnpainted(late, editor, PreviewTextFind.MaxMatches * unit.Length + 1);
            Assert.Equal(source, editor.Text);

            renderer.Clear();
            Assert.Empty(Rectangles(Render(renderer, editor).Visual.Drawing));
            Assert.Equal(source, editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task UnicodeLiteralAcrossCrLfPaintsBothLinesAndKeepsSurroundingTextClear() => OnDispatcherAsync(async () =>
    {
        const string source = "prefix 😀 needle\r\n東京 tail suffix";
        const string query = "😀 needle\r\n東京 tail";
        var editor = CreateEditor(source);
        var renderer = new PreviewFindBackgroundRenderer(editor);
        var window = Show(editor);
        try
        {
            await LayoutAsync(window, editor);
            var result = PreviewTextFind.Find(source, query, matchCase: true, Token);
            Assert.Single(result.Matches);
            renderer.Update(result.Matches);
            var frame = Render(renderer, editor);
            AssertPainted(frame, editor, source.IndexOf("needle", StringComparison.Ordinal) + 1);
            AssertPainted(frame, editor, source.IndexOf("tail", StringComparison.Ordinal) + 1);
            AssertUnpainted(frame, editor, 1);
            AssertUnpainted(frame, editor, source.IndexOf("suffix", StringComparison.Ordinal) + 1);
            AssertVisibleGeometry(frame, editor, maximumRectangles: 4);
            Assert.Equal(2, Rectangles(frame.Visual.Drawing).Select(rectangle => rectangle.Top).Distinct().Count());
            Assert.Equal(source, editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RedrawUsesCurrentBrushAndIgnoresRangesOutsideCurrentDocument() => OnDispatcherAsync(async () =>
    {
        const string source = "plain needle plain";
        var editor = CreateEditor(source);
        var renderer = new PreviewFindBackgroundRenderer(editor);
        var window = Show(editor);
        try
        {
            await LayoutAsync(window, editor);
            renderer.Update(PreviewTextFind.Find(source, "needle", matchCase: true, Token).Matches);
            var first = Render(renderer, editor);
            int offset = source.IndexOf("needle", StringComparison.Ordinal) + 1;
            AssertPainted(first, editor, offset);
            if (SystemParameters.HighContrast) AssertHighContrastOutline(first);
            else Assert.Equal(Colors.Magenta, ColorAt(first, editor, offset));

            editor.Resources["Atlas.HitBrush"] = FrozenBrush(Colors.Cyan);
            var changed = Render(renderer, editor);
            if (SystemParameters.HighContrast) AssertHighContrastOutline(changed);
            else Assert.Equal(Colors.Cyan, ColorAt(changed, editor, offset));

            // A late snapshot can be stale after a document replacement. Invalid ranges
            // must not reach AvalonEdit or paint unrelated text in the shorter document.
            editor.Text = "short";
            await LayoutAsync(window, editor);
            Assert.Empty(Rectangles(Render(renderer, editor).Visual.Drawing));
            renderer.Update([
                new PreviewTextMatch(-1, 2),
                new PreviewTextMatch(0, 0),
                new PreviewTextMatch(1, int.MaxValue),
                new PreviewTextMatch(8, 2),
                new PreviewTextMatch(int.MaxValue, 1),
            ]);
            Assert.Empty(Rectangles(Render(renderer, editor).Visual.Drawing));
            Assert.Equal("short", editor.Text);
        }
        finally { window.Close(); }
    });

    private static TextEditor CreateEditor(string text)
    {
        var editor = new TextEditor
        {
            Text = text,
            IsReadOnly = true,
            WordWrap = true,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.Resources["Atlas.HitBrush"] = FrozenBrush(Colors.Magenta);
        return editor;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Window Show(TextEditor editor)
    {
        var window = new Window
        {
            Content = editor,
            Width = 460,
            Height = 260,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        return window;
    }

    private static async Task LayoutAsync(Window window, TextEditor editor)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.True(editor.TextArea.TextView.VisualLinesValid);
        Assert.True(editor.TextArea.TextView.ActualWidth > 0);
        Assert.True(editor.TextArea.TextView.ActualHeight > 0);
    }

    private sealed record RenderedLayer(DrawingVisual Visual, RenderTargetBitmap Bitmap);

    private static RenderedLayer Render(PreviewFindBackgroundRenderer renderer, TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) renderer.Draw(view, context);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return new RenderedLayer(visual, bitmap);
    }

    private static Point PointAt(TextEditor editor, int offset)
    {
        var view = editor.TextArea.TextView;
        var point = view.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(offset)), VisualYPosition.LineMiddle)
            - view.ScrollOffset;
        // Probe inside the character after this caret, away from antialiased edges.
        return new Point(point.X + 2, point.Y);
    }

    private static Color ColorAt(RenderedLayer frame, TextEditor editor, int offset)
    {
        var point = PointAt(editor, offset);
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        Assert.InRange(x, 0, frame.Bitmap.PixelWidth - 1);
        Assert.InRange(y, 0, frame.Bitmap.PixelHeight - 1);
        var pixel = new byte[4];
        frame.Bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void AssertPainted(RenderedLayer frame, TextEditor editor, int offset)
    {
        if (SystemParameters.HighContrast)
        {
            AssertHighContrastOutline(frame);
            Assert.Contains(Rectangles(frame.Visual.Drawing), rectangle => rectangle.Contains(PointAt(editor, offset)));
        }
        else Assert.True(ColorAt(frame, editor, offset).A > 0, $"Expected a Find highlight at UTF-16 offset {offset}.");
    }

    private static void AssertUnpainted(RenderedLayer frame, TextEditor editor, int offset)
    {
        if (SystemParameters.HighContrast)
            Assert.DoesNotContain(Rectangles(frame.Visual.Drawing), rectangle => rectangle.Contains(PointAt(editor, offset)));
        else Assert.Equal(0, ColorAt(frame, editor, offset).A);
    }

    private static void AssertHighContrastOutline(RenderedLayer frame)
    {
        var drawings = GeometryDrawings(frame.Visual.Drawing).ToArray();
        Assert.NotEmpty(drawings);
        Assert.All(drawings, drawing =>
        {
            Assert.Null(drawing.Brush);
            Assert.NotNull(drawing.Pen);
            Assert.Equal(1, drawing.Pen.Thickness);
            Assert.Equal(System.Windows.SystemColors.WindowTextBrush.Color,
                Assert.IsType<SolidColorBrush>(drawing.Pen.Brush).Color);
        });
    }

    private static void AssertVisibleGeometry(RenderedLayer frame, TextEditor editor, int maximumRectangles)
    {
        var view = editor.TextArea.TextView;
        var viewport = new Rect(0, 0, view.ActualWidth, view.ActualHeight);
        var rectangles = Rectangles(frame.Visual.Drawing).ToArray();
        Assert.InRange(rectangles.Length, 1, maximumRectangles);
        Assert.All(rectangles, rectangle => Assert.True(rectangle.IntersectsWith(viewport),
            $"Find emitted offscreen geometry {rectangle} outside {viewport}."));
    }

    private static IEnumerable<Rect> Rectangles(Drawing? drawing) =>
        GeometryDrawings(drawing).Select(geometry => geometry.Geometry.Bounds);

    private static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing? drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var childGeometry in GeometryDrawings(child)) yield return childGeometry;
    }

    private static async Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
    }
}
