using System.IO;
using FileSearch.Core.Engine;

namespace FileSearch.Gui.Services;

internal enum SourceOpenTargetKind
{
    VisualStudioCode,
    PdfPageUri,
}

internal sealed record SourceOpenTarget(
    SourceOpenTargetKind Kind,
    string? FileName,
    IReadOnlyList<string> Arguments,
    string? Uri);

internal static class SourceOpenTargetBuilder
{
    public static SourceOpenTarget? TryCreate(string path, Hit hit, string? visualStudioCodePath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var locator = GetLocator(hit);
        if (IsPdf(path) && locator?.Page is > 0)
        {
            return new SourceOpenTarget(
                SourceOpenTargetKind.PdfPageUri,
                null,
                Array.Empty<string>(),
                $"{new Uri(Path.GetFullPath(path)).AbsoluteUri}#page={locator.Page.Value}");
        }

        if (!string.IsNullOrWhiteSpace(visualStudioCodePath) && locator?.StartLine is > 0)
        {
            var column = locator.Column is > 0 ? locator.Column.Value : 1;
            return new SourceOpenTarget(
                SourceOpenTargetKind.VisualStudioCode,
                visualStudioCodePath,
                new[] { "-g", $"{path}:{locator.StartLine.Value}:{column}" },
                null);
        }

        return null;
    }

    private static SourceLocator? GetLocator(Hit hit)
    {
        if (hit.Snippet?.Locator is not null)
            return hit.Snippet.Locator;
        if (hit.Locator is not null)
            return hit.Locator;
        if (hit.Anchor is not null)
            return SourceLocator.FromAnchor(hit.Anchor, hit.LineNumber > 0 ? hit.LineNumber : null);
        return hit.LineNumber > 0 ? new SourceLocator(StartLine: hit.LineNumber, EndLine: hit.LineNumber) : null;
    }

    private static bool IsPdf(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
}
