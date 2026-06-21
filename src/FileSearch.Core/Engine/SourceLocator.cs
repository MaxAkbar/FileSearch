using FileSearch.Core.Extractors;

namespace FileSearch.Core.Engine;

public sealed record SourceLocator(
    int? Page = null,
    int? Slide = null,
    string? Sheet = null,
    string? CellRange = null,
    int? StartLine = null,
    int? EndLine = null,
    int? Column = null,
    string? Section = null,
    string? ArchiveMember = null,
    string? DisplayText = null,
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    int? SourceWidth = null,
    int? SourceHeight = null)
{
    public static SourceLocator FromAnchor(SourceAnchor? anchor, int? lineNumber = null)
    {
        if (anchor is null)
            return FromLine(lineNumber);

        return new SourceLocator(
            Page: anchor.Page,
            Slide: anchor.Kind == SourceAnchorKind.PowerPoint ? anchor.Page : null,
            Sheet: anchor.Sheet,
            CellRange: anchor.Cell,
            StartLine: anchor.Line ?? lineNumber,
            EndLine: anchor.Line ?? lineNumber,
            Column: anchor.Column,
            Section: anchor.Section,
            ArchiveMember: anchor.MemberPath,
            DisplayText: anchor.DisplayText,
            X: anchor.X,
            Y: anchor.Y,
            Width: anchor.Width,
            Height: anchor.Height,
            SourceWidth: anchor.SourceWidth,
            SourceHeight: anchor.SourceHeight);
    }

    private static SourceLocator FromLine(int? lineNumber) =>
        new(
            StartLine: lineNumber > 0 ? lineNumber : null,
            EndLine: lineNumber > 0 ? lineNumber : null,
            DisplayText: lineNumber > 0 ? $"line {lineNumber}" : null);
}
