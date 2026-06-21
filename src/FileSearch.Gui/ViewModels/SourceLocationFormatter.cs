using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;

namespace FileSearch.Gui.ViewModels;

internal static class SourceLocationFormatter
{
    public static string Format(SourceAnchor? anchor, SourceLocator? locator)
    {
        if (!string.IsNullOrWhiteSpace(anchor?.DisplayText))
            return anchor.DisplayText;

        if (locator is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(locator.DisplayText))
            return locator.DisplayText;

        var parts = new List<string>();
        AddNumber(parts, "page", locator.Page);
        AddNumber(parts, "slide", locator.Slide);
        AddSheet(parts, locator);
        AddLine(parts, locator);
        AddNumber(parts, "column", locator.Column);
        AddText(parts, locator.Section);
        AddText(parts, locator.ArchiveMember);
        AddRegion(parts, locator);
        return string.Join(", ", parts);
    }

    private static void AddNumber(List<string> parts, string label, int? value)
    {
        if (value is > 0)
            parts.Add($"{label} {value.Value}");
    }

    private static void AddSheet(List<string> parts, SourceLocator locator)
    {
        if (string.IsNullOrWhiteSpace(locator.Sheet))
            return;

        parts.Add(string.IsNullOrWhiteSpace(locator.CellRange)
            ? locator.Sheet
            : $"{locator.Sheet}!{locator.CellRange}");
    }

    private static void AddLine(List<string> parts, SourceLocator locator)
    {
        if (locator.StartLine is not > 0)
            return;

        if (locator.EndLine is > 0 && locator.EndLine != locator.StartLine)
            parts.Add($"lines {locator.StartLine}-{locator.EndLine}");
        else
            parts.Add($"line {locator.StartLine}");
    }

    private static void AddText(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value);
    }

    private static void AddRegion(List<string> parts, SourceLocator locator)
    {
        if (locator.X is not { } x ||
            locator.Y is not { } y ||
            locator.Width is not { } width ||
            locator.Height is not { } height)
        {
            return;
        }

        var source = locator.SourceWidth is { } sourceWidth && locator.SourceHeight is { } sourceHeight
            ? $" of {sourceWidth}x{sourceHeight}"
            : string.Empty;
        parts.Add($"region x{x} y{y} {width}x{height}{source}");
    }
}
