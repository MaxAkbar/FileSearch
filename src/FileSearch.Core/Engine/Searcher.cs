using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

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

    public Searcher(
        IFileWalker walker,
        IExtractorRegistry extractors,
        SearchOptions? options = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _options = options ?? new SearchOptions();
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var channel = Channel.CreateBounded<Hit>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

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
                        var extractor = _extractors.GetFor(path);
                        if (extractor is null) return;

                        try
                        {
                            await ProcessFileAsync(path, extractor, request.Expression, channel.Writer, token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            // Swallow per-file errors so one bad file doesn't kill the run.
                            // A logging hook could be added here.
                        }
                    }).ConfigureAwait(false);
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

    private async Task ProcessFileAsync(
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
    }
}
