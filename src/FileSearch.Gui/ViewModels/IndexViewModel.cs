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
    private readonly ApplicationSettingsViewModel _applicationSettings;
    private readonly IFileLauncher _fileLauncher;
    private readonly IFileSavePicker? _fileSavePicker;
    private readonly IUiDispatcher _dispatcher;
    private readonly SearchViewModel _search;
    private readonly StatusBarViewModel _status;
    private readonly IBackgroundIndexerProcessService? _backgroundIndexer;

    private string? _pendingStatsRoot;
    private bool _isIndexingOperationActive;
    private bool _resumeIndexingAfterCompaction;
    private CancellationTokenSource? _workerStatusCts;
    private bool _usingBackgroundIndexer;

    public IndexViewModel(
        IFileIndex fileIndex,
        IIndexingService indexingService,
        ISettingsService settingsService,
        ApplicationSettingsViewModel applicationSettings,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        SearchViewModel search,
        StatusBarViewModel status,
        IBackgroundIndexerProcessService? backgroundIndexer = null,
        IFileSavePicker? fileSavePicker = null)
    {
        _fileIndex = fileIndex;
        _indexingService = indexingService;
        _settingsService = settingsService;
        _applicationSettings = applicationSettings;
        _fileLauncher = fileLauncher;
        _fileSavePicker = fileSavePicker;
        _dispatcher = dispatcher;
        _search = search;
        _status = status;
        _backgroundIndexer = backgroundIndexer;
        _indexingService.SetResourceProfile(_applicationSettings.IndexerResourceProfile);
        _indexingService.SetRuntimeOptions(BuildRuntimeOptions());
        _newIndexRecursive = _search.IncludeSubfolders;
        _newIndexEnableDocumentExtraction = _search.EnableDocumentExtraction;
        _newIndexSkipUnknownFileTypes = _search.SkipUnknownFileTypes;
        IndexedLocationList = new PagedSidebarList<IndexedLocationSettings>(
            IndexedLocations,
            MatchesIndexedLocation,
            "indexed locations",
            _applicationSettings.SidebarPageSize);

        foreach (var list in LoadFilterLists(_settingsService.Current.IndexInclusionLists))
            IndexInclusionLists.Add(list);

        foreach (var list in LoadFilterLists(_settingsService.Current.IndexExclusionLists))
            IndexExclusionLists.Add(list);

        foreach (var location in LoadIndexedLocations(_settingsService.Current))
            IndexedLocations.Add(location);

        IndexedLocations.CollectionChanged += (_, _) => NotifyIndexCommandStateChanged();
        _applicationSettings.PropertyChanged += OnApplicationSettingsPropertyChanged;
        _search.PropertyChanged += OnSearchPropertyChanged;
        _indexingService.StatusChanged += OnIndexingStatusChanged;

        _ = RefreshIndexDatabaseInfoAsync();
    }

    public ObservableCollection<IndexedLocationSettings> IndexedLocations { get; } = new();

    public PagedSidebarList<IndexedLocationSettings> IndexedLocationList { get; }

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
    [ObservableProperty] private string _indexDatabaseVolumeHealthText = "No volume checkpoints";
    [ObservableProperty] private string _indexDatabaseLastIndexedText = "Never indexed";
    [ObservableProperty] private long _indexDatabaseFailedFileCount;
    [ObservableProperty] private bool _newIndexRecursive = true;
    [ObservableProperty] private bool _newIndexIncludeHidden;
    [ObservableProperty] private bool _newIndexEnableDocumentExtraction = true;
    [ObservableProperty] private bool _newIndexSkipUnknownFileTypes;
    [ObservableProperty] private IndexFilterListSettings? _selectedIndexInclusionList;
    [ObservableProperty] private string _newIndexInclusionListName = string.Empty;
    [ObservableProperty] private string _newIndexIncludedExtensions = string.Empty;
    [ObservableProperty] private string _newIndexIncludedFolders = string.Empty;
    [ObservableProperty] private IndexFilterListSettings? _selectedIndexExclusionList;
    [ObservableProperty] private string _newIndexExclusionListName = string.Empty;
    [ObservableProperty] private string _newIndexExcludedExtensions = DefaultNewIndexExcludedExtensions;
    [ObservableProperty] private string _newIndexExcludedFolders = string.Empty;

    public ObservableCollection<IndexFilterListSettings> IndexInclusionLists { get; } = new();

    public ObservableCollection<IndexFilterListSettings> IndexExclusionLists { get; } = new();

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

        if (!await AddOrUpdateIndexLocationAsync(location).ConfigureAwait(true))
        {
            RemoveIndexedLocation(location.Root);
            _status.Text = "Couldn't queue the index update.";
            return;
        }

        _search.UseIndex = true;
        SaveLocations();
        _status.Text = "Index updating in background.";
    }

    private bool CanAddCurrentFolderToIndex() =>
        !string.IsNullOrWhiteSpace(_search.SearchPath) && Directory.Exists(_search.SearchPath) && !IsCurrentFolderIndexed;

    private bool CanBuildOrRefreshIndex() => CanAddCurrentFolderToIndex();

    [RelayCommand(CanExecute = nameof(CanSaveNewIndexInclusionList))]
    private void SaveNewIndexInclusionList()
    {
        var list = new IndexFilterListSettings
        {
            Name = NewIndexInclusionListName.Trim(),
            Extensions = NormalizeExtensionList(NewIndexIncludedExtensions),
            Folders = NormalizeFolderList(NewIndexIncludedFolders),
        };

        AddOrReplaceFilterList(IndexInclusionLists, list);
        SelectedIndexInclusionList = list;
        SaveFilterLists();
        _status.Text = $"Saved include list: {list.Name}.";
    }

    private bool CanSaveNewIndexInclusionList() =>
        !string.IsNullOrWhiteSpace(NewIndexInclusionListName) &&
        (!string.IsNullOrWhiteSpace(NewIndexIncludedExtensions) ||
         !string.IsNullOrWhiteSpace(NewIndexIncludedFolders));

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedIndexInclusionList))]
    private void RemoveSelectedIndexInclusionList()
    {
        var selected = SelectedIndexInclusionList;
        if (selected is null)
            return;

        IndexInclusionLists.Remove(selected);
        SelectedIndexInclusionList = IndexInclusionLists.FirstOrDefault();
        SaveFilterLists();
        _status.Text = "Include list removed.";
    }

    private bool CanRemoveSelectedIndexInclusionList() => SelectedIndexInclusionList is not null;

    [RelayCommand(CanExecute = nameof(CanSaveNewIndexExclusionList))]
    private void SaveNewIndexExclusionList()
    {
        var list = new IndexFilterListSettings
        {
            Name = NewIndexExclusionListName.Trim(),
            Extensions = NormalizeExtensionList(NewIndexExcludedExtensions),
            Folders = NormalizeFolderList(NewIndexExcludedFolders),
        };

        AddOrReplaceFilterList(IndexExclusionLists, list);
        SelectedIndexExclusionList = list;
        SaveFilterLists();
        _status.Text = $"Saved exclude list: {list.Name}.";
    }

    private bool CanSaveNewIndexExclusionList() =>
        !string.IsNullOrWhiteSpace(NewIndexExclusionListName) &&
        (!string.IsNullOrWhiteSpace(NewIndexExcludedExtensions) ||
         !string.IsNullOrWhiteSpace(NewIndexExcludedFolders));

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedIndexExclusionList))]
    private void RemoveSelectedIndexExclusionList()
    {
        var selected = SelectedIndexExclusionList;
        if (selected is null)
            return;

        IndexExclusionLists.Remove(selected);
        SelectedIndexExclusionList = IndexExclusionLists.FirstOrDefault();
        SaveFilterLists();
        _status.Text = "Exclude list removed.";
    }

    private bool CanRemoveSelectedIndexExclusionList() => SelectedIndexExclusionList is not null;

    [RelayCommand(CanExecute = nameof(CanRebuildSelectedIndex))]
    private async Task RebuildSelectedIndexAsync()
    {
        var location = SelectedIndexedLocation;
        if (location is null)
            return;

        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
        {
            if (!await _backgroundIndexer.RefreshRootAsync(ToIndexedLocation(location), CancellationToken.None).ConfigureAwait(true))
            {
                _status.Text = "Couldn't queue the index rebuild.";
                return;
            }
        }
        else
        {
            await _indexingService.EnqueueRootRefreshAsync(
                location.Root,
                ToIndexedLocation(location).WalkerOptions,
                IndexQueuePriority.High,
                CancellationToken.None).ConfigureAwait(true);
        }

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
            if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
            {
                if (!await _backgroundIndexer.RemoveLocationAsync(_search.SearchPath, CancellationToken.None).ConfigureAwait(true))
                {
                    _status.Text = "Couldn't clear index for current folder.";
                    return;
                }
            }
            else
            {
                await _fileIndex.ClearAsync(_search.SearchPath, CancellationToken.None).ConfigureAwait(true);
            }

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
            if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
            {
                if (!await _backgroundIndexer.RemoveLocationAsync(root, CancellationToken.None).ConfigureAwait(true))
                {
                    _status.Text = "Couldn't clear removed index data.";
                    return;
                }
            }
            else
            {
                await _indexingService.RemoveLocationAsync(root, CancellationToken.None).ConfigureAwait(true);
            }

            await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't clear removed index data: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseBackgroundIndexing))]
    private async Task PauseBackgroundIndexing()
    {
        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
            await _backgroundIndexer.PauseAsync(CancellationToken.None).ConfigureAwait(true);
        else
            _indexingService.Pause();

        IsIndexingPaused = true;
    }

    private bool CanPauseBackgroundIndexing() => !IsIndexingPaused;

    [RelayCommand(CanExecute = nameof(CanResumeBackgroundIndexing))]
    private async Task ResumeBackgroundIndexing()
    {
        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
            await _backgroundIndexer.ResumeAsync(CancellationToken.None).ConfigureAwait(true);
        else
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
            await QueueIndexDatabaseCompactionAsync().ConfigureAwait(true);
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

    [RelayCommand(CanExecute = nameof(CanExportIndexFailures))]
    private async Task ExportIndexFailuresAsync()
    {
        var failures = await _fileIndex.GetFailedFilesAsync(CancellationToken.None).ConfigureAwait(true);
        if (failures.Count == 0)
        {
            _status.Text = "No failed index extractions to export.";
            await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
            return;
        }

        if (_fileSavePicker is null)
        {
            _status.Text = "No save-file picker is available.";
            return;
        }

        var path = _fileSavePicker.PickSaveFile(
            "Export failed index extractions",
            "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
            "filesearch-index-failures.csv");
        if (string.IsNullOrWhiteSpace(path))
            return;

        var format = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? IndexFailureExportFormat.Json
            : IndexFailureExportFormat.Csv;

        await _fileIndex.ExportFailedFilesAsync(path, format, CancellationToken.None).ConfigureAwait(true);
        _status.Text = $"Exported {failures.Count:n0} index failure report rows.";
    }

    private bool CanExportIndexFailures() => IndexDatabaseFailedFileCount > 0;

    public async Task StartBackgroundIndexingAsync()
    {
        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
        {
            if (await _backgroundIndexer.EnsureRunningAsync(CancellationToken.None).ConfigureAwait(true))
            {
                _usingBackgroundIndexer = true;
                await _backgroundIndexer.SetResourceProfileAsync(
                    _applicationSettings.IndexerResourceProfile,
                    CancellationToken.None).ConfigureAwait(true);
                await _backgroundIndexer.SetRuntimeOptionsAsync(
                    BuildRuntimeOptions(),
                    CancellationToken.None).ConfigureAwait(true);
                await ApplyWorkerStatusAsync(CancellationToken.None).ConfigureAwait(true);
                StartWorkerStatusPolling();
                await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
                return;
            }

            _status.Text = "Couldn't start the background indexer; using in-window indexing.";
        }

        _usingBackgroundIndexer = false;
        await _indexingService.StartAsync(
            IndexedLocations.Select(ToIndexedLocation),
            CancellationToken.None).ConfigureAwait(true);
        await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
    }

    public async Task StopBackgroundIndexingAsync()
    {
        _workerStatusCts?.Cancel();
        _workerStatusCts?.Dispose();
        _workerStatusCts = null;

        if (_usingBackgroundIndexer)
            return;

        // ConfigureAwait(false): the exit path blocks the UI thread on this
        // task, so resuming on the dispatcher would deadlock shutdown.
        await _indexingService.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _workerStatusCts?.Cancel();
        _workerStatusCts?.Dispose();
        _indexingService.StatusChanged -= OnIndexingStatusChanged;
        _search.PropertyChanged -= OnSearchPropertyChanged;
        _applicationSettings.PropertyChanged -= OnApplicationSettingsPropertyChanged;
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
                    IncludedExtensions = NormalizeExtensionList(location.IncludedExtensions),
                    IncludedFolders = NormalizeFolderList(location.IncludedFolders),
                    ExcludedExtensions = NormalizeExtensionList(location.ExcludedExtensions),
                    ExcludedFolders = NormalizeFolderList(location.ExcludedFolders),
                    WatchEnabled = location.WatchEnabled,
                    LastIndexedUtcTicks = location.LastIndexedUtcTicks,
                    FileCount = location.FileCount,
                    LineCount = location.LineCount,
                })
                .ToList();
            settings.LastIndexedRoot = string.Empty;
        });
    }

    private void SaveFilterLists()
    {
        _settingsService.Update(settings =>
        {
            settings.IndexInclusionLists = IndexInclusionLists.Select(NormalizeFilterList).ToList();
            settings.IndexExclusionLists = IndexExclusionLists.Select(NormalizeFilterList).ToList();
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

    private void OnApplicationSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationSettingsViewModel.SidebarPageSize))
        {
            IndexedLocationList.PageSize = _applicationSettings.SidebarPageSize;
        }
        else if (e.PropertyName == nameof(ApplicationSettingsViewModel.IndexerResourceProfile))
        {
            if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
            {
                _ = _backgroundIndexer.SetResourceProfileAsync(
                    _applicationSettings.IndexerResourceProfile,
                    CancellationToken.None);
            }
            else
            {
                _indexingService.SetResourceProfile(_applicationSettings.IndexerResourceProfile);
            }
        }
        else if (e.PropertyName is
                 nameof(ApplicationSettingsViewModel.PauseIndexingOnBattery) or
                 nameof(ApplicationSettingsViewModel.IndexOnlyWhenIdle) or
                 nameof(ApplicationSettingsViewModel.IndexerCpuLimitPercent) or
                 nameof(ApplicationSettingsViewModel.IndexerDiskPauseMilliseconds))
        {
            _ = ApplyRuntimeOptionsAsync();
        }
    }

    private bool UseBackgroundIndexerMode =>
        _backgroundIndexer is not null &&
        (_applicationSettings.KeepIndexUpdatedAfterClose ||
         _applicationSettings.StartBackgroundIndexerAtSignIn);

    private bool UseActiveBackgroundIndexer =>
        _usingBackgroundIndexer && _backgroundIndexer is not null;

    private IndexerRuntimeOptions BuildRuntimeOptions() =>
        new(
            _applicationSettings.PauseIndexingOnBattery,
            _applicationSettings.IndexOnlyWhenIdle,
            _applicationSettings.IndexerCpuLimitPercent,
            _applicationSettings.IndexerDiskPauseMilliseconds);

    private async Task ApplyRuntimeOptionsAsync()
    {
        var options = BuildRuntimeOptions();
        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
        {
            await _backgroundIndexer.SetRuntimeOptionsAsync(options, CancellationToken.None).ConfigureAwait(true);
            return;
        }

        _indexingService.SetRuntimeOptions(options);
    }

    private async Task<bool> AddOrUpdateIndexLocationAsync(IndexedLocationSettings location)
    {
        if (UseBackgroundIndexerMode && _backgroundIndexer is not null)
        {
            return await _backgroundIndexer.AddOrUpdateLocationAsync(
                    ToIndexedLocation(location),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }

        await _indexingService.AddOrUpdateLocationAsync(
                ToIndexedLocation(location),
                queueInitialRefresh: true,
                CancellationToken.None)
            .ConfigureAwait(true);
        return true;
    }

    private void StartWorkerStatusPolling()
    {
        _workerStatusCts?.Cancel();
        _workerStatusCts?.Dispose();
        _workerStatusCts = new CancellationTokenSource();
        _ = PollWorkerStatusAsync(_workerStatusCts.Token);
    }

    private async Task PollWorkerStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ApplyWorkerStatusAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ApplyWorkerStatusAsync(CancellationToken cancellationToken)
    {
        if (_backgroundIndexer is null)
            return;

        var status = await _backgroundIndexer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
            return;

        _dispatcher.Post(() => ApplyIndexingStatus(status));
    }

    private static IEnumerable<IndexFilterListSettings> LoadFilterLists(IEnumerable<IndexFilterListSettings> lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            var normalized = NormalizeFilterList(list);
            if (string.IsNullOrWhiteSpace(normalized.Name) || !seen.Add(normalized.Name))
                continue;

            yield return normalized;
        }
    }

    private static IndexFilterListSettings NormalizeFilterList(IndexFilterListSettings list) =>
        new()
        {
            Name = list.Name.Trim(),
            Extensions = NormalizeExtensionList(list.Extensions),
            Folders = NormalizeFolderList(list.Folders),
        };

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
            location.IncludedExtensions = NormalizeExtensionList(location.IncludedExtensions);
            location.IncludedFolders = NormalizeFolderList(location.IncludedFolders);
            location.ExcludedExtensions = NormalizeExtensionList(location.ExcludedExtensions);
            location.ExcludedFolders = NormalizeFolderList(location.ExcludedFolders);
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
            IncludedExtensions = NormalizeExtensionList(NewIndexIncludedExtensions),
            IncludedFolders = NormalizeFolderList(NewIndexIncludedFolders),
            ExcludedExtensions = NormalizeExtensionList(NewIndexExcludedExtensions),
            ExcludedFolders = NormalizeFolderList(NewIndexExcludedFolders),
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

    private static void AddOrReplaceFilterList(
        ObservableCollection<IndexFilterListSettings> lists,
        IndexFilterListSettings list)
    {
        for (var i = 0; i < lists.Count; i++)
        {
            if (string.Equals(lists[i].Name, list.Name, StringComparison.OrdinalIgnoreCase))
            {
                lists[i] = list;
                return;
            }
        }

        lists.Add(list);
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
        _dispatcher.Post(() => ApplyIndexingStatus(status));
    }

    private void ApplyIndexingStatus(IndexingStatus status)
    {
        _isIndexingOperationActive = status.IsProcessing;
        IsIndexing = status.IsProcessing || status.QueueLength > 0;
        IsIndexingPaused = status.IsPaused;
        IndexQueueLength = status.QueueLength;
        ActiveIndexingRoot = status.ActiveRoot ?? string.Empty;
        ApplyIndexingRuntimeStatus(status);
        ApplyActiveIndexProgressStats(status);

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
    }

    private void ApplyActiveIndexProgressStats(IndexingStatus status)
    {
        if (!status.IsProcessing ||
            status.ActiveKind != IndexChangeKind.RefreshRoot ||
            string.IsNullOrWhiteSpace(status.ActiveRoot) ||
            status.ActiveProgress is not { } progress)
        {
            return;
        }

        var location = IndexedLocations.FirstOrDefault(x =>
            string.Equals(x.Root, status.ActiveRoot, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return;

        // Provisional UI counters keep rebuild progress visible while the
        // database is being repopulated. Final persisted totals still come
        // from the database when the refresh completes.
        location.FileCount = Math.Max(location.FileCount, progress.FilesEnumerated);
        location.LineCount = Math.Max(location.LineCount, progress.LinesIndexed);

        IndexDatabaseContentText = FormatDatabaseContent(
            IndexedLocations.Count,
            IndexedLocations.Sum(x => x.FileCount),
            IndexedLocations.Sum(x => x.LineCount),
            suffix: " (scanning)");
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
        IndexDatabaseVolumeHealthText = FormatVolumeHealth(info);
        IndexDatabaseFailedFileCount = info.FailedFileCount;
        IndexDatabaseLastIndexedText = info.LastIndexedUtc is { } indexedUtc
            ? $"Last indexed {indexedUtc.ToLocalTime():g}"
            : "Never indexed";
        CompactIndexDatabaseCommand.NotifyCanExecuteChanged();
        ExportIndexFailuresCommand.NotifyCanExecuteChanged();
    }

    private async Task QueueIndexDatabaseCompactionAsync()
    {
        IsIndexDatabaseCompactionQueued = true;
        _resumeIndexingAfterCompaction |= !IsIndexingPaused;

        if (!IsIndexingPaused)
        {
            IsIndexingPaused = true;
            await PauseIndexingForCompactionAsync().ConfigureAwait(true);
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
            var compacted = UseActiveBackgroundIndexer
                ? await _backgroundIndexer!.CompactDatabaseAsync(CancellationToken.None).ConfigureAwait(true)
                : await CompactLocalIndexDatabaseAsync().ConfigureAwait(true);

            if (compacted)
            {
                await RefreshIndexDatabaseInfoAsync().ConfigureAwait(true);
                _status.Text = "Index database compacted.";
            }
            else
            {
                _status.Text = "Couldn't compact index database.";
            }
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
                await ResumeIndexingAfterCompactionAsync().ConfigureAwait(true);
                IsIndexingPaused = false;
            }
        }
    }

    private async Task PauseIndexingForCompactionAsync()
    {
        if (UseActiveBackgroundIndexer)
            await _backgroundIndexer!.PauseAsync(CancellationToken.None).ConfigureAwait(false);
        else
            _indexingService.Pause();
    }

    private async Task ResumeIndexingAfterCompactionAsync()
    {
        if (UseActiveBackgroundIndexer)
            await _backgroundIndexer!.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        else
            _indexingService.Resume();
    }

    private async Task<bool> CompactLocalIndexDatabaseAsync()
    {
        await _fileIndex.CompactAsync(CancellationToken.None).ConfigureAwait(true);
        return true;
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

    partial void OnSelectedIndexInclusionListChanged(IndexFilterListSettings? value)
    {
        if (value is not null)
        {
            NewIndexInclusionListName = value.Name;
            NewIndexIncludedExtensions = value.Extensions;
            NewIndexIncludedFolders = value.Folders;
        }

        RemoveSelectedIndexInclusionListCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIndexExclusionListChanged(IndexFilterListSettings? value)
    {
        if (value is not null)
        {
            NewIndexExclusionListName = value.Name;
            NewIndexExcludedExtensions = value.Extensions;
            NewIndexExcludedFolders = value.Folders;
        }

        RemoveSelectedIndexExclusionListCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewIndexInclusionListNameChanged(string value) =>
        SaveNewIndexInclusionListCommand.NotifyCanExecuteChanged();

    partial void OnNewIndexIncludedExtensionsChanged(string value) =>
        SaveNewIndexInclusionListCommand.NotifyCanExecuteChanged();

    partial void OnNewIndexIncludedFoldersChanged(string value) =>
        SaveNewIndexInclusionListCommand.NotifyCanExecuteChanged();

    partial void OnNewIndexExclusionListNameChanged(string value) =>
        SaveNewIndexExclusionListCommand.NotifyCanExecuteChanged();

    partial void OnNewIndexExcludedExtensionsChanged(string value) =>
        SaveNewIndexExclusionListCommand.NotifyCanExecuteChanged();

    partial void OnNewIndexExcludedFoldersChanged(string value) =>
        SaveNewIndexExclusionListCommand.NotifyCanExecuteChanged();

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

    partial void OnIndexDatabaseFailedFileCountChanged(long value) =>
        ExportIndexFailuresCommand.NotifyCanExecuteChanged();

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
        var rootDetails = status.RootStatusDetails ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in IndexedLocations)
        {
            var isActive = !string.IsNullOrWhiteSpace(status.ActiveRoot) &&
                string.Equals(location.Root, status.ActiveRoot, StringComparison.OrdinalIgnoreCase);
            var queuedCount = queued.TryGetValue(location.Root, out var count) ? count : 0;
            var rootDetail = rootDetails.TryGetValue(location.Root, out var detail) ? detail : string.Empty;

            location.IsIndexing = status.IsProcessing && isActive;
            location.RuntimeStatusDetail = location.IsIndexing
                ? FormatRuntimeStatus(status)
                : rootDetail;
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
        return FormatDatabaseContent(
            info.LocationCount,
            info.TotalFileCount,
            info.TotalLineCount,
            failedFileCount: info.FailedFileCount);
    }

    private static string FormatDatabaseContent(
        int locationCount,
        long totalFileCount,
        long totalLineCount,
        long failedFileCount = 0,
        string suffix = "")
    {
        var locations = locationCount == 1
            ? "1 location"
            : $"{locationCount:n0} locations";
        var failures = failedFileCount > 0 ? $", {failedFileCount:n0} failed" : string.Empty;
        return $"{locations}, {totalFileCount:n0} files, {totalLineCount:n0} lines{failures}{suffix}";
    }

    private static string FormatPendingChanges(int count) =>
        count switch
        {
            0 => "No pending index changes",
            1 => "1 pending index change",
            _ => $"{count:n0} pending index changes",
        };

    private static string FormatVolumeHealth(IndexDatabaseInfo info)
    {
        var volumes = info.VolumeHealth ?? Array.Empty<IndexVolumeHealthInfo>();
        if (volumes.Count == 0)
            return "No volume checkpoints";

        return string.Join(
            Environment.NewLine,
            volumes.Select(volume =>
            {
                var health = string.IsNullOrWhiteSpace(volume.Health) ? "unknown" : volume.Health;
                var capability = volume.IsRemote
                    ? "remote"
                    : volume.UsnSupported ? $"{volume.FileSystemName} USN" : $"{volume.FileSystemName} no USN";
                var checkpoint = volume.JournalId is null || volume.LastCommittedUsn <= 0
                    ? "no checkpoint"
                    : $"USN {volume.LastCommittedUsn:n0}";
                var error = string.IsNullOrWhiteSpace(volume.LastError) ? string.Empty : $"; {volume.LastError}";
                return $"{health}: {capability}, {checkpoint}{error}";
            }));
    }

    private static string NormalizeExtensionList(string raw) =>
        string.Join("; ", ExtensionList.Parse(raw));

    private static string NormalizeFolderList(string raw) =>
        string.Join("; ", IndexFilterListSettings.ParseFolders(raw));

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

    private static bool MatchesIndexedLocation(IndexedLocationSettings location, string needle) =>
        Contains(location.DisplayName, needle) ||
        Contains(location.Root, needle) ||
        Contains(location.Summary, needle) ||
        Contains(location.TypeSummary, needle) ||
        Contains(location.RuntimeStatusSummary, needle);

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
