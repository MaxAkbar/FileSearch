using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public interface ISnippetGenerator
{
    Task<SearchSnippet> GenerateAsync(
        SearchRequest request,
        Hit hit,
        CancellationToken cancellationToken);
}

public sealed class ContentUnitSnippetGenerator : ISnippetGenerator
{
    private const int ContextBefore = 1;
    private const int ContextAfter = 1;

    private readonly IContentUnitReader _contentUnits;

    public ContentUnitSnippetGenerator(IContentUnitReader contentUnits)
    {
        _contentUnits = contentUnits ?? throw new ArgumentNullException(nameof(contentUnits));
    }

    public async Task<SearchSnippet> GenerateAsync(
        SearchRequest request,
        Hit hit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hit);

        if (hit.ContentUnitId is not { } contentUnitId)
            return CreateFallbackSnippet(request.Expression, hit);

        var units = await _contentUnits
            .GetNeighboringUnitsAsync(contentUnitId, ContextBefore, ContextAfter, cancellationToken)
            .ConfigureAwait(false);

        if (units.Count == 0)
            return CreateFallbackSnippet(request.Expression, hit);

        var text = string.Join(Environment.NewLine, units.Select(unit => unit.Text));
        var center = units.FirstOrDefault(unit => unit.Id == contentUnitId);
        return new SearchSnippet(
            text,
            CollectHighlights(request.Expression, text),
            center?.Locator ?? hit.Locator,
            contentUnitId,
            units.Select(unit => unit.Id).ToArray());
    }

    private static SearchSnippet CreateFallbackSnippet(Query query, Hit hit) =>
        new(
            hit.LineContent,
            hit.Highlights.Count > 0 ? hit.Highlights : CollectHighlights(query, hit.LineContent),
            hit.Locator,
            hit.ContentUnitId,
            hit.ContentUnitId is { } id ? new[] { id } : Array.Empty<long>());

    private static List<MatchSpan> CollectHighlights(Query query, string text)
    {
        var highlights = new List<MatchSpan>();
        query.CollectHighlights(text, highlights);
        return highlights;
    }
}
