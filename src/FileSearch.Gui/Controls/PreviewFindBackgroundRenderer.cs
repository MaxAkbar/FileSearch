using System.Windows;
using System.Windows.Media;
using FileSearch.Gui.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace FileSearch.Gui.Controls;

/// <summary>Highlights literal Find matches without creating geometry for offscreen text.</summary>
internal sealed class PreviewFindBackgroundRenderer(TextEditor editor) : IBackgroundRenderer
{
    private static readonly SolidColorBrush FallbackBrush = CreateFallbackBrush();
    private readonly TextEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    private PreviewTextMatch[] _matches = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void Update(IReadOnlyList<PreviewTextMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        // PreviewTextFind supplies sorted, non-overlapping UTF-16 ranges. Own the snapshot.
        _matches = matches.ToArray();
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Clear()
    {
        _matches = [];
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_matches.Length == 0 || !textView.VisualLinesValid || textView.Document is not { } document ||
            textView.ActualWidth <= 0 || textView.ActualHeight <= 0)
            return;

        // Resolve on each draw so existing matches follow theme/high-contrast changes.
        bool highContrast = SystemParameters.HighContrast;
        var brush = highContrast ? null : _editor.TryFindResource("Atlas.HitBrush") as Brush ?? FallbackBrush;
        // An outline preserves the editor's glyph colors instead of putting them on
        // an unrelated system selection background in high-contrast themes.
        var pen = highContrast ? new Pen(System.Windows.SystemColors.WindowTextBrush, 1) : null;
        pen?.Freeze();
        var viewport = new Rect(0, 0, textView.ActualWidth, textView.ActualHeight);
        double viewportTop = textView.VerticalOffset;
        double viewportBottom = viewportTop + textView.ActualHeight;

        drawingContext.PushClip(new RectangleGeometry(viewport));
        try
        {
            foreach (var line in textView.VisualLines)
            {
                double top = Math.Max(viewportTop, line.VisualTop);
                double bottom = Math.Min(viewportBottom, line.VisualTop + line.Height);
                if (bottom <= top)
                    continue;

                // One realized VisualLine can contain thousands of wrapped rows. Restrict
                // the offset search to its visible rows before asking AvalonEdit for geometry.
                var firstRow = line.GetTextLineByVisualYPosition(top);
                var lastRow = line.GetTextLineByVisualYPosition(Math.BitDecrement(bottom));
                int startColumn = Math.Min(line.VisualLength, line.GetTextLineVisualStartColumn(firstRow));
                int endColumn = Math.Min(line.VisualLength,
                    line.GetTextLineVisualStartColumn(lastRow) + lastRow.Length);
                int visibleStart = line.FirstDocumentLine.Offset + line.GetRelativeOffset(startColumn);
                int visibleEnd = endColumn == line.VisualLength
                    ? line.LastDocumentLine.Offset + line.LastDocumentLine.TotalLength
                    : line.FirstDocumentLine.Offset + line.GetRelativeOffset(endColumn);
                visibleEnd = Math.Min(visibleEnd, document.TextLength);

                int index = LowerBound(visibleStart);
                // Non-overlapping matches allow at most one match to cross this boundary.
                if (index > 0)
                    index--;
                for (; index < _matches.Length; index++)
                {
                    var match = _matches[index];
                    if (match.Start >= visibleEnd)
                        break;
                    if (match.Start < 0 || match.Length <= 0 || match.Start > document.TextLength - match.Length)
                        continue;
                    int start = Math.Max(match.Start, visibleStart);
                    int end = Math.Min(match.End, visibleEnd);
                    if (end <= start)
                        continue;

                    var segment = new TextSegment { StartOffset = start, Length = end - start };
                    foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, false))
                    {
                        if (rectangle.IntersectsWith(viewport))
                            drawingContext.DrawRectangle(brush, pen, rectangle);
                    }
                }
            }
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private int LowerBound(int offset)
    {
        int low = 0;
        int high = _matches.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_matches[middle].Start < offset)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static SolidColorBrush CreateFallbackBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xCE, 0x54));
        brush.Freeze();
        return brush;
    }
}
