using System.Diagnostics;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Benchmarks;

internal static class BenchmarkSearch
{
    public static SearchRequest CreateRequest(string root, string query, WalkerOptions options, bool useIndex = true) =>
        new(
            new TermQuery(query),
            new[] { root },
            options,
            UseIndex: useIndex,
            RawQuery: query,
            Mode: QueryMode.PlainText);

    public static SearchRequest CreateRegexRequest(string root, string pattern, WalkerOptions options, bool useIndex = true) =>
        new(
            new RegexQuery(pattern),
            new[] { root },
            options,
            UseIndex: useIndex,
            RawQuery: pattern,
            Mode: QueryMode.Regex);

    public static async Task<IReadOnlyList<Hit>> SearchAllAsync(
        IFileIndex index,
        string root,
        string query,
        CancellationToken cancellationToken)
    {
        var hits = new List<Hit>();
        await foreach (var hit in index.SearchAsync(CreateRequest(root, query, BenchmarkIndexFactory.IndexOptions), cancellationToken)
                           .ConfigureAwait(false))
        {
            hits.Add(hit);
        }

        return hits;
    }

    public static async Task<(double Milliseconds, int HitCount)> TimeSearchAsync(
        IFileIndex index,
        string root,
        string query,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(root, query, BenchmarkIndexFactory.IndexOptions);
        return await TimeAsync(() => index.SearchAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    public static async Task<double> TimeToFirstResultAsync(
        IFileIndex index,
        string root,
        string query,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(root, query, BenchmarkIndexFactory.IndexOptions);
        return await TimeToFirstAsync(() => index.SearchAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Times a full enumeration of any hit stream (live or indexed).</summary>
    public static async Task<(double Milliseconds, int HitCount)> TimeAsync(Func<IAsyncEnumerable<Hit>> search)
    {
        var stopwatch = Stopwatch.StartNew();
        var hits = 0;
        await foreach (var _ in search().ConfigureAwait(false))
        {
            hits++;
        }

        stopwatch.Stop();
        return (stopwatch.Elapsed.TotalMilliseconds, hits);
    }

    /// <summary>Times a hit stream up to its first yielded hit.</summary>
    public static async Task<double> TimeToFirstAsync(Func<IAsyncEnumerable<Hit>> search)
    {
        var stopwatch = Stopwatch.StartNew();
        await foreach (var _ in search().ConfigureAwait(false))
        {
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Counts distinct file paths in a hit stream (parity checks).</summary>
    public static async Task<int> CountDistinctPathsAsync(IAsyncEnumerable<Hit> hits)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var hit in hits.ConfigureAwait(false))
        {
            paths.Add(hit.Path);
        }

        return paths.Count;
    }
}
