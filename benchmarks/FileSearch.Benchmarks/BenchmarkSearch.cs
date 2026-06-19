using System.Diagnostics;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Benchmarks;

internal static class BenchmarkSearch
{
    public static SearchRequest CreateRequest(string root, string query, WalkerOptions options) =>
        new(
            new TermQuery(query),
            new[] { root },
            options,
            UseIndex: true,
            RawQuery: query,
            Mode: QueryMode.PlainText);

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
        var stopwatch = Stopwatch.StartNew();
        var hits = 0;
        await foreach (var _ in index.SearchAsync(CreateRequest(root, query, BenchmarkIndexFactory.IndexOptions), cancellationToken)
                           .ConfigureAwait(false))
        {
            hits++;
        }

        stopwatch.Stop();
        return (stopwatch.Elapsed.TotalMilliseconds, hits);
    }

    public static async Task<double> TimeToFirstResultAsync(
        IFileIndex index,
        string root,
        string query,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await foreach (var _ in index.SearchAsync(CreateRequest(root, query, BenchmarkIndexFactory.IndexOptions), cancellationToken)
                           .ConfigureAwait(false))
        {
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
