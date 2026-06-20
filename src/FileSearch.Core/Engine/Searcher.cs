using System;
using System.Collections.Generic;
using System.IO;
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

        if (request.Expression is UnifiedQuery { HasUnavailableSemantic: true })
        {
            request.Status?.Invoke(UnifiedQuery.SemanticUnavailableMessage);
            yield break;
        }

        if (request.SearchTarget != SearchTarget.Content)
        {
            await foreach (var hit in SearchNamesAsync(request, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        if (request.Expression is UnifiedQuery unified && !unified.HasContentCriteria)
        {
            await foreach (var hit in SearchUnifiedFileMetadataAsync(request, unified, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        // The producer runs on its own linked token so that a consumer that
        // stops enumerating early (break/dispose) can shut it down instead of
        // abandoning workers that still hold file handles.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producerToken = producerCts.Token;

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

        // Count at the walker, not in the worker body — FilesEnumerated means
        // "files the walker has produced", not "files a worker has started".
        IEnumerable<string> CountedPaths()
        {
            foreach (var path in _walker.Enumerate(request.Roots, request.WalkerOptions, producerToken))
            {
                Interlocked.Increment(ref filesEnumerated);
                yield return path;
            }
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    CountedPaths(),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                        CancellationToken = producerToken,
                    },
                    async (path, token) =>
                    {
                        var extractor = _extractors.GetFor(path);
                        if (extractor is null)
                        {
                            Interlocked.Increment(ref filesSkipped);
                            PublishProgress();
                            return;
                        }

                        if (request.Expression is UnifiedQuery unified &&
                            !unified.MatchesLiveFile(request.Roots, path, extractor.ExtractorId))
                        {
                            Interlocked.Increment(ref filesSkipped);
                            PublishProgress();
                            return;
                        }

                        try
                        {
                            var hits = await ProcessFileAsync(
                                    path,
                                    extractor,
                                    request.Expression,
                                    request.WalkerOptions,
                                    channel.Writer,
                                    token)
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
        }, producerToken);

        try
        {
            await foreach (var hit in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return hit;
        }
        finally
        {
            // Runs on normal completion AND when the consumer stops early
            // (break disposes the iterator, which skips code after the loop
            // but not this finally). Joining the producer here guarantees no
            // worker still holds a file open when the caller continues, and
            // surfaces real producer faults; its cancellation is not a fault.
            producerCts.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<int> ProcessFileAsync(
        string path,
        ITextExtractor extractor,
        Query query,
        WalkerOptions options,
        ChannelWriter<Hit> writer,
        CancellationToken token)
    {
        int hitsForFile = 0;
        var highlightBuffer = new List<MatchSpan>(4);
        var context = new TextExtractionContext(options.EnableOcr);

        await foreach (var line in extractor.ExtractWithContextAsync(path, context, token).ConfigureAwait(false))
        {
            if (!query.IsMatch(line.Content)) continue;

            highlightBuffer.Clear();
            query.CollectHighlights(line.Content, highlightBuffer);

            var hit = new Hit(
                path,
                line.Number,
                line.Content,
                highlightBuffer.ToArray(),
                Anchor: line.Anchor);

            await writer.WriteAsync(hit, token).ConfigureAwait(false);

            if (++hitsForFile >= _options.MaxHitsPerFile) break;
        }

        return hitsForFile;
    }

    private async IAsyncEnumerable<Hit> SearchUnifiedFileMetadataAsync(
        SearchRequest request,
        UnifiedQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

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

        foreach (var path in _walker.Enumerate(request.Roots, request.WalkerOptions, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref filesEnumerated);
            Hit? matchedHit = null;
            try
            {
                Interlocked.Increment(ref filesProcessed);
                var extractor = _extractors.GetFor(path);
                if (!query.MatchesLiveFile(request.Roots, path, extractor?.ExtractorId))
                {
                    Interlocked.Increment(ref filesSkipped);
                    PublishProgress();
                    continue;
                }

                var root = FindContainingRoot(request.Roots, path);
                var relative = GetRelativePath(root, path);
                var displayText = string.IsNullOrWhiteSpace(relative) || relative == "."
                    ? Path.GetFileName(path)
                    : relative;
                var (sizeBytes, modifiedUtc) = TryReadMetadata(path, isDirectory: false);
                matchedHit = new Hit(
                    path,
                    0,
                    $"File match: {displayText}",
                    Array.Empty<MatchSpan>(),
                    HitKind.Metadata,
                    700,
                    sizeBytes,
                    modifiedUtc);
                Interlocked.Increment(ref filesMatched);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref filesFailed);
                _logger.LogDebug(ex, "Unified metadata search failed for file {Path}.", path);
            }

            PublishProgress();
            if (matchedHit is not null)
                yield return matchedHit;
        }

        PublishProgress(force: true);
    }

    private async IAsyncEnumerable<Hit> SearchNamesAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var includeFiles = request.SearchTarget is SearchTarget.FileNames or SearchTarget.FileAndFolderNames;
        var includeFolders = request.SearchTarget is SearchTarget.FolderNames or SearchTarget.FileAndFolderNames;
        var highlightBuffer = new List<MatchSpan>(4);
        long itemsEnumerated = 0;
        long itemsProcessed = 0;
        long itemsMatched = 0;
        long itemsSkipped = 0;
        long itemsFailed = 0;
        long lastProgressTimestamp = 0;
        const long ProgressIntervalMs = 100;

        void PublishProgress(bool force = false)
        {
            if (request.Progress is null)
                return;

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
                Interlocked.Read(ref itemsEnumerated),
                Interlocked.Read(ref itemsProcessed),
                Interlocked.Read(ref itemsMatched),
                Interlocked.Read(ref itemsSkipped),
                Interlocked.Read(ref itemsFailed)));
        }

        foreach (var root in request.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Interlocked.Increment(ref itemsSkipped);
                PublishProgress();
                continue;
            }

            var normalizedRoot = Path.GetFullPath(root);

            if (includeFolders)
            {
                foreach (var directory in EnumerateDirectoriesForNameSearch(normalizedRoot, request.WalkerOptions, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref itemsEnumerated);
                    Hit? matchedHit = null;
                    try
                    {
                        Interlocked.Increment(ref itemsProcessed);
                        if (!TryCreateNameHit(directory, normalizedRoot, isDirectory: true, request.Expression, highlightBuffer, out var hit))
                        {
                            PublishProgress();
                            continue;
                        }

                        Interlocked.Increment(ref itemsMatched);
                        matchedHit = hit;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref itemsFailed);
                        _logger.LogDebug(ex, "Folder name search failed for {Path}.", directory);
                    }

                    PublishProgress();
                    if (matchedHit is not null)
                        yield return matchedHit;
                }
            }

            if (!includeFiles)
                continue;

            foreach (var path in _walker.Enumerate(new[] { normalizedRoot }, request.WalkerOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref itemsEnumerated);
                Hit? matchedHit = null;
                try
                {
                    Interlocked.Increment(ref itemsProcessed);
                    if (!TryCreateNameHit(path, normalizedRoot, isDirectory: false, request.Expression, highlightBuffer, out var hit))
                    {
                        PublishProgress();
                        continue;
                    }

                    Interlocked.Increment(ref itemsMatched);
                    matchedHit = hit;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref itemsFailed);
                    _logger.LogDebug(ex, "File name search failed for {Path}.", path);
                }

                PublishProgress();
                if (matchedHit is not null)
                    yield return matchedHit;
            }
        }

        PublishProgress(force: true);
    }

    private static IEnumerable<string> EnumerateDirectoriesForNameSearch(
        string root,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        if (ShouldIncludeDirectoryResult(root, isRoot: true, options))
            yield return root;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            if (!options.Recursive && depth > 0)
                continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldEnterDirectory(child, options))
                    continue;

                if (ShouldIncludeDirectoryResult(child, isRoot: false, options))
                    yield return child;

                if (options.Recursive || depth == 0)
                    stack.Push((child, depth + 1));
            }
        }
    }

    private static bool ShouldEnterDirectory(string path, WalkerOptions options)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (options.ExcludeDirectories.Contains(info.Name))
                return false;

            var skipAttributes = FileAttributes.ReparsePoint;
            if (!options.IncludeHidden)
                skipAttributes |= FileAttributes.Hidden | FileAttributes.System;

            return (info.Attributes & skipAttributes) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldIncludeDirectoryResult(string path, bool isRoot, WalkerOptions options)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!isRoot && options.ExcludeDirectories.Contains(info.Name))
                return false;

            if (!isRoot && !options.IncludeHidden &&
                (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                return false;
            }

            if (options.ModifiedAfterUtc is { } after && info.LastWriteTimeUtc < after)
                return false;
            if (options.ModifiedBeforeUtc is { } before && info.LastWriteTimeUtc > before)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateNameHit(
        string path,
        string root,
        bool isDirectory,
        Query query,
        List<MatchSpan> highlightBuffer,
        out Hit hit)
    {
        var name = GetItemName(path, isDirectory);
        var relativePath = GetRelativePath(root, path);
        var score = 0d;
        string? displayText = null;

        if (query.IsMatch(name))
        {
            displayText = name;
            score = 900;
        }
        else if (!string.Equals(relativePath, name, StringComparison.OrdinalIgnoreCase) &&
                 query.IsMatch(relativePath))
        {
            displayText = relativePath;
            score = 600;
        }
        else if (query.IsMatch(path))
        {
            displayText = path;
            score = 300;
        }

        if (displayText is null)
        {
            hit = null!;
            return false;
        }

        var line = isDirectory
            ? $"Folder name match: {displayText}"
            : $"File name match: {displayText}";
        highlightBuffer.Clear();
        query.CollectHighlights(line, highlightBuffer);

        var (sizeBytes, modifiedUtc) = TryReadMetadata(path, isDirectory);
        hit = new Hit(
            path,
            0,
            line,
            highlightBuffer.ToArray(),
            HitKind.Metadata,
            score,
            sizeBytes,
            modifiedUtc);
        return true;
    }

    private static string GetItemName(string path, bool isDirectory)
    {
        var trimmed = isDirectory
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string GetRelativePath(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private static string FindContainingRoot(IReadOnlyList<string> roots, string path)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            try
            {
                var normalizedRoot = Path.GetFullPath(root);
                var normalizedPath = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
                if (relative.Length > 0 &&
                    relative != "." &&
                    !relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative))
                {
                    return normalizedRoot;
                }
            }
            catch
            {
            }
        }

        return roots.Count > 0 ? roots[0] : string.Empty;
    }

    private static (long? SizeBytes, DateTime? ModifiedUtc) TryReadMetadata(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                return directory.Exists
                    ? (null, directory.LastWriteTimeUtc)
                    : (null, null);
            }

            var file = new FileInfo(path);
            return file.Exists
                ? (file.Length, file.LastWriteTimeUtc)
                : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }
}
