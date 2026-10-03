using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Volumes;

/// <summary>Mutable per-volume state. Fields are guarded as documented.</summary>
internal sealed class VolumeIndexState : IDisposable
{
    public VolumeIndexState(IndexVolumeInfo volume, string volumeRoot, string snapshotPath)
    {
        Volume = volume;
        VolumeRoot = volumeRoot;
        SnapshotPath = snapshotPath;
    }

    public IndexVolumeInfo Volume { get; set; }

    /// <summary>Drive root used to build paths, e.g. <c>C:\</c>.</summary>
    public string VolumeRoot { get; set; }

    public string SnapshotPath { get; }

    /// <summary>Readers (searches, saves) share it; journal edits and table swaps take it exclusively.</summary>
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Serializes builds, catch-ups, and saves.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public VolumeNameTable? Table { get; set; }
    public ulong JournalId { get; set; }
    public long CheckpointUsn { get; set; }
    public VolumeBuildMethod? BuildMethod { get; set; }
    public DateTime? BuiltUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime LastSavedUtc { get; set; }
    public long LastCatchUpTimestamp { get; set; }
    public bool Dirty { get; set; }
    public bool LoadAttempted { get; set; }
    public bool IsTracking { get; set; }
    public VolumeIndexPhase Phase { get; set; } = VolumeIndexPhase.NotIndexed;
    public string Message { get; set; } = string.Empty;
    public long? ProgressEntries { get; set; }
    public DateTime NextRebuildAttemptUtc { get; set; }
    public Task? BuildTask { get; set; }
    public IVolumeFileIdResolver? Resolver { get; set; }

    public void Dispose()
    {
        Resolver?.Dispose();
        Lock.Dispose();
        Gate.Dispose();
    }
}

/// <summary>
/// Owns the drive name indexes of this process. A GUI that calls
/// <see cref="StartTrackingAsync"/> becomes the live owner: it follows the
/// change journal once a second and saves snapshots. Other hosts (CLI, MCP)
/// load snapshots lazily the first time a search needs them and catch up
/// from the journal in memory without ever writing to disk.
/// </summary>
internal sealed class VolumeNameIndexService : IVolumeNameIndex, IDisposable
{
    private readonly IIndexVolumeResolver _volumeResolver;
    private readonly IUsnJournalReader _journal;
    private readonly IVolumeScanner _scanner;
    private readonly IVolumeFileIdResolverFactory _resolverFactory;
    private readonly IVolumeScanLauncher? _launcher;
    private readonly VolumeNameIndexOptions _options;
    private readonly ILogger _logger;
    private readonly Func<bool> _isElevated;
    private readonly ConcurrentDictionary<string, VolumeIndexState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly object _trackingLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private HashSet<string> _trackedKeys = new(StringComparer.OrdinalIgnoreCase);
    private Task? _pollLoop;
    private long _lastStatusTimestamp;
    private int _disposed;

    public VolumeNameIndexService(
        IIndexVolumeResolver volumeResolver,
        IUsnJournalReader journal,
        IVolumeScanner scanner,
        IVolumeFileIdResolverFactory resolverFactory,
        IVolumeScanLauncher? launcher = null,
        VolumeNameIndexOptions? options = null,
        ILogger<VolumeNameIndexService>? logger = null,
        Func<bool>? isElevated = null)
    {
        _volumeResolver = volumeResolver ?? throw new ArgumentNullException(nameof(volumeResolver));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _launcher = launcher;
        _options = options ?? new VolumeNameIndexOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _isElevated = isElevated ?? (() => Environment.IsPrivilegedProcess);
    }

    public event EventHandler<VolumeIndexStatus>? StatusChanged;

