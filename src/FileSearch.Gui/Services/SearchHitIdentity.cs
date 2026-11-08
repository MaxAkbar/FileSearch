using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;

namespace FileSearch.Gui.Services;

internal readonly record struct SearchHitIdentity(string ResultKey, HitKind Kind, int Line, string Text,
    int? Page, int? Slide, string? Member, string? Section, string? Sheet, string? Cell,
    string? Table, long? Row, string? Column, string? RecordKey, int? X, int? Y, int? StartLine, int? EndLine, long? Unit)
{
    internal static SearchHitIdentity Create(Hit hit)
    {
        var snippet = hit.Snippet?.Locator;
        var locator = hit.Locator;
        var anchor = hit.Anchor;
        var database = snippet?.Database ?? locator?.Database ?? anchor?.Database;
        return new(hit.ResultKey.ToUpperInvariant(), hit.Kind, hit.LineNumber, hit.Kind == HitKind.Metadata ? "" : hit.LineContent,
            snippet?.Page ?? locator?.Page ?? anchor?.Page, snippet?.Slide ?? locator?.Slide ?? (anchor?.Kind == SourceAnchorKind.PowerPoint ? anchor.Page : null),
            snippet?.ArchiveMember ?? locator?.ArchiveMember ?? anchor?.MemberPath,
            snippet?.Section ?? locator?.Section ?? anchor?.Section, snippet?.Sheet ?? locator?.Sheet ?? anchor?.Sheet,
            snippet?.CellRange ?? locator?.CellRange ?? anchor?.Cell,
            database?.Table, database?.RowNumber, database?.Column, database?.RowKey,
            snippet?.X ?? locator?.X ?? anchor?.X, snippet?.Y ?? locator?.Y ?? anchor?.Y,
            hit.LineNumber > 0 ? null : snippet?.StartLine ?? locator?.StartLine ?? anchor?.Line,
            hit.LineNumber > 0 ? null : snippet?.EndLine ?? locator?.EndLine,
            hit.Kind == HitKind.Content && hit.LineNumber <= 0 && database is null &&
                (snippet?.StartLine ?? locator?.StartLine ?? anchor?.Line) is not > 0 ? hit.ContentUnitId : null);
    }
}
