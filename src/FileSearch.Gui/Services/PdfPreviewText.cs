using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using Point = System.Windows.Point;

namespace FileSearch.Gui.Services;

internal sealed record PdfPreviewGlyph(int Start, int Length, IReadOnlyList<Point> Polygon);

internal sealed record PdfPreviewPage(string Text, IReadOnlyList<PdfPreviewGlyph> Glyphs,
    bool IsPartial, string? UnavailableReason, string? CoverageReason = null)
{
    public bool CanFind => UnavailableReason is null && Glyphs.Count > 0;
}

internal sealed record PdfPreviewMatch(PreviewTextMatch Range, IReadOnlyList<IReadOnlyList<Point>> Regions);

internal sealed record PdfPreviewFindResult(IReadOnlyList<PdfPreviewMatch> Matches, bool IsComplete, string? LimitReason);

/// <summary>Bounded embedded text and actual glyph polygons for the displayed PDF page.</summary>
internal static class PdfPreviewText
{
    private static readonly ReadOnlyCollection<Point> EmptyPolygon = Array.AsReadOnly(Array.Empty<Point>());
    public const int MaxGlyphs = 100_000;
    public const int MaxTextCharacters = 250_000;

    public static PdfPreviewPage Read(string path, int pageNumber, double renderedPageWidth,
        double renderedPageHeight, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(renderedPageWidth) || !double.IsFinite(renderedPageHeight) ||
            renderedPageWidth <= 0 || renderedPageHeight <= 0)
            return Unavailable("PDF text geometry is unavailable for this rendered page.");
        if (new FileInfo(path).Length > DocumentPreviewService.MaxPdfBytes)
            return Unavailable("PDF text previews are limited to 100 MB.");

