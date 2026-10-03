using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Volumes;

/// <summary>
/// Serves file, folder, and file-and-folder name searches from the drive
/// name index when every root lies on an indexed NTFS volume and the
/// request allows index use; everything else goes to the wrapped searcher.
/// Results are meant to equal the live name search: the same folders are
/// skipped (hidden or system unless requested, excluded names, and for
/// folder results reparse points), the same filters apply, and matching and
/// hit formatting are shared with <see cref="Searcher"/>.
/// </summary>
/// <remarks>
/// The live file walker follows junctions and directory symlinks, but the
/// index stores their contents under the link target. When a file search
/// could reach such a link, this searcher falls back to the live walk
/// rather than return a different result set.
/// </remarks>
internal sealed class VolumeNameSearcher : ISearcher
{
    private const int ChunkRecords = 1 << 15;
    private const int MaxReparseChecks = 256;
    private const uint ReparseTagNameSurrogate = 0x20000000;
    private const int FileAttributeTagInfoClass = 9;

    private readonly ISearcher _inner;
    private readonly VolumeNameIndexService _index;

    public VolumeNameSearcher(ISearcher inner, VolumeNameIndexService index)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public async IAsyncEnumerable<Hit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (plans, fallbackReason) = await TryPlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (plans is null)
        {
            if (fallbackReason is not null)
                request.Status?.Invoke($"{fallbackReason}; using live scan");

            await foreach (var hit in _inner.SearchAsync(request, cancellationToken).ConfigureAwait(false))
                yield return hit;
            yield break;
        }

        var started = Stopwatch.GetTimestamp();
        var counters = new Counters(request.Progress);
        request.Status?.Invoke(
            $"Searching drive name index ({string.Join(", ", plans.Select(plan => plan.State.VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase))})");

        foreach (var plan in plans)
        {
            await foreach (var hit in SearchRootAsync(plan, request, counters, cancellationToken).ConfigureAwait(false))
                yield return hit;
        }