    public IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes()
    {
        var candidates = new List<VolumeIndexCandidate>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return candidates;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;
                var fileSystem = drive.DriveFormat;
                var supported = TryResolveVolume(root, out _, out var reason);
                candidates.Add(new VolumeIndexCandidate(
                    root,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    fileSystem,
                    drive.TotalSize,
                    supported,
                    supported ? null : reason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return candidates;
    }

    public IReadOnlyList<VolumeIndexStatus> GetStatuses()
    {
        var statuses = new List<VolumeIndexStatus>();
        foreach (var candidate in GetCandidateVolumes().Where(candidate => candidate.IsSupported))
        {
            if (!TryResolveVolume(candidate.VolumeRoot, out var volume, out _))
                continue;

            if (_states.TryGetValue(volume.VolumeKey, out var state) &&
                (state.Table is not null || state.IsTracking || state.Phase != VolumeIndexPhase.NotIndexed))
            {
                statuses.Add(ToStatus(state));
                continue;
            }

            var snapshotPath = GetSnapshotPath(volume);
            if (!File.Exists(snapshotPath))
                continue;

            var header = VolumeSnapshotSerializer.TryReadHeaderFile(snapshotPath, out var error);
            statuses.Add(header is null || !string.Equals(header.VolumeKey, volume.VolumeKey, StringComparison.OrdinalIgnoreCase)
                ? new VolumeIndexStatus(candidate.VolumeRoot, VolumeIndexPhase.NeedsRebuild, 0, null, null, null, false, error ?? "Snapshot belongs to another volume.")
                : new VolumeIndexStatus(
                    candidate.VolumeRoot,
                    VolumeIndexPhase.Ready,
                    header.EntryCount,
                    header.BuildMethod,
                    header.BuiltUtc,
                    header.SavedUtc,
                    IsTracking: false,
                    "Snapshot on disk; loads on first search."));
        }

        return statuses;
    }

    public Task StartTrackingAsync(IReadOnlyCollection<string> volumeRoots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);
        ThrowIfDisposed();

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in volumeRoots)
        {
            if (TryGetOrCreateState(root, out var state, out var reason))
            {
                keys.Add(state.Volume.VolumeKey);
                state.IsTracking = true;
                Publish(state, force: true);
            }
            else
            {
                _logger.LogInformation("Drive name index cannot track {Root}: {Reason}", root, reason);
            }
        }

        List<VolumeIndexState> released;
        lock (_trackingLock)
        {
            released = _trackedKeys
                .Where(key => !keys.Contains(key))
                .Select(key => _states.TryGetValue(key, out var state) ? state : null)
                .OfType<VolumeIndexState>()
                .ToList();
            _trackedKeys = keys;
            _pollLoop ??= Task.Run(() => PollLoopAsync(_lifetime.Token), CancellationToken.None);
        }

        foreach (var state in released)
        {
            state.IsTracking = false;
            Publish(state, force: true);
        }

        _wake.Release();
        return Task.CompletedTask;
    }

    public async Task<VolumeIndexStatus> BuildAsync(string volumeRoot, VolumeBuildMethod method, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!TryGetOrCreateState(volumeRoot, out var state, out var reason))
            throw new InvalidOperationException(reason);