        using var document = PdfDocument.Open(path);
        token.ThrowIfCancellationRequested();
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return Unavailable("The requested PDF page is unavailable.");
        Page page;
        try { page = document.GetPage(pageNumber); }
        catch (ArgumentOutOfRangeException)
        { return Unavailable("PDF page geometry or UserUnit is unsupported. Use Matches or Open."); }
        token.ThrowIfCancellationRequested();
        if (page.Dictionary.TryGet(NameToken.UserUnit, out IToken unit) &&
            (unit is not NumericToken number || number.Data != 1))
            return Unavailable("PDF text highlighting is unavailable for this page's custom UserUnit. Use Matches or Open.");
        if (!CropInsideMedia(page.CropBox.Bounds, page.MediaBox.Bounds))
            return Unavailable("PDF crop geometry extends beyond the media box. Use Matches or Open.");
        double width = page.Width, height = page.Height;
        int rotation = page.Rotation.Value;
        // PdfPig's initial page matrix has already applied CropBox translation and
        // /Rotate to Letter coordinates. Applying either transform again misaligns
        // cropped/rotated pages. WinRT dimensions can use a different uniform unit.
        double pageAspect = width / height, renderedAspect = renderedPageWidth / renderedPageHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(pageAspect) || !double.IsFinite(renderedAspect) || pageAspect <= 0 || renderedAspect <= 0 ||
            rotation is not (0 or 90 or 180 or 270) ||
            Math.Abs(pageAspect / renderedAspect - 1) > 0.001)
            return Unavailable("PDF text geometry does not match the displayed page. Use Matches or Open.");

        var text = new StringBuilder();
        var glyphs = new List<PdfPreviewGlyph>();
        Letter? previous = null;
        bool interrupted = false, partial = false, unsupportedGeometry = false;
        var letters = page.Letters;
        for (int index = 0; index < letters.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index == MaxGlyphs) { partial = true; break; }
            var letter = letters[index];
            if (string.IsNullOrEmpty(letter.Value)) continue;
            var polygon = GlyphPolygon(letter.GlyphRectangle, width, height);
            bool whitespace = string.IsNullOrWhiteSpace(letter.Value);
            if (polygon.Count == 0 && !whitespace)
            {
                interrupted = true; previous = null;
                // Cropped-out glyphs are outside the displayed-page scope. Invalid
                // geometry can also occur in unusual fonts; do not invent a box.
                unsupportedGeometry |= HasVisibleOrInvalidGeometry(letter.GlyphRectangle, width, height);
                continue;
            }

            string separator = interrupted ? "\n" : previous is null ? "" : Separator(previous, letter);
            interrupted = false;
            if (text.Length > MaxTextCharacters - letter.Value.Length - separator.Length)
            { partial = true; break; }
            if (text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(letter.Value[0])) text.Append(separator);
            int start = text.Length;
            text.Append(letter.Value);
            glyphs.Add(new(start, letter.Value.Length, polygon));
            previous = letter;
        }
        token.ThrowIfCancellationRequested();
        if (glyphs.Count == 0 || glyphs.All(glyph => glyph.Polygon.Count == 0))
            return Unavailable("This PDF page has no supported embedded text. Use Matches for available OCR hits.");
        string? coverageReason = partial ? "Only the first 100,000 glyphs or 250,000 characters of this PDF page were searched." : null;
        if (unsupportedGeometry) coverageReason = (coverageReason is null ? "" : coverageReason + " ") +
            "Some embedded text has unsupported glyph geometry and was omitted.";
        return new(text.ToString(), glyphs.AsReadOnly(), partial || unsupportedGeometry, null, coverageReason);
    }

    public static PdfPreviewFindResult Find(PdfPreviewPage page, string query, bool matchCase, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(page);
        token.ThrowIfCancellationRequested();
        if (!page.CanFind) return new([], false, page.UnavailableReason);
        var found = PreviewTextFind.Find(page.Text, query, matchCase, token);
        var matches = new List<PdfPreviewMatch>(found.Matches.Count);
        foreach (var range in found.Matches)
        {
            token.ThrowIfCancellationRequested();
            var regions = new List<IReadOnlyList<Point>>();
            int index = LowerBound(page.Glyphs, range.Start);
            if (index > 0) index--;
            for (; index < page.Glyphs.Count && page.Glyphs[index].Start < range.End; index++)
            {
                var glyph = page.Glyphs[index];
                if (glyph.Start + glyph.Length > range.Start && glyph.Polygon.Count > 0) regions.Add(glyph.Polygon);
            }
            matches.Add(new(range, regions.AsReadOnly()));
        }
        string? reason = page.IsPartial ? page.CoverageReason ?? "Only the first 100,000 glyphs or 250,000 characters of this PDF page were searched." : null;
        if (found.LimitReason is { } limit) reason = reason is null ? limit : reason + " " + limit;
        return new(matches.AsReadOnly(), !page.IsPartial && found.IsComplete, reason);
    }

    private static PdfPreviewPage Unavailable(string reason) => new("", [], false, reason);

    private static string Separator(Letter previous, Letter current)
    {
        double dx = previous.EndBaseLine.X - previous.StartBaseLine.X;
        double dy = previous.EndBaseLine.Y - previous.StartBaseLine.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 0.0001) return "";
        dx /= length; dy /= length;
        double offsetX = current.StartBaseLine.X - previous.EndBaseLine.X;
        double offsetY = current.StartBaseLine.Y - previous.EndBaseLine.Y;
        double scale = Math.Max(1, Math.Max(previous.PointSize, current.PointSize));
        double along = offsetX * dx + offsetY * dy;
        double across = -offsetX * dy + offsetY * dx;
        if (!double.IsFinite(along) || !double.IsFinite(across) || Math.Abs(across) > scale * 0.3 || along < -scale * 0.5)
            return "\n";
        return along > scale * 0.15 ? " " : "";
    }

    private static ReadOnlyCollection<Point> GlyphPolygon(PdfRectangle rectangle, double width, double height)
    {
        var points = new List<Point>();
        foreach (var corner in new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopRight, rectangle.TopLeft })
        {
            var point = new Point(corner.X / width, 1 - corner.Y / height);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return EmptyPolygon;
            points.Add(point);
        }
        points = Clip(points, point => point.X >= 0, (a, b) => IntersectX(a, b, 0));
        points = Clip(points, point => point.X <= 1, (a, b) => IntersectX(a, b, 1));
        points = Clip(points, point => point.Y >= 0, (a, b) => IntersectY(a, b, 0));
        points = Clip(points, point => point.Y <= 1, (a, b) => IntersectY(a, b, 1));
        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Y - points[index].Y * next.X;
        }
        return points.Count < 3 || Math.Abs(area) < 1e-14 ? EmptyPolygon : points.AsReadOnly();
    }

    private static bool CropInsideMedia(PdfRectangle crop, PdfRectangle media)
    {
        var cropPoints = new[] { crop.BottomLeft, crop.BottomRight, crop.TopRight, crop.TopLeft };
        var mediaPoints = new[] { media.BottomLeft, media.BottomRight, media.TopRight, media.TopLeft };
        if (cropPoints.Concat(mediaPoints).Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        double left = mediaPoints.Min(point => point.X), right = mediaPoints.Max(point => point.X);
        double bottom = mediaPoints.Min(point => point.Y), top = mediaPoints.Max(point => point.Y);
        const double tolerance = 0.001;
        return cropPoints.All(point => point.X >= left - tolerance && point.X <= right + tolerance &&
            point.Y >= bottom - tolerance && point.Y <= top + tolerance);
    }

    private static List<Point> Clip(List<Point> input, Func<Point, bool> inside, Func<Point, Point, Point> intersect)
    {
        var output = new List<Point>();
        if (input.Count == 0) return output;
        var previous = input[^1];
        bool previousInside = inside(previous);
        foreach (var current in input)
        {
            bool currentInside = inside(current);
            if (currentInside != previousInside) output.Add(intersect(previous, current));
            if (currentInside) output.Add(current);
            previous = current; previousInside = currentInside;
        }
        return output;
    }

    private static Point IntersectX(Point a, Point b, double x) => new(x, a.Y + (b.Y - a.Y) * ((x - a.X) / (b.X - a.X)));
    private static Point IntersectY(Point a, Point b, double y) => new(a.X + (b.X - a.X) * ((y - a.Y) / (b.Y - a.Y)), y);

    private static bool HasVisibleOrInvalidGeometry(PdfRectangle rectangle, double width, double height)
    {
        var corners = new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopRight, rectangle.TopLeft };
        if (corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return true;
        return corners.Max(point => point.X) >= 0 && corners.Min(point => point.X) <= width &&
            corners.Max(point => point.Y) >= 0 && corners.Min(point => point.Y) <= height;
    }

    private static int LowerBound(IReadOnlyList<PdfPreviewGlyph> glyphs, int offset)
    {
        int low = 0, high = glyphs.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (glyphs[middle].Start < offset) low = middle + 1; else high = middle;
        }
        return low;
    }
}