        counters.Publish(force: true);
        request.Status?.Invoke(
            $"Drive name index searched {counters.Enumerated:n0} entries in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:n0} ms");
    }

    private async Task<(List<RootPlan>? Plans, string? FallbackReason)> TryPlanAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SearchTarget == SearchTarget.Content ||
            !request.UseIndex ||
            request.Roots.Count == 0 ||
            request.Expression is UnifiedQuery { HasSemantic: true } ||
            !OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        var includeFiles = request.SearchTarget is SearchTarget.FileNames or SearchTarget.FileAndFolderNames;
        var plans = new List<RootPlan>(request.Roots.Count);
        foreach (var root in request.Roots)
        {
            // Mirror the live searcher, which silently skips missing roots.
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var normalizedRoot = Path.GetFullPath(root);
            var state = await _index.AcquireReadyStateAsync(normalizedRoot, cancellationToken, alwaysCatchUp: true).ConfigureAwait(false);
            if (state is null)
                return (null, null);

            var record = VolumeNameIndexService.ResolveScopeRecord(state, normalizedRoot);
            if (record < 0)
                return (null, $"{normalizedRoot} is not in the drive name index yet");

            if (includeFiles &&
                request.WalkerOptions.Recursive &&
                HasReachableFolderLink(state, record, normalizedRoot, request.WalkerOptions))
            {
                return (null, $"{normalizedRoot} contains folder links the drive name index cannot follow");
            }

            plans.Add(new RootPlan(state, record, normalizedRoot));
        }

        return plans.Count == 0 ? (null, null) : (plans, null);
    }

    private static async IAsyncEnumerable<Hit> SearchRootAsync(
        RootPlan plan,
        SearchRequest request,
        Counters counters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var options = request.WalkerOptions;
        var query = request.Expression;
        var matcher = VolumeNameMatcher.Compile(query);
        var highlights = new List<MatchSpan>(4);

        if (request.SearchTarget is SearchTarget.FolderNames or SearchTarget.FileAndFolderNames)
        {
            counters.AddEnumerated(1);
            if (TryCreateFolderHit(plan.RootPath, plan.RootPath, query, options, highlights, counters) is { } rootHit)
                yield return rootHit;

            var rules = VolumeVisibilityRules.From(options.ExcludeDirectories, pruneHidden: !options.IncludeHidden, pruneReparsePoints: true);
            await foreach (var path in CollectAsync(plan, rules, options.Recursive, CollectFolders, counters, cancellationToken).ConfigureAwait(false))
            {
                if (TryCreateFolderHit(path, plan.RootPath, query, options, highlights, counters) is { } hit)
                    yield return hit;
            }

            bool CollectFolders(VolumeNameTable table, int record) =>
                table.IsDirectory(record) && !rules.IsPrunedDirectory(table, record);
        }

        if (request.SearchTarget is SearchTarget.FileNames or SearchTarget.FileAndFolderNames)
        {
            var rules = VolumeVisibilityRules.From(options.ExcludeDirectories, pruneHidden: !options.IncludeHidden, pruneReparsePoints: false);
            await foreach (var path in CollectAsync(plan, rules, options.Recursive, CollectFiles, counters, cancellationToken).ConfigureAwait(false))
            {
                if (TryCreateFileHit(path, plan.RootPath, query, options, highlights, counters) is { } hit)
                    yield return hit;
            }

            bool CollectFiles(VolumeNameTable table, int record)
            {
                if (table.IsDirectory(record))
                    return false;

                if (!options.IncludeHidden &&
                    (table.FlagsOf(record) & (VolumeEntryFlags.Hidden | VolumeEntryFlags.System)) != 0)
                {
                    return false;
                }

                Span<char> buffer = stackalloc char[VolumeNameTable.MaxNameLength];
                var name = table.NameOf(record, buffer);
                if (options.IncludeExtensions.Count > 0 || options.ExcludeExtensions.Count > 0)
                {
                    var extension = Path.GetExtension(name).ToString();
                    if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(extension))
                        return false;
                    if (options.ExcludeExtensions.Count > 0 && options.ExcludeExtensions.Contains(extension))
                        return false;
                }

                return FileWalker.MatchesGlobs(name, options.IncludeGlobs, defaultIfEmpty: true) &&
                    !FileWalker.MatchesGlobs(name, options.ExcludeGlobs, defaultIfEmpty: false) &&
                    matcher.IsMatch(name);
            }
        }
    }

    /// <summary>
    /// Streams paths of entries below the plan's root that the live walker
    /// would reach and <paramref name="accept"/> keeps. The table is read in
    /// chunks under the read lock and the lock is released before anything
    /// is yielded, so slow consumers never block journal updates.
    /// </summary>
    private static async IAsyncEnumerable<string> CollectAsync(
        RootPlan plan,
        VolumeVisibilityRules rules,
        bool recursive,
        Func<VolumeNameTable, int, bool> accept,
        Counters counters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = plan.State;
        VolumeVisibilityMemo? memo = null;
        var builder = new StringBuilder(260);
        var batch = new List<string>(256);
        var scopes = new HashSet<int> { plan.Record };
        try
        {
            for (var start = 0; ; start += ChunkRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Clear();
                var finished = false;
                state.Lock.EnterReadLock();
                try
                {
                    var table = state.Table;
                    if (table is null)
                        yield break;

                    memo ??= new VolumeVisibilityMemo(table, rules, scopes);
                    var end = Math.Min(table.HighWater, start + ChunkRecords);
                    finished = end >= table.HighWater;
                    for (var record = start; record < end; record++)
                    {
                        if (!table.IsInUse(record) || record == plan.Record || record == table.RootRecord)
                            continue;

                        var parent = table.ParentOf(record);
                        if (!(recursive ? memo.ChildrenVisible(parent) : parent == plan.Record))
                            continue;

                        counters.AddEnumerated(1);
                        if (accept(table, record) && table.TryBuildPathFrom(record, plan.Record, plan.RootPath, builder))
                            batch.Add(builder.ToString());
                    }
                }
                finally
                {
                    state.Lock.ExitReadLock();
                }

                foreach (var path in batch)
                    yield return path;

                if (finished)
                    yield break;

                await Task.Yield();
            }
        }
        finally
        {
            memo?.Dispose();
        }
    }

    private static Hit? TryCreateFileHit(
        string path,
        string root,
        Query query,
        WalkerOptions options,
        List<MatchSpan> highlights,
        Counters counters)
    {
        counters.AddProcessed();
        try
        {
            if (options.IncludeDirectories.Count > 0 && !FileWalker.HasDirectorySegment(path, root, options.IncludeDirectories))
                return null;

            if (!Searcher.TryMatchName(path, root, isDirectory: false, query, out var line, out var score))
                return null;

            var file = new FileInfo(path);
            if (!file.Exists)
                return null;

            if (options.MinFileSizeBytes > 0 && file.Length < options.MinFileSizeBytes) return null;
            if (options.MaxFileSizeBytes > 0 && file.Length > options.MaxFileSizeBytes) return null;
            if (options.ModifiedAfterUtc is { } after && file.LastWriteTimeUtc < after) return null;
            if (options.ModifiedBeforeUtc is { } before && file.LastWriteTimeUtc > before) return null;

            counters.AddMatched();
            return Searcher.CreateNameHit(path, line, score, query, highlights, file.Length, file.LastWriteTimeUtc) with
            {
                Route = HitRoute.Indexed,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            counters.AddFailed();
            return null;
        }
    }

    private static Hit? TryCreateFolderHit(
        string path,
        string root,
        Query query,
        WalkerOptions options,
        List<MatchSpan> highlights,
        Counters counters)
    {
        counters.AddProcessed();
        try
        {
            if (!Searcher.TryMatchName(path, root, isDirectory: true, query, out var line, out var score))
                return null;

            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return null;

            var modified = directory.LastWriteTimeUtc;
            if (options.ModifiedAfterUtc is { } after && modified < after) return null;
            if (options.ModifiedBeforeUtc is { } before && modified > before) return null;

            counters.AddMatched();
            return Searcher.CreateNameHit(path, line, score, query, highlights, null, modified) with
            {
                Route = HitRoute.Indexed,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            counters.AddFailed();
            return null;
        }
    }

    /// <summary>
    /// True when the file walker could descend into a junction or directory
    /// symlink below <paramref name="root"/>. Cloud-file placeholders and
    /// other reparse points that are real folders are fine: the index holds
    /// their children.
    /// </summary>
    private static bool HasReachableFolderLink(VolumeIndexState state, int record, string root, WalkerOptions options)
    {
        var candidates = new List<string>();
        var rules = VolumeVisibilityRules.From(options.ExcludeDirectories, pruneHidden: !options.IncludeHidden, pruneReparsePoints: false);
        var builder = new StringBuilder(260);
        state.Lock.EnterReadLock();
        try
        {
            var table = state.Table;
            if (table is null)
                return true;

            using var memo = new VolumeVisibilityMemo(table, rules, new HashSet<int> { record });
            for (var current = 0; current < table.HighWater; current++)
            {
                if (!table.IsInUse(current) || current == record)
                    continue;

                var flags = table.FlagsOf(current);
                if ((flags & (VolumeEntryFlags.Directory | VolumeEntryFlags.ReparsePoint)) !=
                    (VolumeEntryFlags.Directory | VolumeEntryFlags.ReparsePoint))
                {
                    continue;
                }

                if (rules.IsPrunedDirectory(table, current) || !memo.ChildrenVisible(table.ParentOf(current)))
                    continue;

                if (candidates.Count == MaxReparseChecks)
                    return true;

                if (table.TryBuildPathFrom(current, record, root, builder))
                    candidates.Add(builder.ToString());
            }
        }
        finally
        {
            state.Lock.ExitReadLock();
        }

        return candidates.Any(IsNameSurrogate);
    }

    private static bool IsNameSurrogate(string path)
    {
        using var handle = VolumeNativeMethods.OpenForMetadata(path, VolumeNativeMethods.FileReadAttributes, openReparsePoint: true);
        if (handle.IsInvalid)
            return true;

        var buffer = new byte[8];
        if (!VolumeNativeMethods.GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, buffer, buffer.Length))
            return true;

        var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        return (tag & ReparseTagNameSurrogate) != 0;
    }

    private sealed record RootPlan(VolumeIndexState State, int Record, string RootPath);

    private sealed class Counters(Action<SearchProgress>? progress)
    {
        private long _enumerated;
        private long _processed;
        private long _matched;
        private long _failed;
        private long _lastPublish;

        public long Enumerated => Interlocked.Read(ref _enumerated);

        public void AddEnumerated(long count)
        {
            Interlocked.Add(ref _enumerated, count);
            Publish();
        }

        public void AddProcessed() => Interlocked.Increment(ref _processed);

        public void AddMatched() => Interlocked.Increment(ref _matched);

        public void AddFailed() => Interlocked.Increment(ref _failed);

        public void Publish(bool force = false)
        {
            if (progress is null)
                return;

            var now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref _lastPublish) < 100)
                return;

            Interlocked.Exchange(ref _lastPublish, now);
            progress(new SearchProgress(
                Interlocked.Read(ref _enumerated),
                Interlocked.Read(ref _processed),
                Interlocked.Read(ref _matched),
                0,
                Interlocked.Read(ref _failed)));
        }
    }
}
