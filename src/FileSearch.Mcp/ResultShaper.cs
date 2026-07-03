using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Mcp;

/// <summary>
/// Every limit that keeps tool output model-sized lives here: result-count
/// caps, per-file caps, line windowing around the first match, and the
/// collect-with-timeout loop that turns an unbounded hit stream into a
/// bounded document with honest <c>truncated</c>/<c>timedOut</c> flags.
/// </summary>
internal static class ResultShaper
{
    public const int DefaultMaxResults = 50;
    public const int MaxResultsCeiling = 500;
    public const int MaxResultsPerFileCeiling = 100;
    public const int DefaultTimeoutSeconds = 30;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 120;
    public const int MaxHitLineChars = 320;

    public static int ClampMaxResults(int? requested) =>
        Math.Clamp(requested ?? DefaultMaxResults, 1, MaxResultsCeiling);

    public static int ClampMaxResultsPerFile(int? requested) =>
        Math.Clamp(requested ?? 0, 0, MaxResultsPerFileCeiling);

    public static TimeSpan ClampTimeout(int? requestedSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(requestedSeconds ?? DefaultTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));

    /// <summary>
    /// Drains a hit stream until the result cap or the deadline. A deadline
    /// expiry returns what was found so far (partial results beat an error);
    /// client-initiated cancellation still propagates.
    /// </summary>
    public static async Task<CollectedHits> CollectAsync(
        IAsyncEnumerable<Hit> stream,
        int maxResults,
        int maxResultsPerFile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var hits = new List<Hit>(Math.Min(maxResults, 64));
        var perFile = maxResultsPerFile > 0
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : null;
        var total = 0;
        var truncated = false;
        var timedOut = false;

        try
        {
            await foreach (var hit in stream.WithCancellation(timeoutSource.Token).ConfigureAwait(false))
            {
                total++;
                if (perFile is not null)
                {
                    perFile.TryGetValue(hit.Path, out var count);
                    if (count >= maxResultsPerFile)
                    {
                        truncated = true;
                        continue;
                    }

                    perFile[hit.Path] = count + 1;
                }

                hits.Add(hit);
                if (hits.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }

        return new CollectedHits(hits, total, truncated, timedOut);
    }

    public static SearchHitDocument ToDocument(Hit hit)
    {
        var (line, lineTruncated) = ShapeLine(hit.LineContent, hit.Highlights, MaxHitLineChars);
        return new SearchHitDocument(
            hit.Path,
            hit.LineNumber,
            line,
            lineTruncated,
            hit.Kind.ToString(),
            hit.Route.ToString(),
            hit.Score,
            hit.SizeBytes,
            hit.ModifiedUtc,
            hit.Anchor?.DisplayText);
    }

    /// <summary>
    /// Caps a matched line, windowing around the first highlight so the match
    /// itself always survives truncation (think minified JS or log lines).
    /// </summary>
    public static (string Line, bool Truncated) ShapeLine(
        string content,
        IReadOnlyList<MatchSpan> highlights,
        int maxChars)
    {
        if (content.Length <= maxChars)
            return (content.Trim(), false);

        var anchor = highlights.Count > 0 ? highlights[0].Start : 0;
        anchor = Math.Clamp(anchor, 0, content.Length - 1);
        var start = Math.Clamp(anchor - maxChars / 4, 0, content.Length - maxChars);
        var window = content.Substring(start, maxChars).Trim();

        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + maxChars < content.Length ? "…" : string.Empty;
        return (prefix + window + suffix, true);
    }
}

internal sealed record CollectedHits(
    IReadOnlyList<Hit> Hits,
    int TotalMatches,
    bool Truncated,
    bool TimedOut);
