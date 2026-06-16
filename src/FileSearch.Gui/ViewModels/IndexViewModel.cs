using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core;
using FileSearch.Core.Indexing;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Indexed-location management and background-indexing status. Owns the
/// IndexedLocations slice of the persisted settings. Depends on
/// <see cref="SearchViewModel"/> (one direction only) for the current folder
/// and the search-derived indexing options.
/// </summary>
public sealed partial class IndexViewModel : ObservableObject, IDisposable
{
    private const string DefaultNewIndexExcludedExtensions = ".dll; .exe";

    private readonly IFileIndex _fileIndex;
    private readonly IIndexingService _indexingService;
    private readonly ISettingsService _settingsService;
    private readonly IFileLauncher _fileLauncher;
    private readonly IUiDispatcher _dispatcher;
    private readonly SearchViewModel _search;
    private readonly StatusBarViewModel _status;

    private string? _pendingStatsRoot;
    private bool _isIndexingOperationActive;
    private bool _resumeIndexingAfterCompaction;

    public IndexViewModel(
        IFileIndex fileIndex,
        IIndexingService indexingService,
        ISettingsService settingsService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        SearchViewModel search,
        StatusBarViewModel status)
    {
        _fileIndex = fileIndex;
        _indexingService = indexingService;
        _settingsService = settingsService;
        _fileLauncher = fileLauncher;
        _dispatcher = dispatcher;
        _search = search;
        _status = status;
        _newIndexRecursive = _search.IncludeSubfolders;
        _newIndexEnableDocumentExtraction = _search.EnableDocumentExtraction;
        _newIndexSkipUnknownFileTypes = _search.SkipUnknownFileTypes;

        foreach (var location in LoadIndexedLocations(_settingsService.Current))
            IndexedLocations.Add(location);

        IndexedLocations.CollectionChanged += (_, _) => NotifyIndexCommandStateChanged();
        _search.PropertyChanged += OnSearchPropertyChanged;
        _indexingService.StatusChanged += OnIndexingStatusChanged;

        _ = RefreshIndexDatabaseInfoAsync();
    }

    public ObservableCollection<IndexedLocationSettings> IndexedLocations { get; } = new();

    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _isIndexingPaused;
    [ObservableProperty] private int _indexQueueLength;
    [ObservableProperty] private string _activeIndexingRoot = string.Empty;
    [ObservableProperty] private IndexedLocationSettings? _selectedIndexedLocation;
    [ObservableProperty] private bool _indexDatabaseExists;
    [ObservableProperty] private bool _indexDatabaseIsCompatible;
    [ObservableProperty] private bool _isCompactingIndexDatabase;
    [ObservableProperty] private bool _isIndexDatabaseCompactionQueued;
    [ObservableProperty] private string _indexDatabasePath = string.Empty;
    [ObservableProperty] private string _indexDatabaseStatusText = "Database info unavailable";
    [ObservableProperty] private string _indexDatabaseSizeText = "No database file yet";
    [ObservableProperty] private string _indexDatabaseContentText = "No indexed content";
    [ObservableProperty] private string _indexDatabaseQueueText = "No pending index changes";
    [ObservableProperty] private string _indexDatabaseLastIndexedText = "Never indexed";
    [ObservableProperty] private bool _newIndexRecursive = true;
    [ObservableProperty] private bool _newIndexIncludeHidden;
    [ObservableProperty] private bool _newIndexEnableDocumentExtraction = true;
    [ObservableProperty] private bool _newIndexSkipUnknownFileTypes;
    [ObservableProperty] private string _newIndexExcludedExtensions = DefaultNewIndexExcludedExtensions;

    public string IndexActivityText =>
        IsIndexingPaused
            ? "Indexing paused"
            : !string.IsNullOrWhiteSpace(ActiveIndexingRoot)
                ? $"Indexing {GetDisplayName(ActiveIndexingRoot)}"
            : IndexQueueLength > 0
                ? $"Indexing {IndexQueueLength:n0} queued"
                : "Index ready";