        await BuildCoreAsync(state, method, allowElevationPrompt: true, cancellationToken).ConfigureAwait(false);
        return ToStatus(state);
    }

    public async Task RemoveAsync(string volumeRoot, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!TryResolveVolume(NormalizeVolumeRoot(volumeRoot), out var volume, out _))
            return;

        lock (_trackingLock)
        {
            _trackedKeys.Remove(volume.VolumeKey);
        }

        if (_states.TryGetValue(volume.VolumeKey, out var state))
        {
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                state.IsTracking = false;
                InstallTable(state, null);
                state.Dirty = false;
                state.LoadAttempted = true;
                state.Phase = VolumeIndexPhase.NotIndexed;
                state.Message = "Not indexed.";
                state.BuildMethod = null;
                state.BuiltUtc = null;
                state.UpdatedUtc = null;
                TryDeleteFile(state.SnapshotPath);
            }
            finally
            {
                state.Gate.Release();
            }

            Publish(state, force: true);
            return;
        }

        TryDeleteFile(GetSnapshotPath(volume));
    }

    public async Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        var started = Stopwatch.GetTimestamp();
        var compiled = VolumeTermSearch.Compile(request.Text);
        var scopeRoots = request.ScopeRoots is { Count: > 0 }
            ? request.ScopeRoots
            : GetCandidateVolumes().Where(candidate => candidate.IsSupported).Select(candidate => candidate.VolumeRoot).ToList();
        var uncovered = new List<string>();
        if (compiled is null)
            return VolumeTermSearchResult.Empty(uncovered);

        var groups = new Dictionary<VolumeIndexState, HashSet<int>>();
        foreach (var root in scopeRoots)
        {
            var state = await AcquireReadyStateAsync(root, cancellationToken).ConfigureAwait(false);
            var record = state is null ? -1 : ResolveScopeRecord(state, root);
            if (state is null || record < 0)
            {
                uncovered.Add(root);
                continue;
            }

            if (!groups.TryGetValue(state, out var records))
            {
                records = new HashSet<int>();
                groups[state] = records;
            }

            records.Add(record);
        }

        var searches = groups.Select(group => Task.Run(() =>
        {
            var state = group.Key;
            state.Lock.EnterReadLock();
            try
            {
                return state.Table is null
                    ? (Matches: new List<VolumeTermMatch>(), Total: 0L)
                    : VolumeTermSearch.Search(state.Table, state.VolumeRoot, compiled, request, group.Value, cancellationToken);
            }
            finally
            {
                state.Lock.ExitReadLock();
            }
        }, cancellationToken)).ToArray();

        var results = await Task.WhenAll(searches).ConfigureAwait(false);
        var matches = results
            .SelectMany(result => result.Matches)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, request.MaxResults))
            .ToList();
        return new VolumeTermSearchResult(
            matches,
            results.Sum(result => result.Total),
            groups.Keys.Select(state => state.VolumeRoot).ToList(),
            uncovered,
            Stopwatch.GetElapsedTime(started));
    }

    public bool IsReady(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryResolveVolume(path, out var volume, out _))
            return false;

        return _states.TryGetValue(volume.VolumeKey, out var state) &&
            state.Table is not null &&
            state.Phase == VolumeIndexPhase.Ready;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _states.Values.Where(state => state.IsTracking && state.Dirty))
        {
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveCoreAsync(state).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Synchronous disposal for containers disposed with <c>Dispose()</c>
    /// (the CLI). Every await inside uses ConfigureAwait(false), so blocking
    /// here cannot deadlock on a UI thread.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var state in _states.Values)
        {
            if (state.BuildTask is { } build)
            {
                try
                {
                    await build.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                }
            }
        }

        try
        {
            using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await FlushAsync(flushTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save drive name index snapshots during shutdown.");
        }

        foreach (var state in _states.Values)
            state.Dispose();

        _lifetime.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// Returns a loaded, journal-current state for the volume holding
    /// <paramref name="path"/>, or null when there is no usable index.
    /// Non-owners load the snapshot on first use and catch up on demand.
    /// </summary>
    internal async ValueTask<VolumeIndexState?> AcquireReadyStateAsync(
        string path,
        CancellationToken cancellationToken,
        bool alwaysCatchUp = false)
    {
        if (Volatile.Read(ref _disposed) != 0 || !TryResolveVolume(path, out var volume, out _))
            return null;

        if (!_states.TryGetValue(volume.VolumeKey, out var state))
        {
            if (!File.Exists(GetSnapshotPath(volume)) || !TryGetOrCreateState(path, out state, out _))
                return null;
        }

        if (state.Table is null)
        {
            if (state.IsTracking || state.LoadAttempted)
                return null;

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state.Table is null && !state.LoadAttempted)
                    await LoadCoreAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }

        // A full search waits for the journal so a file created a moment ago
        // is never missing; keystroke searches catch up only when stale and
        // skip the wait if the owner's poll is already doing it.
        if (state.Table is not null &&
            (alwaysCatchUp
                ? await WaitForGateAsync(state, cancellationToken).ConfigureAwait(false)
                : Stopwatch.GetElapsedTime(state.LastCatchUpTimestamp) >= _options.OnDemandCatchUpInterval &&
                  await state.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)))
        {
            try
            {
                if (state.Phase == VolumeIndexPhase.Ready)
                    await CatchUpCoreAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }

        return state.Table is not null && state.Phase == VolumeIndexPhase.Ready ? state : null;
    }

    private static async Task<bool> WaitForGateAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        // A build holds the gate for minutes; search the current table
        // instead of waiting for it.
        if (state.Phase == VolumeIndexPhase.Building)
            return false;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Maps a folder to its record in <paramref name="state"/>'s table, or
    /// -1 when the folder is unknown or is itself a reparse point (a
    /// junction's results would not read like a live walk of it).
    /// </summary>
    internal static int ResolveScopeRecord(VolumeIndexState state, string folder)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return -1;
        }

        if (!VolumeNativeMethods.TryGetFileReference(fullPath, out var reference, out var attributes) ||
            (attributes & (uint)FileAttributes.Directory) == 0)
        {
            return -1;
        }

        if (IsReparsePoint(fullPath))
            return -1;

        state.Lock.EnterReadLock();
        try
        {
            var table = state.Table;
            if (table is null)
                return -1;

            var record = VolumeNameTable.RecordOf(reference);
            if (record == table.RootRecord)
                return record;

            return table.Contains(reference) ? record : -1;
        }
        finally
        {
            state.Lock.ExitReadLock();
        }
    }

    /// <summary>Test hook: installs a table built for a folder as if it were a volume.</summary>
    internal VolumeIndexState AttachForTesting(
        string rootPath,
        VolumeNameTable table,
        IndexVolumeInfo volume,
        ulong journalId,
        long checkpointUsn)
    {
        var state = new VolumeIndexState(volume, rootPath, GetSnapshotPath(volume))
        {
            JournalId = journalId,
            CheckpointUsn = checkpointUsn,
            BuildMethod = VolumeBuildMethod.DirectoryWalk,
            BuiltUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            LoadAttempted = true,
            LastCatchUpTimestamp = Stopwatch.GetTimestamp(),
        };
        InstallTable(state, table);
        state.Phase = VolumeIndexPhase.Ready;
        _states[volume.VolumeKey] = state;
        return state;
    }

    internal Task CatchUpForTestingAsync(VolumeIndexState state, CancellationToken cancellationToken) =>
        CatchUpCoreAsync(state, cancellationToken);

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            VolumeIndexState[] tracked;
            lock (_trackingLock)
            {
                tracked = _trackedKeys
                    .Select(key => _states.TryGetValue(key, out var state) ? state : null)
                    .OfType<VolumeIndexState>()
                    .ToArray();
            }

            foreach (var state in tracked)
            {
                try
                {
                    await MaintainAsync(state, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Drive name index maintenance failed for {Root}.", state.VolumeRoot);
                    state.Message = ex.Message;
                    Publish(state, force: true);
                }
            }

            try
            {
                await _wake.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MaintainAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        if (!state.IsTracking || state.BuildTask is { IsCompleted: false })
            return;

        if (!await state.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        var needsBuild = false;
        try
        {
            if (state.Table is null && !state.LoadAttempted)
                await LoadCoreAsync(state, cancellationToken).ConfigureAwait(false);

            if (state.Table is not null && state.Phase == VolumeIndexPhase.Ready)
                await CatchUpCoreAsync(state, cancellationToken).ConfigureAwait(false);

            needsBuild = (state.Table is null || state.Phase == VolumeIndexPhase.NeedsRebuild) &&
                state.Phase != VolumeIndexPhase.Unsupported &&
                DateTime.UtcNow >= state.NextRebuildAttemptUtc;

            if (!needsBuild &&
                state.Dirty &&
                DateTime.UtcNow - state.LastSavedUtc >= _options.SaveInterval)
            {
                await SaveCoreAsync(state).ConfigureAwait(false);
            }
        }
        finally
        {
            state.Gate.Release();
        }

        if (needsBuild)
        {
            state.NextRebuildAttemptUtc = DateTime.UtcNow + _options.RebuildRetryInterval;
            state.BuildTask = Task.Run(async () =>
            {
                try
                {
                    await BuildCoreAsync(state, VolumeBuildMethod.Auto, allowElevationPrompt: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // BuildCoreAsync already logged it and published the failure.
                }
            }, cancellationToken);
        }
    }

    private async Task BuildCoreAsync(
        VolumeIndexState state,
        VolumeBuildMethod method,
        bool allowElevationPrompt,
        CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousPhase = state.Phase;
        try
        {
            var elevated = _isElevated();
            var resolved = method switch
            {
                VolumeBuildMethod.Auto => elevated ? VolumeBuildMethod.MasterFileTable : VolumeBuildMethod.DirectoryWalk,
                VolumeBuildMethod.MasterFileTable when !elevated && (!allowElevationPrompt || _launcher is null) =>
                    VolumeBuildMethod.DirectoryWalk,
                _ => method,
            };

            state.Phase = VolumeIndexPhase.Building;
            state.ProgressEntries = 0;
            state.Message = resolved == VolumeBuildMethod.MasterFileTable
                ? $"Reading the master file table of {state.VolumeRoot}..."
                : $"Scanning folders on {state.VolumeRoot}...";
            Publish(state, force: true);

            if (resolved == VolumeBuildMethod.MasterFileTable && !elevated)
            {
                await BuildWithElevatedHelperAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var journal = await TryQueryJournalAsync(state, cancellationToken).ConfigureAwait(false);
                var progress = new SynchronousProgress(count =>
                {
                    state.ProgressEntries = count;
                    Publish(state);
                });
                var target = new VolumeScanTarget(state.VolumeRoot, state.Volume.VolumeDevicePath);
                var table = await Task.Run(
                        () => _scanner.Scan(target, resolved, progress, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                InstallTable(state, table);
                state.JournalId = journal?.JournalId ?? 0;
                state.CheckpointUsn = journal?.NextUsn ?? 0;
                state.BuildMethod = resolved;
                state.BuiltUtc = DateTime.UtcNow;
            }

            state.Phase = VolumeIndexPhase.Ready;
            state.Dirty = true;
            state.LoadAttempted = true;
            state.ProgressEntries = null;
            state.Message = state.JournalId == 0
                ? "Indexed; this drive has no change journal, so rebuild to pick up changes."
                : "Indexed.";

            // Replays whatever changed while the scan ran.
            await CatchUpCoreAsync(state, cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(state).ConfigureAwait(false);
            _logger.LogInformation(
                "Built drive name index for {Root} with {Method}: {Count} entries.",
                state.VolumeRoot,
                state.BuildMethod,
                state.Table?.Count ?? 0);
        }
        catch (OperationCanceledException)
        {
            state.ProgressEntries = null;
            state.Phase = state.Table is null ? VolumeIndexPhase.NotIndexed : previousPhase;
            state.Message = state.Table is null ? "Not indexed." : "Build canceled; the previous index is still in use.";
            throw;
        }
        catch (Exception ex)
        {
            state.ProgressEntries = null;
            state.Phase = state.Table is null ? VolumeIndexPhase.Failed : previousPhase;
            state.Message = $"Build failed: {ex.Message}";
            _logger.LogWarning(ex, "Drive name index build failed for {Root}.", state.VolumeRoot);
            throw;
        }
        finally
        {
            state.Gate.Release();
            Publish(state, force: true);
        }
    }

    private async Task BuildWithElevatedHelperAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.SnapshotDirectory);
        var temp = Path.Combine(
            _options.SnapshotDirectory,
            Path.GetFileNameWithoutExtension(state.SnapshotPath) + "." + Guid.NewGuid().ToString("N") + VolumeScanHelper.OutputExtension);
        try
        {
            await _launcher!.RunElevatedScanAsync(state.VolumeRoot, temp, cancellationToken).ConfigureAwait(false);
            string? readError = null;
            var snapshot = await Task.Run(() => VolumeSnapshotSerializer.TryReadFile(temp, out readError), cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is not { } loaded)
                throw new IOException($"The elevated scan produced an unreadable snapshot: {readError}");
            if (!string.Equals(loaded.Header.VolumeKey, state.Volume.VolumeKey, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The elevated scan returned a snapshot for a different volume.");

            InstallTable(state, loaded.Table);
            state.JournalId = loaded.Header.JournalId;
            state.CheckpointUsn = loaded.Header.CheckpointUsn;
            state.BuildMethod = VolumeBuildMethod.MasterFileTable;
            state.BuiltUtc = loaded.Header.BuiltUtc;
        }
        finally
        {
            TryDeleteFile(temp);
            TryDeleteFile(temp + VolumeScanHelper.ErrorExtension);
        }
    }

    private async Task LoadCoreAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        state.LoadAttempted = true;
        if (!File.Exists(state.SnapshotPath))
        {
            state.Phase = VolumeIndexPhase.NotIndexed;
            state.Message = "Not indexed.";
            Publish(state, force: true);
            return;
        }

        state.Phase = VolumeIndexPhase.Loading;
        state.Message = "Loading snapshot...";
        Publish(state, force: true);

        string? error = null;
        var loaded = await Task.Run(() => VolumeSnapshotSerializer.TryReadFile(state.SnapshotPath, out error), cancellationToken)
            .ConfigureAwait(false);
        if (loaded is not { } snapshot ||
            !string.Equals(snapshot.Header.VolumeKey, state.Volume.VolumeKey, StringComparison.OrdinalIgnoreCase))
        {
            state.Phase = VolumeIndexPhase.NeedsRebuild;
            state.Message = error ?? "The snapshot belongs to a different volume.";
            Publish(state, force: true);
            return;
        }

        InstallTable(state, snapshot.Table);
        state.JournalId = snapshot.Header.JournalId;
        state.CheckpointUsn = snapshot.Header.CheckpointUsn;
        state.BuildMethod = snapshot.Header.BuildMethod;
        state.BuiltUtc = snapshot.Header.BuiltUtc;
        state.UpdatedUtc = snapshot.Header.SavedUtc;
        state.LastSavedUtc = DateTime.UtcNow;
        state.Phase = VolumeIndexPhase.Ready;
        state.Message = "Loaded snapshot.";
        await CatchUpCoreAsync(state, cancellationToken).ConfigureAwait(false);
        Publish(state, force: true);
    }

    /// <summary>Applies journal changes since the checkpoint. Caller holds the gate.</summary>
    private async Task CatchUpCoreAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        if (state.Table is null || state.JournalId == 0)
            return;

        UsnJournalSnapshot journal;
        try
        {
            journal = await _journal.QueryAsync(state.Volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            state.Message = $"Change journal unavailable: {ex.Message}";
            return;
        }

        if (journal.JournalId != state.JournalId || state.CheckpointUsn < journal.FirstUsn)
        {
            MarkNeedsRebuild(state, journal.JournalId != state.JournalId
                ? "The drive's change journal was recreated; the index must be rebuilt."
                : "Too many changes happened since the index was last updated; it must be rebuilt.");
            return;
        }

        if (state.CheckpointUsn < journal.NextUsn)
        {
            var coalescer = new VolumeChangeCoalescer();
            try
            {
                await foreach (var record in _journal
                                   .ReadChangesAsync(state.Volume, state.CheckpointUsn, journal.NextUsn, journal.JournalId, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    coalescer.Add(record);
                }
            }
            catch (IOException ex)
            {
                // The reader maps ERROR_JOURNAL_ENTRY_DELETED to IOException.
                MarkNeedsRebuild(state, $"The change journal could not be replayed: {ex.Message}");
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Transient; the next poll retries from the same checkpoint.
                state.Message = $"Change journal read failed: {ex.Message}";
                return;
            }

            if (coalescer.Changes.Count > 0)
                ApplyChanges(state, coalescer.Changes);

            state.CheckpointUsn = journal.NextUsn;
            state.UpdatedUtc = DateTime.UtcNow;
        }

        state.LastCatchUpTimestamp = Stopwatch.GetTimestamp();
    }

    private void ApplyChanges(VolumeIndexState state, List<PendingVolumeChange> changes)
    {
        List<PendingVolumeChange> relevant;
        state.Lock.EnterReadLock();
        try
        {
            if (state.Table is null)
                return;

            relevant = VolumeChangeApplier.FilterRelevant(state.Table, changes);
        }
        finally
        {
            state.Lock.ExitReadLock();
        }

        if (relevant.Count == 0)
            return;

        state.Resolver ??= _resolverFactory.Create(state.Volume.RootDirectoryPath);
        VolumeChangeApplier.ResolveNames(relevant, state.Resolver);

        int applied;
        state.Lock.EnterWriteLock();
        try
        {
            if (state.Table is null)
                return;

            applied = VolumeChangeApplier.Apply(state.Table, relevant);
        }
        finally
        {
            state.Lock.ExitWriteLock();
        }

        if (applied > 0)
        {
            state.Dirty = true;
            Publish(state);
        }
    }

    /// <summary>Writes the snapshot. Caller holds the gate.</summary>
    private async Task SaveCoreAsync(VolumeIndexState state)
    {
        if (state.Table is null)
            return;

        await Task.Run(() =>
        {
            state.Lock.EnterWriteLock();
            try
            {
                state.Table?.CompactIfFragmented();
            }
            finally
            {
                state.Lock.ExitWriteLock();
            }

            state.Lock.EnterReadLock();
            try
            {
                var table = state.Table;
                if (table is null)
                    return;

                var header = new VolumeSnapshotHeader(
                    state.Volume.VolumeKey,
                    state.Volume.VolumeSerial,
                    state.VolumeRoot,
                    state.JournalId,
                    state.CheckpointUsn,
                    state.BuildMethod ?? VolumeBuildMethod.DirectoryWalk,
                    state.BuiltUtc ?? DateTime.UtcNow,
                    DateTime.UtcNow,
                    table.RootRecord,
                    table.SequenceAt(table.RootRecord),
                    table.Count);
                VolumeSnapshotSerializer.WriteFile(state.SnapshotPath, header, table);
            }
            finally
            {
                state.Lock.ExitReadLock();
            }
        }).ConfigureAwait(false);

        state.Dirty = false;
        state.LastSavedUtc = DateTime.UtcNow;
    }

    private async Task<UsnJournalSnapshot?> TryQueryJournalAsync(VolumeIndexState state, CancellationToken cancellationToken)
    {
        try
        {
            return await _journal.QueryAsync(state.Volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogInformation(ex, "No change journal for {Root}; the drive name index will not update live.", state.VolumeRoot);
            return null;
        }
    }

    private static void InstallTable(VolumeIndexState state, VolumeNameTable? table)
    {
        state.Lock.EnterWriteLock();
        try
        {
            state.Table = table;
        }
        finally
        {
            state.Lock.ExitWriteLock();
        }
    }

    private void MarkNeedsRebuild(VolumeIndexState state, string message)
    {
        state.Phase = VolumeIndexPhase.NeedsRebuild;
        state.Message = message;
        Publish(state, force: true);
    }

    private bool TryGetOrCreateState(string path, out VolumeIndexState state, out string reason)
    {
        state = null!;
        var root = NormalizeVolumeRoot(path);
        if (!TryResolveVolume(root, out var volume, out reason))
            return false;

        state = _states.GetOrAdd(volume.VolumeKey, _ => new VolumeIndexState(volume, root, GetSnapshotPath(volume)));
        state.Volume = volume;

        // The volume key survives drive-letter changes; follow the new letter.
        if (!Directory.Exists(state.VolumeRoot))
            state.VolumeRoot = root;

        return true;
    }

    private bool TryResolveVolume(string path, out IndexVolumeInfo volume, out string reason)
    {
        if (!_volumeResolver.TryResolveVolume(path, out volume, out reason))
            return false;

        if (volume.IsRemote)
        {
            reason = "Network drives cannot be indexed by name.";
            return false;
        }

        if (volume.DriveKind is not (IndexVolumeDriveKind.Fixed or IndexVolumeDriveKind.Unknown))
        {
            reason = "Only fixed local drives can be indexed by name.";
            return false;
        }

        // Only the file system matters here. Some NTFS volumes omit
        // FILE_SUPPORTS_USN_JOURNAL from their flags yet answer journal
        // queries fine; a volume with no journal still indexes, it just
        // reports that it cannot update live.
        if (!volume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Drive name indexing needs NTFS; this drive is {volume.FileSystemName}.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private string GetSnapshotPath(IndexVolumeInfo volume)
    {
        var key = volume.VolumeKey.TrimEnd('\\');
        var name = key[(key.LastIndexOf('\\') + 1)..];
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(name))
            name = "volume";

        return Path.Combine(_options.SnapshotDirectory, name + VolumeSnapshotSerializer.FileExtension);
    }

    internal static string NormalizeVolumeRoot(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length is 2 or 3 &&
            char.IsAsciiLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed.Length == 2 || trimmed[2] is '\\' or '/'))
        {
            return char.ToUpperInvariant(trimmed[0]) + @":\";
        }

        try
        {
            return Path.GetPathRoot(Path.GetFullPath(trimmed)) is { Length: > 0 } root
                ? root.ToUpperInvariant()
                : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private VolumeIndexStatus ToStatus(VolumeIndexState state) =>
        new(
            state.VolumeRoot,
            state.Phase,
            state.Table?.Count ?? 0,
            state.BuildMethod,
            state.BuiltUtc,
            state.UpdatedUtc,
            state.IsTracking,
            state.Message,
            state.ProgressEntries);

    private void Publish(VolumeIndexState state, bool force = false)
    {
        var handler = StatusChanged;
        if (handler is null)
            return;

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastStatusTimestamp);
        if (!force && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(250))
            return;

        Interlocked.Exchange(ref _lastStatusTimestamp, now);
        try
        {
            handler(this, ToStatus(state));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Drive name index status listener failed.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Reports on the scanning thread. Progress&lt;T&gt; would post to the
    /// caller's synchronization context, and status listeners marshal
    /// to their own UI thread anyway.
    /// </summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
