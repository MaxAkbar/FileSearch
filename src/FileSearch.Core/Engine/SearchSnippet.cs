using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed record SearchSnippet
{
    public SearchSnippet(
        string text,
        IReadOnlyList<MatchSpan>? highlights = null,
        SourceLocator? locator = null,
        long? contentUnitId = null,
        IReadOnlyList<long>? contentUnitIds = null)
    {
        Text = text ?? string.Empty;
        Highlights = highlights ?? Array.Empty<MatchSpan>();
        Locator = locator;
        ContentUnitId = contentUnitId;
        ContentUnitIds = contentUnitIds ?? Array.Empty<long>();
    }

    public string Text { get; init; }

    public IReadOnlyList<MatchSpan> Highlights { get; init; }

    public SourceLocator? Locator { get; init; }

    public long? ContentUnitId { get; init; }

    public IReadOnlyList<long> ContentUnitIds { get; init; }
}