    public bool IsCurrentFolderIndexed => GetIndexedLocation(_search.SearchPath) is not null;

    public string CurrentFolderIndexActionText =>
        IsCurrentFolderIndexed ? "Current folder already indexed" : "Add current folder";

    public string IndexedLocationCountText =>
        IndexedLocations.Count == 1
            ? "1 indexed location"
            : $"{IndexedLocations.Count:n0} indexed locations";

    public string CompactIndexDatabaseActionText =>
        IsIndexDatabaseCompactionQueued
            ? "Compact queued"
            : IsIndexing || _search.IsSearching
                ? "Compact when idle"
                : "Compact database";

    // ----- commands -----

    [RelayCommand(CanExecute = nameof(CanBuildOrRefreshIndex))]
    private async Task BuildOrRefreshIndexAsync()
    {
        await AddCurrentFolderToIndexAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAddCurrentFolderToIndex))]
    private async Task AddCurrentFolderToIndexAsync()
    {
        if (string.IsNullOrWhiteSpace(_search.SearchPath) || !Directory.Exists(_search.SearchPath))
        {
            _status.Text = "Choose an existing folder before adding an index.";
            return;
        }

        if (IsCurrentFolderIndexed)
        {
            _status.Text = "Current folder is already indexed.";
            return;
        }

        await AddFolderToIndexAsync(_search.SearchPath).ConfigureAwait(true);
    }

    public async Task AddFolderToIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _status.Text = "Choose an existing folder before adding an index.";
            return;
        }

        if (GetIndexedLocation(folder) is { } existing)
        {
            SelectedIndexedLocation = existing;
            _status.Text = "Selected folder is already indexed.";
            return;
        }

        var location = CreateIndexedLocationSettings(folder);
        AddOrReplaceIndexedLocation(location);
        SelectedIndexedLocation = location;

        await _indexingService.AddOrUpdateLocationAsync(ToIndexedLocation(location), queueInitialRefresh: true, CancellationToken.None)
            .ConfigureAwait(true);

        _search.UseIndex = true;
        SaveLocations();
        _status.Text = "Index updating in background.";
    }

    private bool CanAddCurrentFolderToIndex() =>
        !string.IsNullOrWhiteSpace(_search.SearchPath) && Directory.Exists(_search.SearchPath) && !IsCurrentFolderIndexed;

    private bool CanBuildOrRefreshIndex() => CanAddCurrentFolderToIndex();

    [RelayCommand(CanExecute = nameof(CanRebuildSelectedIndex))]
    private async Task RebuildSelectedIndexAsync()
    {
        var location = SelectedIndexedLocation;
        if (location is null)
            return;

        await _indexingService.EnqueueRootRefreshAsync(
            location.Root,
            ToIndexedLocation(location).WalkerOptions,
            IndexQueuePriority.High,
            CancellationToken.None).ConfigureAwait(true);

        _status.Text = "Index rebuild queued.";
    }

    private bool CanRebuildSelectedIndex() => SelectedIndexedLocation is not null;

    [RelayCommand(CanExecute = nameof(CanClearIndexForCurrentFolder))]
    private async Task ClearIndexForCurrentFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_search.SearchPath))
            return;

        try
        {
            await _fileIndex.ClearAsync(_search.SearchPath, CancellationToken.None).ConfigureAwait(true);
            RemoveIndexedLocation(_search.SearchPath);
            _status.Text = "Index cleared for current folder.";
            SaveLocations();
            await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't clear index: {ex.Message}";
        }
    }

    private bool CanClearIndexForCurrentFolder() => !string.IsNullOrWhiteSpace(_search.SearchPath);

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedIndex))]
    private Task RemoveSelectedIndexAsync()
    {
        var location = SelectedIndexedLocation;
        if (location is null)
            return Task.CompletedTask;

        var root = location.Root;
        IndexedLocations.Remove(location);
        SelectedIndexedLocation = IndexedLocations.FirstOrDefault();
        SaveLocations();
        _status.Text = "Indexed location removed.";
        _ = RemoveIndexStorageAsync(root);
        return Task.CompletedTask;
    }

    private bool CanRemoveSelectedIndex() => SelectedIndexedLocation is not null;

    private async Task RemoveIndexStorageAsync(string root)
    {
        try
        {
            await _indexingService.RemoveLocationAsync(root, CancellationToken.None).ConfigureAwait(true);
            await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't clear removed index data: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseBackgroundIndexing))]
    private void PauseBackgroundIndexing()
    {
        _indexingService.Pause();
        IsIndexingPaused = true;
    }

    private bool CanPauseBackgroundIndexing() => !IsIndexingPaused;

    [RelayCommand(CanExecute = nameof(CanResumeBackgroundIndexing))]
    private void ResumeBackgroundIndexing()
    {
        _indexingService.Resume();
        IsIndexingPaused = false;
    }

    private bool CanResumeBackgroundIndexing() => IsIndexingPaused;

    [RelayCommand]
    private void OpenIndexLocation()
    {
        var path = _fileIndex.DatabasePath;
        var folder = Path.GetDirectoryName(path);
        _fileLauncher.RevealInExplorer(File.Exists(path) ? path : folder ?? path);
    }

    [RelayCommand(CanExecute = nameof(CanCompactIndexDatabase))]
    private async Task CompactIndexDatabaseAsync()
    {
        if (_isIndexingOperationActive || IndexQueueLength > 0 || _search.IsSearching)
        {
            QueueIndexDatabaseCompaction();
            RunQueuedIndexDatabaseCompactionIfReady();
            return;
        }

        await RunCompactIndexDatabaseAsync().ConfigureAwait(true);
    }

    private bool CanCompactIndexDatabase() =>
        IndexDatabaseExists &&
        IndexDatabaseIsCompatible &&
        !IsCompactingIndexDatabase &&
        !IsIndexDatabaseCompactionQueued;

    public async Task StartBackgroundIndexingAsync()
    {
        await _indexingService.StartAsync(
            IndexedLocations.Select(ToIndexedLocation),
            CancellationToken.None).ConfigureAwait(true);
        await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
    }

    public async Task StopBackgroundIndexingAsync()
    {
        // ConfigureAwait(false): the exit path blocks the UI thread on this
        // task, so resuming on the dispatcher would deadlock shutdown.
        await _indexingService.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _indexingService.StatusChanged -= OnIndexingStatusChanged;
        _search.PropertyChanged -= OnSearchPropertyChanged;
    }

    /// <summary>Persists this view model's slice of the settings.</summary>
    public void SaveLocations()
    {
        _settingsService.Update(settings =>
        {
            settings.IndexedLocations = IndexedLocations
                .Where(location => !string.IsNullOrWhiteSpace(location.Root))
                .Select(location => new IndexedLocationSettings
                {
                    Root = IndexPath.NormalizeRoot(location.Root),
                    Recursive = location.Recursive,
                    IncludeHidden = location.IncludeHidden,
                    EnableDocumentExtraction = location.EnableDocumentExtraction,
                    SkipUnknownFileTypes = location.SkipUnknownFileTypes,
                    ExcludedExtensions = NormalizeExtensionList(location.ExcludedExtensions),
                    WatchEnabled = location.WatchEnabled,
                    LastIndexedUtcTicks = location.LastIndexedUtcTicks,
                    FileCount = location.FileCount,
                    LineCount = location.LineCount,
                })
                .ToList();
            settings.LastIndexedRoot = string.Empty;
        });
    }

    // ----- helpers -----

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.SearchPath))
            NotifyIndexCommandStateChanged();

        if (e.PropertyName == nameof(SearchViewModel.IsSearching))
        {
            OnPropertyChanged(nameof(CompactIndexDatabaseActionText));
            RunQueuedIndexDatabaseCompactionIfReady();
        }
    }

    private static IEnumerable<IndexedLocationSettings> LoadIndexedLocations(AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in settings.IndexedLocations)
        {
            if (string.IsNullOrWhiteSpace(location.Root))
                continue;

            var normalizedRoot = IndexPath.NormalizeRoot(location.Root);
            if (!seen.Add(normalizedRoot))
                continue;

            location.Root = normalizedRoot;
            location.ExcludedExtensions = NormalizeExtensionList(location.ExcludedExtensions);
            yield return location;
        }

        if (!string.IsNullOrWhiteSpace(settings.LastIndexedRoot))
        {
            var legacyRoot = IndexPath.NormalizeRoot(settings.LastIndexedRoot);
            if (seen.Add(legacyRoot))
            {
                yield return new IndexedLocationSettings
                {
                    Root = legacyRoot,
                    Recursive = true,
                    WatchEnabled = true,
                };
            }
        }
    }

    private IndexedLocationSettings CreateIndexedLocationSettings(string root) =>
        new()
        {
            Root = IndexPath.NormalizeRoot(root),
            Recursive = NewIndexRecursive,
            IncludeHidden = NewIndexIncludeHidden,
            EnableDocumentExtraction = NewIndexEnableDocumentExtraction,
            SkipUnknownFileTypes = NewIndexSkipUnknownFileTypes,
            ExcludedExtensions = NormalizeExtensionList(NewIndexExcludedExtensions),
            WatchEnabled = true,
        };

    private void AddOrReplaceIndexedLocation(IndexedLocationSettings location)
    {
        for (var i = 0; i < IndexedLocations.Count; i++)
        {
            if (string.Equals(IndexedLocations[i].Root, location.Root, StringComparison.OrdinalIgnoreCase))
            {
                IndexedLocations[i] = location;
                return;
            }
        }

        IndexedLocations.Add(location);
    }

    private IndexedLocationSettings? GetIndexedLocation(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        string normalizedRoot;
        try
        {
            normalizedRoot = IndexPath.NormalizeRoot(root);
        }
        catch
        {
            return null;
        }

        return IndexedLocations.FirstOrDefault(location =>
            string.Equals(location.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveIndexedLocation(string root)
    {
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        for (var i = IndexedLocations.Count - 1; i >= 0; i--)
            if (string.Equals(IndexedLocations[i].Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                IndexedLocations.RemoveAt(i);
    }

    private IndexedLocation ToIndexedLocation(IndexedLocationSettings settings) =>
        new(
            IndexPath.NormalizeRoot(settings.Root),
            _search.BuildIndexWalkerOptions(settings),
            settings.WatchEnabled);

    private void OnIndexingStatusChanged(object? sender, IndexingStatus status)
    {
        _dispatcher.Post(() =>
        {
            _isIndexingOperationActive = status.IsProcessing;
            IsIndexing = status.IsProcessing || status.QueueLength > 0;
            IsIndexingPaused = status.IsPaused;
            IndexQueueLength = status.QueueLength;
            ActiveIndexingRoot = status.ActiveRoot ?? string.Empty;
            ApplyIndexingRuntimeStatus(status);

            if (!_search.IsSearching)
                _status.Text = status.Message;

            // Refresh stats only for a root that just finished processing —
            // re-counting every root on every idle status was a full scan of
            // the lines table per root.
            if (status.IsProcessing && !string.IsNullOrWhiteSpace(status.ActiveRoot))
            {
                _pendingStatsRoot = status.ActiveRoot;
            }
            else if (!status.IsProcessing && _pendingStatsRoot is { } completedRoot)
            {
                _pendingStatsRoot = null;
                _ = RefreshIndexedLocationStatsAsync(completedRoot);
                _ = RefreshIndexDatabaseInfoAsync();
            }

            RunQueuedIndexDatabaseCompactionIfReady();
        });
    }

    private async Task RefreshIndexedLocationStatsAsync(string root)
    {
        IndexStats stats;
        try
        {
            stats = await _fileIndex.GetStatsAsync(root, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        var location = IndexedLocations.FirstOrDefault(x =>
            string.Equals(x.Root, stats.Root, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return;

        location.FileCount = stats.FileCount;
        location.LineCount = stats.LineCount;
        location.LastIndexedUtcTicks = stats.IndexedUtc?.Ticks ?? 0;
        SaveLocations();
    }

    private async Task RefreshIndexDatabaseInfoAsync()
    {
        IndexDatabaseInfo info;
        try
        {
            info = await _fileIndex.GetDatabaseInfoAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            IndexDatabaseStatusText = "Database info unavailable";
            CompactIndexDatabaseCommand.NotifyCanExecuteChanged();
            return;
        }

        IndexDatabasePath = info.DatabasePath;
        IndexDatabaseExists = info.Exists;
        IndexDatabaseIsCompatible = info.IsCompatible;
        IndexDatabaseStatusText = FormatDatabaseStatus(info);
        IndexDatabaseSizeText = FormatDatabaseSize(info);
        IndexDatabaseContentText = FormatDatabaseContent(info);
        IndexDatabaseQueueText = FormatPendingChanges(info.PendingChangeCount);
        IndexDatabaseLastIndexedText = info.LastIndexedUtc is { } indexedUtc
            ? $"Last indexed {indexedUtc.ToLocalTime():g}"
            : "Never indexed";
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();
    }

    private void QueueIndexDatabaseCompaction()
    {
        IsIndexDatabaseCompactionQueued = true;
        _resumeIndexingAfterCompaction |= !_indexingService.IsPaused;

        if (!_indexingService.IsPaused)
        {
            _indexingService.Pause();
            IsIndexingPaused = true;
        }

        _status.Text = "Index database compaction queued. Indexing will pause, compact, then resume.";
    }

    private void RunQueuedIndexDatabaseCompactionIfReady()
    {
        if (!IsIndexDatabaseCompactionQueued ||
            IsCompactingIndexDatabase ||
            _isIndexingOperationActive ||
            _search.IsSearching)
        {
            return;
        }

        _ = RunCompactIndexDatabaseAsync();
    }

    private async Task RunCompactIndexDatabaseAsync()
    {
        var resumeIndexing = _resumeIndexingAfterCompaction;
        IsIndexDatabaseCompactionQueued = false;
        _resumeIndexingAfterCompaction = false;
        IsCompactingIndexDatabase = true;
        _status.Text = "Compacting index database...";

        try
        {
            await _fileIndex.CompactAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
            _status.Text = "Index database compacted.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't compact index database: {ex.Message}";
        }
        finally
        {
            IsCompactingIndexDatabase = false;

            if (resumeIndexing)
            {
                _indexingService.Resume();
                IsIndexingPaused = false;
            }
        }
    }

    partial void OnIsIndexingChanged(bool value)
    {
        BuildOrRefreshIndexCommand.NotifyCanExecuteChanged();
        ClearIndexForCurrentFolderCommand.NotifyCanExecuteChanged();
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CompactIndexDatabaseActionText));
    }

    partial void OnIndexQueueLengthChanged(int value)
    {
        OnPropertyChanged(nameof(IndexActivityText));
        OnPropertyChanged(nameof(CompactIndexDatabaseActionText));
    }

    partial void OnIsIndexingPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexActivityText));
        PauseBackgroundIndexingCommand.NotifyCanExecuteChanged();
        ResumeBackgroundIndexingCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveIndexingRootChanged(string value) =>
        OnPropertyChanged(nameof(IndexActivityText));

    partial void OnSelectedIndexedLocationChanged(IndexedLocationSettings? value)
    {
        RebuildSelectedIndexCommand.NotifyCanExecuteChanged();
        RemoveSelectedIndexCommand.NotifyCanExecuteChanged();
    }

    partial void OnIndexDatabaseExistsChanged(bool value) =>
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();

    partial void OnIndexDatabaseIsCompatibleChanged(bool value) =>
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();

    partial void OnIsCompactingIndexDatabaseChanged(bool value) =>
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();

    partial void OnIsIndexDatabaseCompactionQueuedChanged(bool value)
    {
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CompactIndexDatabaseActionText));
    }

    private void NotifyIndexCommandStateChanged()
    {
        OnPropertyChanged(nameof(IsCurrentFolderIndexed));
        OnPropertyChanged(nameof(CurrentFolderIndexActionText));
        OnPropertyChanged(nameof(IndexedLocationCountText));
        AddCurrentFolderToIndexCommand.NotifyCanExecuteChanged();
        BuildOrRefreshIndexCommand.NotifyCanExecuteChanged();
        ClearIndexForCurrentFolderCommand.NotifyCanExecuteChanged();
    }

    private void ApplyIndexingRuntimeStatus(IndexingStatus status)
    {
        var queued = status.QueuedRootCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in IndexedLocations)
        {
            var isActive = !string.IsNullOrWhiteSpace(status.ActiveRoot) &&
                string.Equals(location.Root, status.ActiveRoot, StringComparison.OrdinalIgnoreCase);
            var queuedCount = queued.TryGetValue(location.Root, out var count) ? count : 0;

            location.IsIndexing = status.IsProcessing && isActive;
            location.RuntimeStatusDetail = location.IsIndexing
                ? FormatRuntimeStatus(status)
                : string.Empty;
            location.QueuedWorkCount = queuedCount;
            location.IsQueued = queuedCount > 0;
            location.IsIndexingPaused = status.IsPaused && (location.IsIndexing || location.IsQueued);
        }
    }

    private static string FormatRuntimeStatus(IndexingStatus status)
    {
        if (status.ActiveProgress is { } progress)
        {
            var changed = progress.FilesIndexed + progress.FilesRemoved;
            var failed = progress.FilesFailed > 0 ? $", {progress.FilesFailed:n0} failed" : string.Empty;
            return $"Scanning {progress.FilesEnumerated:n0}; {changed:n0} changed, {progress.FilesSkippedUnchanged:n0} unchanged{failed}";
        }

        return status.ActiveKind switch
        {
            IndexChangeKind.RefreshRoot => "Scanning files",
            IndexChangeKind.UpsertFile => status.Message,
            IndexChangeKind.DeleteFile => status.Message,
            _ => "Indexing now",
        };
    }

    private static string FormatDatabaseStatus(IndexDatabaseInfo info)
    {
        if (!info.Exists)
            return "Database not created yet";

        return info.IsCompatible
            ? $"Ready, schema {info.SchemaVersion}"
            : $"Schema mismatch, expected {info.SchemaVersion}";
    }

    private static string FormatDatabaseSize(IndexDatabaseInfo info)
    {
        if (!info.Exists && info.TotalBytes == 0)
            return "No database file yet";

        return $"{FormatBytes(info.TotalBytes)} total (db {FormatBytes(info.DatabaseBytes)}, wal {FormatBytes(info.WalBytes)}, shm {FormatBytes(info.ShmBytes)})";
    }

    private static string FormatDatabaseContent(IndexDatabaseInfo info)
    {
        var locations = info.LocationCount == 1
            ? "1 location"
            : $"{info.LocationCount:n0} locations";
        return $"{locations}, {info.TotalFileCount:n0} files, {info.TotalLineCount:n0} lines";
    }

    private static string FormatPendingChanges(int count) =>
        count switch
        {
            0 => "No pending index changes",
            1 => "1 pending index change",
            _ => $"{count:n0} pending index changes",
        };

    private static string NormalizeExtensionList(string raw) =>
        string.Join("; ", ExtensionList.Parse(raw));

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:n0} {units[unit]}"
            : $"{value:n1} {units[unit]}";
    }

    private static string GetDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }
}
