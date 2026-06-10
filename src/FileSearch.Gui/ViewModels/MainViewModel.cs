using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using Microsoft.Win32;

namespace FileSearch.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxHistoryEntries = 15;
    private const int HitsTabIndex = 1;
    public const double MinimumPreviewPaneWidth = 300;
    public const double MaximumPreviewPaneWidth = 720;

    private readonly ISearcher _searcher;
    private readonly IExtractorRegistry _extractorRegistry;
    private readonly IQueryFactory _queryFactory;
    private readonly IFilePreviewService _previewService;
    private readonly IThemeService _themeService;
    private readonly IFileLauncher _fileLauncher;
    private readonly ISettingsService _settingsService;
    private readonly IFileTypeOptionsStore _fileTypeOptionsStore;
    private readonly FileTypeOptions _fileTypeOptions;
    private readonly IFileIndex _fileIndex;
    private readonly IIndexingService _indexingService;
    private readonly IShellIntegrationService _shellIntegrationService;
    private readonly IFolderPicker _folderPicker;
    private readonly IUiDispatcher _dispatcher;

    private readonly Dictionary<string, FileResultViewModel> _filesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _refinementDebounceCts;
    private string? _pendingStatsRoot;
    private bool _isInitialized;

    public MainViewModel(
        ISearcher searcher,
        IExtractorRegistry extractorRegistry,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IThemeService themeService,
        IFileLauncher fileLauncher,
        ISettingsService settingsService,
        IFileTypeOptionsStore fileTypeOptionsStore,
        IFileIndex fileIndex,
        IIndexingService indexingService,
        IShellIntegrationService shellIntegrationService,
        IFolderPicker folderPicker,
        IUiDispatcher dispatcher)
    {
        _searcher = searcher;
        _extractorRegistry = extractorRegistry;
        _queryFactory = queryFactory;
        _previewService = previewService;
        _themeService = themeService;
        _fileLauncher = fileLauncher;
        _settingsService = settingsService;
        _fileTypeOptionsStore = fileTypeOptionsStore;
        _fileTypeOptions = _fileTypeOptionsStore.Load();
        _fileIndex = fileIndex;
        _indexingService = indexingService;
        _shellIntegrationService = shellIntegrationService;
        _folderPicker = folderPicker;
        _dispatcher = dispatcher;
        _indexingService.StatusChanged += OnIndexingStatusChanged;

        // Set up the filtered view used by the "Filter" tab.
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = FilterFiles;
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilesVisible));

        // Load search history, then seed the input fields with the most
        // recent entry so the user lands back where they left off.
        var saved = _settingsService.Current;
        foreach (var q in saved.RecentQueries) RecentQueries.Add(q);
        foreach (var p in saved.RecentPaths) RecentPaths.Add(p);
        foreach (var scope in saved.CustomScopes.Where(scope => !string.IsNullOrWhiteSpace(scope.Name)))
        {
            CustomScopes.Add(new SearchScope
            {
                Name = scope.Name.Trim(),
                FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
            });
        }
        foreach (var location in LoadIndexedLocations(saved))
            IndexedLocations.Add(location);

        RecentQueries.CollectionChanged += (_, _) => ClearRecentQueriesCommand.NotifyCanExecuteChanged();
        RecentPaths.CollectionChanged += (_, _) => ClearRecentPathsCommand.NotifyCanExecuteChanged();
        CustomScopes.CollectionChanged += (_, _) => ClearCustomScopesCommand.NotifyCanExecuteChanged();
        IndexedLocations.CollectionChanged += (_, _) => NotifyIndexCommandStateChanged();

        if (RecentQueries.Count > 0) QueryText = RecentQueries[0];
        if (RecentPaths.Count > 0) SearchPath = RecentPaths[0];
        SkipUnknownFileTypes = saved.SkipUnknownFileTypes;
        UseIndex = saved.UseIndex;
        if (_fileTypeOptions.AdditionalPlainTextExtensions.Count == 0 && !string.IsNullOrWhiteSpace(saved.AdditionalPlainTextExtensions))
        {
            _fileTypeOptions.AdditionalPlainTextExtensions = ParseExtensions(saved.AdditionalPlainTextExtensions).ToList();
            _fileTypeOptionsStore.Save(_fileTypeOptions);
        }
        AdditionalPlainTextExtensions = string.Join("; ", _fileTypeOptions.AdditionalPlainTextExtensions);
        _isInitialized = true;
    }

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

    public ObservableCollection<SearchScope> CustomScopes { get; } = new();

    public ObservableCollection<IndexedLocationSettings> IndexedLocations { get; } = new();

    public ObservableCollection<FileResultViewModel> Files { get; } = new();

    /// <summary>
    /// Filtered view of <see cref="Files"/>. The DataGrid binds to
    /// <c>Files</c> directly; WPF resolves to this same default view, so
    /// changing the filter here filters what the grid shows without
    /// touching the underlying collection.
    /// </summary>
    public ICollectionView FilesView { get; }

    /// <summary>Count of files currently passing the filter.</summary>
    public int FilesVisible =>
        (FilesView as ListCollectionView)?.Count ?? Files.Count;

    public IReadOnlyList<QueryMode> AvailableModes { get; } =
        new[] { QueryMode.Boolean, QueryMode.Regex, QueryMode.PlainText };

    // --- search inputs (Main tab) ---
    [ObservableProperty] private string _searchPath = Environment.CurrentDirectory;
    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private string _fileNamePattern = string.Empty;
    [ObservableProperty] private bool _includeSubfolders = true;

    // --- options (Options tab) ---
    [ObservableProperty] private QueryMode _searchMode = QueryMode.Boolean;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _enableDocumentExtraction = true;
    [ObservableProperty] private bool _skipUnknownFileTypes;
    [ObservableProperty] private bool _useIndex;
    [ObservableProperty] private int _minSizeKB;
    [ObservableProperty] private int _maxSizeKB;
    private string _additionalPlainTextExtensions = string.Empty;

    public string AdditionalPlainTextExtensions
    {
        get => _additionalPlainTextExtensions;
        set => SetProperty(ref _additionalPlainTextExtensions, value);
    }

    // --- date filters (Dates tab) ---
    [ObservableProperty] private bool _modifiedAfterEnabled;
    [ObservableProperty] private DateTime _modifiedAfter = DateTime.Today.AddDays(-7);
    [ObservableProperty] private bool _modifiedBeforeEnabled;
    [ObservableProperty] private DateTime _modifiedBefore = DateTime.Today;

    // --- runtime state ---
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _isIndexingPaused;
    [ObservableProperty] private int _indexQueueLength;
    [ObservableProperty] private string _activeIndexingRoot = string.Empty;
    [ObservableProperty] private int _totalHits;
    [ObservableProperty] private int _filesMatched;
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _previewContent = string.Empty;
    [ObservableProperty] private bool _isPreviewPaneVisible = true;
    [ObservableProperty] private double _previewPaneWidth = 360;
    [ObservableProperty] private int _selectedDetailsTabIndex;
    [ObservableProperty] private IndexedLocationSettings? _selectedIndexedLocation;

    // --- in-memory filter over the results ("search the search") ---
    [ObservableProperty] private string _refinementQuery = string.Empty;

    partial void OnRefinementQueryChanged(string value)
    {
        // Refreshing the view rescans every hit of every file; debounce so
        // fast typing pays once. Clearing applies immediately.
        _refinementDebounceCts?.Cancel();
        _refinementDebounceCts?.Dispose();
        _refinementDebounceCts = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            RefreshFilesView();
            return;
        }

        var cts = new CancellationTokenSource();
        _refinementDebounceCts = cts;
        _ = RefreshFilesViewDebouncedAsync(cts.Token);
    }

    private async Task RefreshFilesViewDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        RefreshFilesView();
    }

    private void RefreshFilesView()
    {
        FilesView.Refresh();
        OnPropertyChanged(nameof(FilesVisible));
    }

    [RelayCommand]
    private void ClearRefinement() => RefinementQuery = string.Empty;

    private bool FilterFiles(object obj)
    {
        if (obj is not FileResultViewModel file) return true;
        if (string.IsNullOrWhiteSpace(RefinementQuery)) return true;

        var needle = RefinementQuery;
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        if (file.FileName.Contains(needle, ci)) return true;
        foreach (var hit in file.Hits)
            if (hit.LineContent.Contains(needle, ci))
                return true;
        return false;
    }

    [ObservableProperty]
    private FileResultViewModel? _selectedFile;

    public bool HasSelectedFile => SelectedFile is not null;

    public string PreviewPaneToggleText => "Preview";

    public string ResultsSummaryText => $"{FilesMatched:n0} files · {TotalHits:n0} hits";

    /// <summary>Heading above the results list ("Find …" once a query is set).</summary>
    public string ResultsContextText =>
        string.IsNullOrWhiteSpace(QueryText) ? "Results" : $"Find “{QueryText.Trim()}”";

    public string FilePatternSummary =>
        string.IsNullOrWhiteSpace(FileNamePattern) ? "All files" : FileNamePattern;

    public string MatchCaseSummary => MatchCase ? "Match case on" : "Match case off";

    public string SubfoldersSummary => IncludeSubfolders ? "Subfolders on" : "Subfolders off";

    public string DocumentExtractionSummary =>
        EnableDocumentExtraction ? "Office/PDF on" : "Office/PDF off";

    public string UnknownFileTypesSummary =>
        SkipUnknownFileTypes ? "Known types only" : "Unknown text allowed";

    public string IndexSummaryText => UseIndex ? "Use index on" : "Use index off";

    public string IndexActivityText =>
        IsIndexingPaused
            ? "Indexing paused"
            : !string.IsNullOrWhiteSpace(ActiveIndexingRoot)
                ? $"Indexing {GetDisplayName(ActiveIndexingRoot)}"
            : IndexQueueLength > 0
                ? $"Indexing {IndexQueueLength:n0} queued"
                : "Index ready";

    public bool IsCurrentFolderIndexed => GetIndexedLocation(SearchPath) is not null;

    public string CurrentFolderIndexActionText =>
        IsCurrentFolderIndexed ? "Current folder already indexed" : "Add current folder";

    public string IndexedLocationCountText =>
        IndexedLocations.Count == 1
            ? "1 indexed location"
            : $"{IndexedLocations.Count:n0} indexed locations";

    public string DateSummary
    {
        get
        {
            if (ModifiedAfterEnabled && ModifiedBeforeEnabled)
                return $"{ModifiedAfter:MMM d} - {ModifiedBefore:MMM d}";
            if (ModifiedAfterEnabled)
                return $"After {ModifiedAfter:MMM d}";
            if (ModifiedBeforeEnabled)
                return $"Before {ModifiedBefore:MMM d}";
            return "Modified any time";
        }
    }

    partial void OnSelectedFileChanged(FileResultViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedFile));
        CopyFileContentCommand.NotifyCanExecuteChanged();
        if (value is not null)
            SelectedDetailsTabIndex = HitsTabIndex;
        _ = LoadPreviewAsync(value);
    }

    partial void OnPreviewContentChanged(string value) =>
        CopyPreviewCommand.NotifyCanExecuteChanged();

    partial void OnIsPreviewPaneVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewPaneToggleText));
    }

    partial void OnPreviewPaneWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinimumPreviewPaneWidth, MaximumPreviewPaneWidth);
        if (Math.Abs(value - clamped) > 0.1)
        {
            PreviewPaneWidth = clamped;
        }
    }

    // ----- commands -----

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneVisible = !IsPreviewPaneVisible;

    [RelayCommand(CanExecute = nameof(CanCopyPreview))]
    private void CopyPreview()
    {
        _fileLauncher.CopyToClipboard(PreviewContent);
        StatusText = "Copied preview to clipboard.";
    }

    private bool CanCopyPreview() => !string.IsNullOrEmpty(PreviewContent);

    [RelayCommand(CanExecute = nameof(CanCopyFileContent))]
    private async Task CopyFileContentAsync()
    {
        var file = SelectedFile;
        if (file is null) return;

        try
        {
            StatusText = "Reading file content...";
            var text = await _previewService
                .LoadFullTextAsync(file.FullPath, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(text))
            {
                StatusText = "No extractable text content for this file type.";
                return;
            }

            _fileLauncher.CopyToClipboard(text);
            StatusText = $"Copied file content ({text.Length:n0} characters) to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't copy content: {ex.Message}";
        }
    }

    private bool CanCopyFileContent() => HasSelectedFile;

    [RelayCommand]
    private void Browse()
    {
        var folder = _folderPicker.PickFolder("Select folder to search", SearchPath);
        if (folder is not null)
            SearchPath = folder;
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(QueryText) || string.IsNullOrWhiteSpace(SearchPath))
            return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Record this attempt in history first so a failing/cancelled
        // search still ends up in the dropdowns.
        RecordHistory(QueryText, SearchPath);

        RefinementQuery = string.Empty;
        _filesByPath.Clear();
        Files.Clear();
        SelectedFile = null;
        PreviewContent = string.Empty;
        TotalHits = 0;
        FilesMatched = 0;
        ElapsedText = "—";

        FileSearch.Core.Queries.Query query;
        try
        {
            query = _queryFactory.Build(QueryText, SearchMode, MatchCase);
        }
        catch (Exception ex)
        {
            StatusText = $"Invalid query: {ex.Message}";
            return;
        }

        IsSearching = true;
        StatusText = "Searching...";
        SearchCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        var stopwatch = Stopwatch.StartNew();
        var routeStatus = string.Empty;
        try
        {
            IProgress<SearchProgress> progress = new Progress<SearchProgress>(UpdateProgressStatus);
            var request = new SearchRequest(
                query,
                new[] { SearchPath },
                BuildWalkerOptions(),
                progress.Report,
                UseIndex,
                message =>
                {
                    routeStatus = message;
                    StatusText = message;
                });

            // Consume the hit stream on the thread pool and flush to the UI
            // in timed batches — applying hits one at a time marshalled every
            // result through the dispatcher and stuttered on large result sets.
            var pendingHits = new System.Collections.Concurrent.ConcurrentQueue<Hit>();
            var consumer = Task.Run(async () =>
            {
                await foreach (var hit in _searcher.SearchAsync(request, token).ConfigureAwait(false))
                    pendingHits.Enqueue(hit);
            }, token);

            while (true)
            {
                DrainPendingHits(pendingHits);
                if (consumer.IsCompleted && pendingHits.IsEmpty)
                    break;
                await Task.Delay(75).ConfigureAwait(true);
            }

            await consumer.ConfigureAwait(true); // surface cancellation/errors

            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            StatusText = string.IsNullOrEmpty(routeStatus)
                ? $"Done — {TotalHits} hits in {FilesMatched} files"
                : $"{routeStatus}; done — {TotalHits} hits in {FilesMatched} files";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            StatusText = $"Canceled — {TotalHits} hits in {FilesMatched} files";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
            SearchCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSearch() => !IsSearching;

    /// <summary>
    /// Applies queued hits to the UI collections. Runs on the UI thread; the
    /// per-drain cap keeps a huge backlog from freezing a frame.
    /// </summary>
    private void DrainPendingHits(System.Collections.Concurrent.ConcurrentQueue<Hit> pendingHits)
    {
        const int maxPerDrain = 2000;
        var total = TotalHits;
        var drained = 0;

        while (drained < maxPerDrain && pendingHits.TryDequeue(out var hit))
        {
            if (!_filesByPath.TryGetValue(hit.Path, out var file))
            {
                file = new FileResultViewModel(hit.Path, _fileLauncher);
                _filesByPath[hit.Path] = file;
                Files.Add(file);
            }

            file.AddHit(hit);
            total++;
            drained++;
        }

        if (drained == 0)
            return;

        TotalHits = total;
        FilesMatched = Files.Count;
        StatusText = $"Searching... {TotalHits:n0} hits in {FilesMatched:n0} files";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _searchCts?.Cancel();
    }

    private bool CanCancel() => IsSearching;

    [RelayCommand(CanExecute = nameof(CanBuildOrRefreshIndex))]
    private async Task BuildOrRefreshIndexAsync()
    {
        await AddCurrentFolderToIndexAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAddCurrentFolderToIndex))]
    private async Task AddCurrentFolderToIndexAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchPath) || !Directory.Exists(SearchPath))
        {
            StatusText = "Choose an existing folder before adding an index.";
            return;
        }

        if (IsCurrentFolderIndexed)
        {
            StatusText = "Current folder is already indexed.";
            return;
        }

        await AddFolderToIndexAsync(SearchPath).ConfigureAwait(true);
    }

    public async Task AddFolderToIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusText = "Choose an existing folder before adding an index.";
            return;
        }

        if (GetIndexedLocation(folder) is { } existing)
        {
            SelectedIndexedLocation = existing;
            StatusText = "Selected folder is already indexed.";
            return;
        }

        var location = CreateIndexedLocationSettings(folder);
        AddOrReplaceIndexedLocation(location);
        SelectedIndexedLocation = location;

        await _indexingService.AddOrUpdateLocationAsync(ToIndexedLocation(location), queueInitialRefresh: true, CancellationToken.None)
            .ConfigureAwait(true);

        UseIndex = true;
        SaveSettings();
        StatusText = "Index updating in background.";
    }

    private bool CanAddCurrentFolderToIndex() =>
        !string.IsNullOrWhiteSpace(SearchPath) && Directory.Exists(SearchPath) && !IsCurrentFolderIndexed;

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

        StatusText = "Index rebuild queued.";
    }

    private bool CanRebuildSelectedIndex() => SelectedIndexedLocation is not null;

    [RelayCommand(CanExecute = nameof(CanClearIndexForCurrentFolder))]
    private async Task ClearIndexForCurrentFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchPath))
            return;

        try
        {
            await _fileIndex.ClearAsync(SearchPath, CancellationToken.None).ConfigureAwait(true);
            RemoveIndexedLocation(SearchPath);
            StatusText = "Index cleared for current folder.";
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't clear index: {ex.Message}";
        }
    }

    private bool CanClearIndexForCurrentFolder() => !string.IsNullOrWhiteSpace(SearchPath);

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedIndex))]
    private async Task RemoveSelectedIndexAsync()
    {
        var location = SelectedIndexedLocation;
        if (location is null)
            return;

        await _indexingService.RemoveLocationAsync(location.Root, CancellationToken.None).ConfigureAwait(true);
        IndexedLocations.Remove(location);
        SelectedIndexedLocation = IndexedLocations.FirstOrDefault();
        SaveSettings();
        StatusText = "Indexed location removed.";
    }

    private bool CanRemoveSelectedIndex() => SelectedIndexedLocation is not null;

    [RelayCommand(CanExecute = nameof(CanPauseBackgroundIndexing))]
    private void PauseBackgroundIndexing()
    {
        _indexingService.Pause();
        IsIndexingPaused = true;
        OnPropertyChanged(nameof(IndexActivityText));
    }

    private bool CanPauseBackgroundIndexing() => !IsIndexingPaused;

    [RelayCommand(CanExecute = nameof(CanResumeBackgroundIndexing))]
    private void ResumeBackgroundIndexing()
    {
        _indexingService.Resume();
        IsIndexingPaused = false;
        OnPropertyChanged(nameof(IndexActivityText));
    }

    private bool CanResumeBackgroundIndexing() => IsIndexingPaused;

    [RelayCommand]
    private void OpenIndexLocation()
    {
        var path = _fileIndex.DatabasePath;
        var folder = Path.GetDirectoryName(path);
        _fileLauncher.RevealInExplorer(File.Exists(path) ? path : folder ?? path);
    }

    public void Dispose()
    {
        _indexingService.StatusChanged -= OnIndexingStatusChanged;
        _searchCts?.Dispose();
        _previewCts?.Dispose();
        _refinementDebounceCts?.Dispose();
    }

    public async Task StartBackgroundIndexingAsync()
    {
        await _indexingService.StartAsync(
            IndexedLocations.Select(ToIndexedLocation),
            CancellationToken.None).ConfigureAwait(true);
    }

    public async Task StopBackgroundIndexingAsync()
    {
        await _indexingService.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ApplyFilePatternPreset(string? pattern) =>
        FileNamePattern = pattern?.Trim() ?? string.Empty;

    [RelayCommand]
    private void ApplyCustomScope(SearchScope? scope)
    {
        if (scope is null)
            return;

        FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty;
        StatusText = $"Scope set to {scope.Name}.";
    }

    public void SaveCustomScope(string name, string fileNamePattern)
    {
        var trimmedName = name.Trim();
        if (string.IsNullOrEmpty(trimmedName))
            return;

        var scope = new SearchScope
        {
            Name = trimmedName,
            FileNamePattern = fileNamePattern.Trim(),
        };

        var existingIndex = -1;
        for (var i = 0; i < CustomScopes.Count; i++)
        {
            if (string.Equals(CustomScopes[i].Name, trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
            CustomScopes[existingIndex] = scope;
        else
            CustomScopes.Add(scope);

        SaveSettings();
        StatusText = $"Saved scope {trimmedName}.";
    }

    [RelayCommand]
    private void RemoveCustomScope(SearchScope? scope)
    {
        if (scope is null)
            return;

        CustomScopes.Remove(scope);
        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanClearCustomScopes))]
    private void ClearCustomScopes()
    {
        CustomScopes.Clear();
        SaveSettings();
    }

    private bool CanClearCustomScopes() => CustomScopes.Count > 0;

    [RelayCommand]
    private void RemoveRecentPath(string? path)
    {
        SearchHistory.Remove(RecentPaths, path);
        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanClearRecentPaths))]
    private void ClearRecentPaths()
    {
        RecentPaths.Clear();
        SaveSettings();
    }

    private bool CanClearRecentPaths() => RecentPaths.Count > 0;

    [RelayCommand]
    private void RemoveRecentQuery(string? query)
    {
        SearchHistory.Remove(RecentQueries, query);
        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanClearRecentQueries))]
    private void ClearRecentQueries()
    {
        RecentQueries.Clear();
        SaveSettings();
    }

    private bool CanClearRecentQueries() => RecentQueries.Count > 0;

    [RelayCommand]
    private void ApplyTheme(string themeName)
    {
        if (Enum.TryParse<AppTheme>(themeName, out var theme))
            _themeService.SetTheme(theme);
    }

    [RelayCommand]
    private void InstallWindowsIntegration()
    {
        try
        {
            _shellIntegrationService.Install();
            StatusText = "Windows integration installed. Pin FileSearch from the Start menu if you want it on the taskbar.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to install Windows integration: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveWindowsIntegration()
    {
        try
        {
            _shellIntegrationService.Remove();
            StatusText = "Windows integration removed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remove Windows integration: {ex.Message}";
        }
    }

    // ----- helpers -----

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
            Recursive = IncludeSubfolders,
            IncludeHidden = false,
            EnableDocumentExtraction = EnableDocumentExtraction,
            SkipUnknownFileTypes = SkipUnknownFileTypes,
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
            BuildIndexWalkerOptions(settings),
            settings.WatchEnabled);

    private WalkerOptions BuildWalkerOptions()
    {
        var include = ParsePatterns(FileNamePattern);

        return new WalkerOptions
        {
            IncludeGlobs = include,
            IncludeExtensions = SkipUnknownFileTypes
                ? BuildKnownTextExtensions()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExcludeExtensions = EnableDocumentExtraction
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : _fileTypeOptions.DocumentExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Recursive = IncludeSubfolders,
            MinFileSizeBytes = (long)Math.Max(0, MinSizeKB) * 1024,
            // No explicit max falls back to the shared default cap so GUI and
            // CLI searches agree; enter a larger value to raise it.
            MaxFileSizeBytes = MaxSizeKB > 0
                ? (long)MaxSizeKB * 1024
                : WalkerOptions.DefaultMaxFileSizeBytes,
            ModifiedAfterUtc = ModifiedAfterEnabled ? ModifiedAfter.ToUniversalTime() : null,
            ModifiedBeforeUtc = ModifiedBeforeEnabled
                ? ModifiedBefore.AddDays(1).AddSeconds(-1).ToUniversalTime() // inclusive end-of-day
                : null,
        };
    }

    private WalkerOptions BuildIndexWalkerOptions(IndexedLocationSettings settings) =>
        new()
        {
            IncludeGlobs = Array.Empty<string>(),
            ExcludeGlobs = Array.Empty<string>(),
            IncludeExtensions = settings.SkipUnknownFileTypes
                ? BuildKnownTextExtensions()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExcludeExtensions = settings.EnableDocumentExtraction
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : _fileTypeOptions.DocumentExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Recursive = settings.Recursive,
            IncludeHidden = settings.IncludeHidden,
            MinFileSizeBytes = 0,
            MaxFileSizeBytes = 0,
            ModifiedAfterUtc = null,
            ModifiedBeforeUtc = null,
        };

    private HashSet<string> BuildKnownTextExtensions()
    {
        var extensions = _extractorRegistry.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in ParseExtensions(AdditionalPlainTextExtensions))
            extensions.Add(extension);
        return extensions;
    }

    public FileTypeOptions BuildFileTypeOptions()
    {
        _fileTypeOptions.AdditionalPlainTextExtensions = ParseExtensions(AdditionalPlainTextExtensions).ToList();
        return _fileTypeOptions;
    }

    internal static string[] ParseExtensions(string raw) => ExtensionList.Parse(raw);

    private void UpdateProgressStatus(SearchProgress progress)
    {
        if (!IsSearching) return;

        var skipped = progress.FilesSkipped > 0 ? $", {progress.FilesSkipped:n0} skipped" : string.Empty;
        var failed = progress.FilesFailed > 0 ? $", {progress.FilesFailed:n0} failed" : string.Empty;
        StatusText = $"Searching... {TotalHits:n0} hits in {FilesMatched:n0} files; {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} scanned{skipped}{failed}";
    }

    private void UpdateIndexProgressStatus(IndexProgress progress)
    {
        if (!IsIndexing) return;

        var failed = progress.FilesFailed > 0 ? $", {progress.FilesFailed:n0} failed" : string.Empty;
        var removed = progress.FilesRemoved > 0 ? $", {progress.FilesRemoved:n0} removed" : string.Empty;
        StatusText = $"Indexing... {progress.FilesIndexed:n0} indexed, {progress.FilesSkippedUnchanged:n0} unchanged, {progress.LinesIndexed:n0} lines{removed}{failed}";
    }

    private void OnIndexingStatusChanged(object? sender, IndexingStatus status)
    {
        _dispatcher.Post(() =>
        {
            IsIndexing = status.IsProcessing || status.QueueLength > 0;
            IsIndexingPaused = status.IsPaused;
            IndexQueueLength = status.QueueLength;
            ActiveIndexingRoot = status.ActiveRoot ?? string.Empty;
            ApplyIndexingRuntimeStatus(status);
            OnPropertyChanged(nameof(IndexActivityText));

            if (!IsSearching)
                StatusText = status.Message;

            // Refresh stats only for a root that just finished processing —
            // the previous behavior re-counted every root (a full scan of the
            // lines table per root) on every idle status and rebuilt the
            // whole IndexedLocations collection.
            if (status.IsProcessing && !string.IsNullOrWhiteSpace(status.ActiveRoot))
            {
                _pendingStatsRoot = status.ActiveRoot;
            }
            else if (!status.IsProcessing && _pendingStatsRoot is { } completedRoot)
            {
                _pendingStatsRoot = null;
                _ = RefreshIndexedLocationStatsAsync(completedRoot);
            }
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
        SaveSettings();
    }

    private async Task LoadPreviewAsync(FileResultViewModel? file)
    {
        _previewCts?.Cancel();
        if (file is null || file.Hits.Count == 0)
        {
            PreviewContent = string.Empty;
            return;
        }

        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        try
        {
            var hitLines = file.Hits.Select(h => h.LineNumber).ToList();
            var content = await _previewService
                .LoadHitsPreviewAsync(file.FullPath, hitLines, contextLines: 3, token)
                .ConfigureAwait(true);
            if (!token.IsCancellationRequested)
                PreviewContent = content;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreviewContent = $"(failed to load preview: {ex.Message})";
        }
    }

    private static readonly char[] s_patternSeparators = { ';', ',' };

    private static string[] ParsePatterns(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(s_patternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ----- search history -----

    private void RecordHistory(string query, string path)
    {
        SearchHistory.PromoteToFront(RecentQueries, query, MaxHistoryEntries);
        SearchHistory.PromoteToFront(RecentPaths, path, MaxHistoryEntries);

        // Persist immediately so history survives a crash.
        SaveSettings();
    }

    /// <summary>Snapshots view-model state into the shared settings. Called
    /// eagerly on changes and once more by the app on exit.</summary>
    public void PersistSettings() => SaveSettings();

    private void SaveSettings()
    {
        _settingsService.Update(settings =>
        {
            settings.RecentQueries = RecentQueries.ToList();
            settings.RecentPaths = RecentPaths.ToList();
            settings.CustomScopes = CustomScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope.Name))
                .Select(scope => new SearchScope
                {
                    Name = scope.Name.Trim(),
                    FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
                })
                .ToList();
            settings.SkipUnknownFileTypes = SkipUnknownFileTypes;
            settings.UseIndex = UseIndex;
            settings.IndexedLocations = IndexedLocations
                .Where(location => !string.IsNullOrWhiteSpace(location.Root))
                .Select(location => new IndexedLocationSettings
                {
                    Root = IndexPath.NormalizeRoot(location.Root),
                    Recursive = location.Recursive,
                    IncludeHidden = location.IncludeHidden,
                    EnableDocumentExtraction = location.EnableDocumentExtraction,
                    SkipUnknownFileTypes = location.SkipUnknownFileTypes,
                    WatchEnabled = location.WatchEnabled,
                    LastIndexedUtcTicks = location.LastIndexedUtcTicks,
                    FileCount = location.FileCount,
                    LineCount = location.LineCount,
                })
                .ToList();
            settings.LastIndexedRoot = string.Empty;
        });
    }

    partial void OnFileNamePatternChanged(string value) =>
        OnPropertyChanged(nameof(FilePatternSummary));

    partial void OnSearchPathChanged(string value) =>
        NotifyIndexCommandStateChanged();

    partial void OnIncludeSubfoldersChanged(bool value) =>
        OnPropertyChanged(nameof(SubfoldersSummary));

    partial void OnMatchCaseChanged(bool value) =>
        OnPropertyChanged(nameof(MatchCaseSummary));

    partial void OnEnableDocumentExtractionChanged(bool value) =>
        OnPropertyChanged(nameof(DocumentExtractionSummary));

    partial void OnSkipUnknownFileTypesChanged(bool value) =>
        OnPropertyChanged(nameof(UnknownFileTypesSummary));

    partial void OnUseIndexChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexSummaryText));
        if (_isInitialized)
            SaveSettings();
    }

    partial void OnIsIndexingChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        BuildOrRefreshIndexCommand.NotifyCanExecuteChanged();
        ClearIndexForCurrentFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnIndexQueueLengthChanged(int value) =>
        OnPropertyChanged(nameof(IndexActivityText));

    partial void OnIsIndexingPausedChanged(bool value) =>
        NotifyIndexingPauseStateChanged();

    partial void OnActiveIndexingRootChanged(string value) =>
        OnPropertyChanged(nameof(IndexActivityText));

    partial void OnSelectedIndexedLocationChanged(IndexedLocationSettings? value)
    {
        RebuildSelectedIndexCommand.NotifyCanExecuteChanged();
        RemoveSelectedIndexCommand.NotifyCanExecuteChanged();
    }

    partial void OnModifiedAfterEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(DateSummary));

    partial void OnModifiedAfterChanged(DateTime value) =>
        OnPropertyChanged(nameof(DateSummary));

    partial void OnModifiedBeforeEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(DateSummary));

    partial void OnModifiedBeforeChanged(DateTime value) =>
        OnPropertyChanged(nameof(DateSummary));

    partial void OnQueryTextChanged(string value) =>
        OnPropertyChanged(nameof(ResultsContextText));

    partial void OnFilesMatchedChanged(int value) =>
        OnPropertyChanged(nameof(ResultsSummaryText));

    partial void OnTotalHitsChanged(int value) =>
        OnPropertyChanged(nameof(ResultsSummaryText));

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
            location.QueuedWorkCount = queuedCount;
            location.IsQueued = queuedCount > 0;
            location.IsIndexingPaused = status.IsPaused && (location.IsIndexing || location.IsQueued);
        }
    }

    private static string GetDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    private void NotifyIndexingPauseStateChanged()
    {
        OnPropertyChanged(nameof(IndexActivityText));
        PauseBackgroundIndexingCommand.NotifyCanExecuteChanged();
        ResumeBackgroundIndexingCommand.NotifyCanExecuteChanged();
    }
}
