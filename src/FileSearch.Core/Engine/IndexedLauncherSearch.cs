using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed record LauncherSearchResult(Hit Hit, string Root);

public sealed record LauncherSearchResponse(
    IReadOnlyList<LauncherSearchResult> Results,
    int SkippedLocations = 0,
    bool IsTruncated = false,
    bool TimedOut = false);

/// <summary>
/// Bounded, read-only searches for external launchers. Searches existing, compatible
/// indexes only: never falls back to a disk scan, builds indexes, or starts watchers.
/// </summary>
public sealed class IndexedLauncherSearch(
    IIndexSearch index,
    IIndexMaintenance maintenance,
    IQueryFactory queries)
{
    public async Task<LauncherSearchResponse> SearchAsync(
        string text,
        SearchTarget target,
        int maxResults = 50,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxResults, 100);
        if (target is not (SearchTarget.Content or SearchTarget.FileNames))
            throw new ArgumentOutOfRangeException(nameof(target));

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
            return new LauncherSearchResponse([]);

        var mode = target == SearchTarget.FileNames ? QueryMode.PlainText : QueryMode.Unified;
        var expression = queries.Build(text, mode, caseSensitive: false);
        var results = new List<LauncherSearchResult>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var scannedHits = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        var token = deadline.Token;

        try
        {
            var locations = await maintenance.GetLocationsAsync(token).ConfigureAwait(false);
            foreach (var location in locations)
            {
                token.ThrowIfCancellationRequested();
                var options = location.GetWalkerOptions();
                if (!location.Exists || options is null)
                {
                    skipped++;
                    continue;
                }

                var request = new SearchRequest(
                    expression, [location.Root], options,
                    UseIndex: true, RawQuery: text, Mode: mode, SearchTarget: target);
                var coverage = await index.GetCoverageAsync(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!coverage.IsCovered)
                {
                    skipped++;
                    continue;
                }

                await foreach (var hit in index.SearchAsync(request, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (paths.Add(hit.Path))
                        results.Add(new LauncherSearchResult(hit, location.Root));

                    if (results.Count >= maxResults || ++scannedHits >= maxResults * 20)
                        return new LauncherSearchResponse(results, skipped, IsTruncated: true);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new LauncherSearchResponse(results, skipped, IsTruncated: true, TimedOut: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LauncherSearchResponse(results, skipped);
    }
}
