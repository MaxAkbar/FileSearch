using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Engine;

/// <summary>
/// Orchestrates the search pipeline:
///   walker → per-file extractor → query match → bounded channel → caller.
/// Depends only on abstractions (<see cref="IFileWalker"/>,
/// <see cref="IExtractorRegistry"/>) per the dependency-inversion principle.
/// </summary>
public sealed class Searcher : ISearcher
{
    private readonly IFileWalker _walker;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions _options;
    private readonly ILogger _logger;

    public Searcher(
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? options = null,
        ILogger<Searcher>? logger = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _options = options ?? new SearchOptions();
        _logger = logger ?? NullLogger<Searcher>.Instance;
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = Channel.CreateBounded<Hit>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        long filesEnumerated = 0;
        long filesProcessed = 0;
        long filesMatched = 0;
        long filesSkipped = 0;
        long filesFailed = 0;
        long lastProgressTimestamp = 0;
        const long ProgressIntervalMs = 100;

        void PublishProgress(bool force = false)
        {
            if (request.Progress is null)
                return;

            // Per-file callbacks marshal to the caller (often a UI thread);
            // throttle to ~10/s and publish exact totals once at the end.
            if (!force)
            {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastProgressTimestamp);
                if (now - last < ProgressIntervalMs ||
                    Interlocked.CompareExchange(ref lastProgressTimestamp, now, last) != last)
                {
                    return;
                }
            }

            request.Progress(new SearchProgress(
                Interlocked.Read(ref filesEnumerated),
                Interlocked.Read(ref filesProcessed),
                Interlocked.Read(ref filesMatched),
                Interlocked.Read(ref filesSkipped),
                Interlocked.Read(ref filesFailed)));
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                var paths = _walker.Enumerate(request.Roots, request.WalkerOptions, cancellationToken);
                await Parallel.ForEachAsync(
                    paths,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (path, token) =>
                    {
                        Interlocked.Increment(ref filesEnumerated);
                        var extractor = _extractors.GetFor(path);
                        if (extractor is null)
                        {
                            Interlocked.Increment(ref filesSkipped);
                            PublishProgress();
                            return;
                        }

                        try
                        {
                            var hits = await ProcessFileAsync(path, extractor, request.Expression, channel.Writer, token)
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref filesProcessed);
                            if (hits > 0)
                                Interlocked.Increment(ref filesMatched);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Per-file errors must not kill the run; locked or
                            // malformed files are routine, so log at Debug.
                            Interlocked.Increment(ref filesFailed);
                            _logger.LogDebug(ex, "Search failed for file {Path}.", path);
                        }
                        finally
                        {
                            PublishProgress();
                        }
                    }).ConfigureAwait(false);

                PublishProgress(force: true);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var hit in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return hit;

        await producer.ConfigureAwait(false); // surface producer-side exceptions
    }

    private async Task<int> ProcessFileAsync(
        string path,
        ITextExtractor extractor,
        Query query,
        ChannelWriter<Hit> writer,
        CancellationToken token)
    {
        int hitsForFile = 0;
        var highlightBuffer = new List<MatchSpan>(4);

        await foreach (var line in extractor.ExtractAsync(path, token).ConfigureAwait(false))
        {
            if (!query.IsMatch(line.Content)) continue;

            highlightBuffer.Clear();
            query.CollectHighlights(line.Content, highlightBuffer);

            var hit = new Hit(
                path,
                line.Number,
                line.Content,
                highlightBuffer.ToArray());

            await writer.WriteAsync(hit, token).ConfigureAwait(false);

            if (++hitsForFile >= _options.MaxHitsPerFile) break;
        }

        return hitsForFile;
    }
}
