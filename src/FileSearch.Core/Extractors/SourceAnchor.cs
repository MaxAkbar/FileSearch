namespace FileSearch.Core.Extractors;

public enum SourceAnchorKind
{
    Text,
    Pdf,
    Word,
    PowerPoint,
    Excel,
    Email,
    Archive,
    Epub,
    OpenDocument,
    ImageOcr,
}

public sealed record SourceAnchor(
    SourceAnchorKind Kind,
    string DisplayText,
    int? Line = null,
    int? Column = null,
    int? Page = null,
    string? Section = null,
    string? Sheet = null,
    string? Cell = null,
    string? MemberPath = null,
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    int? SourceWidth = null,
    int? SourceHeight = null)
{
    public static SourceAnchor ImageOcrRegion(
        int x,
        int y,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight)
    {
        var display = sourceWidth > 0 && sourceHeight > 0
            ? $"OCR region x{x} y{y} {width}x{height} of {sourceWidth}x{sourceHeight}"
            : $"OCR region x{x} y{y} {width}x{height}";

        return new SourceAnchor(
            SourceAnchorKind.ImageOcr,
            display,
            X: x,
            Y: y,
            Width: width,
            Height: height,
            SourceWidth: sourceWidth > 0 ? sourceWidth : null,
            SourceHeight: sourceHeight > 0 ? sourceHeight : null);
    }

    public static SourceAnchor PdfPage(int page) =>
        new(
            SourceAnchorKind.Pdf,
            page > 0 ? $"page {page}" : "PDF page",
            Page: page > 0 ? page : null);

    public static SourceAnchor PdfOcrRegion(
        int page,
        int x,
        int y,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight)
    {
        var display = sourceWidth > 0 && sourceHeight > 0
            ? $"page {page} OCR region x{x} y{y} {width}x{height} of {sourceWidth}x{sourceHeight}"
            : $"page {page} OCR region x{x} y{y} {width}x{height}";

        return new SourceAnchor(
            SourceAnchorKind.Pdf,
            display,
            Page: page > 0 ? page : null,
            X: x,
            Y: y,
            Width: width,
            Height: height,
            SourceWidth: sourceWidth > 0 ? sourceWidth : null,
            SourceHeight: sourceHeight > 0 ? sourceHeight : null);
    }

    public static SourceAnchor EmbeddedOcrRegion(
        SourceAnchorKind kind,
        string label,
        string memberPath,
        int x,
        int y,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        int? page = null,
        string? section = null,
        string? sheet = null)
    {
        var prefix = string.IsNullOrWhiteSpace(label) ? "embedded image" : label.Trim();
        var display = sourceWidth > 0 && sourceHeight > 0
            ? $"{prefix} OCR region x{x} y{y} {width}x{height} of {sourceWidth}x{sourceHeight}"
            : $"{prefix} OCR region x{x} y{y} {width}x{height}";

        return new SourceAnchor(
            kind,
            display,
            Page: page,
            Section: section,
            Sheet: sheet,
            MemberPath: string.IsNullOrWhiteSpace(memberPath) ? null : memberPath,
            X: x,
            Y: y,
            Width: width,
            Height: height,
            SourceWidth: sourceWidth > 0 ? sourceWidth : null,
            SourceHeight: sourceHeight > 0 ? sourceHeight : null);
    }
}
