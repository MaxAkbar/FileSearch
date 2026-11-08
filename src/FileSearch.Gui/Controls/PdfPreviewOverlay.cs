using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace FileSearch.Gui.Controls;

/// <summary>Noninteractive highlights in normalized coordinates of the complete PDF page bitmap.</summary>
internal sealed class PdfPreviewOverlay : FrameworkElement
{
    private static readonly SolidColorBrush FallbackMatchBrush = FrozenBrush(Color.FromRgb(255, 206, 84));
    private static readonly SolidColorBrush FallbackActiveBrush = FrozenBrush(Color.FromRgb(225, 105, 18));
    private static readonly SolidColorBrush FallbackOcrBrush = FrozenBrush(Color.FromRgb(0, 105, 170));
    private static readonly Rect PageBounds = new(0, 0, 1, 1);
    private Polygon[][] _matches = [];
    private int _currentMatch = -1;
    private ScrollViewer? _scroll;
    private Rect? _ocrRegion;

    public PdfPreviewOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Rect ActiveBounds => _currentMatch >= 0 && _currentMatch < _matches.Length
        ? Union(_matches[_currentMatch]) : Rect.Empty;

    public Rect? OcrRegion => _ocrRegion;

    public void Update(IReadOnlyList<IReadOnlyList<IReadOnlyList<Point>>> matches, int activeMatchIndex)
    {
        ArgumentNullException.ThrowIfNull(matches);
        Dispatcher.VerifyAccess();
        // Geometry is immutable to the caller once supplied. Invalid polygons cannot
        // escape the page or inject nonfinite coordinates into WPF's renderer.
        _matches = matches.Select(match => match.Select(CreatePolygon).Where(polygon => polygon is not null)
            .Select(polygon => polygon!).ToArray()).ToArray();
        SetCurrentMatch(activeMatchIndex);
    }

    public void SetCurrentMatch(int activeMatchIndex)
    {
        Dispatcher.VerifyAccess();
        _currentMatch = activeMatchIndex >= 0 && activeMatchIndex < _matches.Length ? activeMatchIndex : -1;
        InvalidateVisual();
    }

    public void Clear()
    {
        Dispatcher.VerifyAccess();
        _matches = [];
        _currentMatch = -1;
        InvalidateVisual();
    }

    public void UpdateOcrRegion(Rect? normalizedRegion)
    {
        Dispatcher.VerifyAccess();
        _ocrRegion = normalizedRegion is { } region && IsPageRectangle(region) ? region : null;
        InvalidateVisual();
    }

