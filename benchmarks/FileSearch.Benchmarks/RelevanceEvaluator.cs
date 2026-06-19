using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;

namespace FileSearch.Benchmarks;

internal sealed class RelevanceEvaluator
{
    public async Task<RelevanceSummary> EvaluateAsync(
        IFileIndex index,
        CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var evaluated = 0;
        var reciprocalRankTotal = 0d;
        var ndcgTotal = 0d;
        var recallAt20Total = 0d;
        var zeroResultCount = 0;
        var topResultCount = 0;
        var topResultQueryCount = 0;

        foreach (var query in manifest.Queries)
        {
            if (query.RelevantPaths.Count == 0)
                continue;

            var root = string.Equals(query.RootKind, "metadata", StringComparison.OrdinalIgnoreCase)
                ? manifest.MetadataRoot
                : manifest.ContentRoot;
            var hits = await BenchmarkSearch.SearchAllAsync(index, root, query.Query, cancellationToken).ConfigureAwait(false);
            var rankedPaths = hits
                .Select(static hit => hit.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var relevant = query.RelevantPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

            evaluated++;
            if (rankedPaths.Length == 0)
            {
                zeroResultCount++;
                continue;
            }

            var firstRelevantIndex = Array.FindIndex(rankedPaths, relevant.Contains);
            if (firstRelevantIndex >= 0)
                reciprocalRankTotal += 1d / (firstRelevantIndex + 1);

            if (!string.IsNullOrWhiteSpace(query.ExpectedTopPath))
            {
                topResultQueryCount++;
                if (string.Equals(rankedPaths[0], query.ExpectedTopPath, StringComparison.OrdinalIgnoreCase))
                    topResultCount++;
            }

            ndcgTotal += NdcgAt(rankedPaths, relevant, 10);
            recallAt20Total += rankedPaths.Take(20).Count(relevant.Contains) / (double)relevant.Count;
        }

        if (evaluated == 0)
            return new RelevanceSummary(0, 0, 0, 0, 0, 0);

        return new RelevanceSummary(
            reciprocalRankTotal / evaluated,
            ndcgTotal / evaluated,
            recallAt20Total / evaluated,
            zeroResultCount / (double)evaluated,
            topResultQueryCount == 0 ? 0 : topResultCount / (double)topResultQueryCount,
            evaluated);
    }

    private static double NdcgAt(string[] rankedPaths, HashSet<string> relevant, int k)
    {
        var dcg = 0d;
        for (var i = 0; i < Math.Min(k, rankedPaths.Length); i++)
        {
            if (relevant.Contains(rankedPaths[i]))
                dcg += 1d / Math.Log2(i + 2);
        }

        var idealCount = Math.Min(k, relevant.Count);
        var idealDcg = 0d;
        for (var i = 0; i < idealCount; i++)
            idealDcg += 1d / Math.Log2(i + 2);

        return idealDcg == 0 ? 0 : dcg / idealDcg;
    }
}