    public void ClearOcrRegion() => UpdateOcrRegion(null);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0 || (_matches.Length == 0 && _ocrRegion is null)) return;
        bool highContrast = SystemParameters.HighContrast;
        double scale = ViewportScale();
        var ordinary = highContrast ? null : Translucent(TryFindResource("Atlas.HitBrush") as Brush ?? FallbackMatchBrush, 0.34);
        var active = highContrast ? null : Translucent(TryFindResource("Atlas.HitBrush") as Brush ?? FallbackMatchBrush, 0.56);
        var activePen = FrozenPen(TryFindResource("Atlas.WarmBrush") as Brush ?? FallbackActiveBrush, 1.8 / scale);
        var systemOuter = highContrast ? FrozenPen(System.Windows.SystemColors.WindowBrush, 3.5 / scale) : null;
        var systemInner = highContrast ? FrozenPen(System.Windows.SystemColors.WindowTextBrush, 1.3 / scale) : null;
        Rect visible = VisibleBounds();

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        try
        {
            // Paint the active occurrence last so an adjacent or overlapping region
            // cannot cover its stronger marker.
            for (int index = 0; index < _matches.Length; index++)
                if (index != _currentMatch) DrawMatch(_matches[index], ordinary, highContrast ? systemInner : null);
            if (_currentMatch >= 0 && _currentMatch < _matches.Length)
                DrawMatch(_matches[_currentMatch], active, highContrast ? systemInner : activePen);

            if (_ocrRegion is { } region && region.IntersectsWith(visible))
            {
                var rectangle = ToPixels(region);
                if (highContrast) drawingContext.DrawRectangle(null, systemOuter, rectangle);
                var ocrPen = FrozenPen(highContrast ? System.Windows.SystemColors.WindowTextBrush :
                    TryFindResource("Atlas.AccentBrush") as Brush ?? FallbackOcrBrush, 1.8 / scale, dashed: true);
                drawingContext.DrawRectangle(null, ocrPen, rectangle);
            }
        }
        finally { drawingContext.Pop(); }

        void DrawMatch(Polygon[] polygons, Brush? fill, Pen? pen)
        {
            foreach (var polygon in polygons)
            {
                if (!polygon.Bounds.IntersectsWith(visible)) continue;
                var geometry = PixelGeometry(polygon.Points);
                if (highContrast) drawingContext.DrawGeometry(null, systemOuter, geometry);
                drawingContext.DrawGeometry(fill, pen, geometry);
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _scroll = Ancestor<ScrollViewer>(this);
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs args) => InvalidateVisual();
    private void OnSystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(InvalidateVisual);
    }

    private Rect VisibleBounds()
    {
        var presenter = Ancestor<ScrollContentPresenter>(this);
        if (presenter is null) return PageBounds;
        var inverse = TransformToAncestor(presenter).Inverse;
        if (inverse is null) return PageBounds;
        var visible = inverse.TransformBounds(new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight));
        var normalized = new Rect(visible.X / ActualWidth, visible.Y / ActualHeight,
            visible.Width / ActualWidth, visible.Height / ActualHeight);
        normalized.Intersect(PageBounds);
        return normalized;
    }

    private double ViewportScale()
    {
        var presenter = Ancestor<ScrollContentPresenter>(this);
        if (presenter is null) return 1;
        var transform = TransformToAncestor(presenter);
        var origin = transform.Transform(new Point());
        var unit = transform.Transform(new Point(1, 0));
        double scale = (unit - origin).Length;
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    private StreamGeometry PixelGeometry(Point[] normalized)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPixels(normalized[0]), isFilled: true, isClosed: true);
            for (int index = 1; index < normalized.Length; index++) context.LineTo(ToPixels(normalized[index]), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Point ToPixels(Point point) => new(point.X * ActualWidth, point.Y * ActualHeight);
    private Rect ToPixels(Rect rectangle) => new(rectangle.X * ActualWidth, rectangle.Y * ActualHeight,
        rectangle.Width * ActualWidth, rectangle.Height * ActualHeight);

    private static Polygon? CreatePolygon(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return null;
        var copy = new Point[points.Count];
        var bounds = Rect.Empty;
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < -1e-8 || point.X > 1 + 1e-8 || point.Y < -1e-8 || point.Y > 1 + 1e-8) return null;
            copy[index] = new Point(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));
            bounds.Union(copy[index]);
        }
        return bounds.Width > 0 && bounds.Height > 0 ? new(copy, bounds) : null;
    }

    private static bool IsPageRectangle(Rect rectangle) => !rectangle.IsEmpty &&
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) &&
        rectangle.Width > 0 && rectangle.Height > 0 && rectangle.X >= 0 && rectangle.Y >= 0 && rectangle.Right <= 1 && rectangle.Bottom <= 1;

    private static Rect Union(Polygon[] polygons)
    {
        var bounds = Rect.Empty;
        foreach (var polygon in polygons) bounds.Union(polygon.Bounds);
        return bounds;
    }

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null && current is not T) current = VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private static Brush Translucent(Brush brush, double opacity)
    {
        var result = brush.CloneCurrentValue();
        result.Opacity = Math.Min(result.Opacity, opacity);
        result.Freeze();
        return result;
    }

    private static Pen FrozenPen(Brush brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush.CloneCurrentValue(), thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record Polygon(Point[] Points, Rect Bounds);
}
