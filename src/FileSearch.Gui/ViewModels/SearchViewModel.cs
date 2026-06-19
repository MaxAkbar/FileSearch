using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Search inputs, options, execution, results, and the preview pane.
/// Owns the UseIndex/SkipUnknownFileTypes slice of the persisted settings.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private const int HitsTabIndex = 1;
    public const double MinimumPreviewPaneWidth = 300;
    public const double MaximumPreviewPaneWidth = 720;

    private readonly ISearcher _searcher;
    private readonly IExtractorRegistry _extractorRegistry;
    private readonly IQueryFactory _queryFactory;
    private readonly IFilePreviewService _previewService;
    private readonly IFileLauncher _fileLauncher;
    private readonly ISettingsService _settingsService;
    private readonly IIndexUsageStore? _indexUsageStore;
    private readonly IFileTypeOptionsStore _fileTypeOptionsStore;
    private readonly FileTypeOptions _fileTypeOptions;
    private readonly IFolderPicker _folderPicker;
    private readonly IFileSavePicker? _fileSavePicker;
    private readonly IFileOperationService? _fileOperationService;
    private readonly HistoryViewModel _history;
    private readonly StatusBarViewModel _status;

    private readonly Dictionary<string, FileResultViewModel> _filesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _refinementDebounceCts;
    private readonly bool _isInitialized;
    private bool _isRebuildingFacetOptions;
    private bool _suppressResultViewMaintenance;
    private int _nextResultRank;

    public SearchViewModel(
        ISearcher searcher,
        IExtractorRegistry extractorRegistry,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IFileLauncher fileLauncher,
        ISettingsService settingsService,
        IFileTypeOptionsStore fileTypeOptionsStore,
        IFolderPicker folderPicker,
        HistoryViewModel history,
        StatusBarViewModel status,
        IIndexUsageStore? indexUsageStore = null,
        IFileSavePicker? fileSavePicker = null,
        IFileOperationService? fileOperationService = null)
    {
        _searcher = searcher;
        _extractorRegistry = extractorRegistry;
        _queryFactory = queryFactory;
        _previewService = previewService;
        _fileLauncher = fileLauncher;
        _settingsService = settingsService;
        _indexUsageStore = indexUsageStore;
        _fileTypeOptionsStore = fileTypeOptionsStore;
        _fileTypeOptions = _fileTypeOptionsStore.Load();
        _folderPicker = folderPicker;
        _fileSavePicker = fileSavePicker;
        _fileOperationService = fileOperationService;
        _history = history;
        _status = status;

        // Set up the filtered view used by the "Filter" tab.
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = FilterFiles;
        Files.CollectionChanged += OnFilesChanged;
        _history.FavoriteResults.CollectionChanged += OnFavoriteResultsChanged;

        SelectedSortOption = ResultSortOptions[0];
        SelectedGroupOption = ResultGroupOptions[0];
        RebuildFacetOptions();
        ApplyResultViewShape();

        var saved = _settingsService.Current;
        SkipUnknownFileTypes = saved.SkipUnknownFileTypes;
        UseIndex = saved.UseIndex;
        if (_fileTypeOptions.AdditionalPlainTextExtensions.Count == 0 && !string.IsNullOrWhiteSpace(saved.AdditionalPlainTextExtensions))
        {
            _fileTypeOptions.AdditionalPlainTextExtensions = ParseExtensions(saved.AdditionalPlainTextExtensions).ToList();
            _fileTypeOptionsStore.Save(_fileTypeOptions);
        }
        AdditionalPlainTextExtensions = string.Join("; ", _fileTypeOptions.AdditionalPlainTextExtensions);

        // Seed the input fields with the most recent full saved search when
        // available; older settings files fall back to query/path history.
        if (_history.SavedSearches.Count > 0)
        {
            SelectedSavedSearch = _history.SavedSearches[0];
        }
        else
        {
            if (_history.RecentQueries.Count > 0) QueryText = _history.RecentQueries[0];
            if (_history.RecentPaths.Count > 0) SearchPath = _history.RecentPaths[0];
        }

        _isInitialized = true;
    }

    public ObservableCollection<FileResultViewModel> Files { get; } = new();

    public ObservableCollection<ResultFacetOption> FileTypeFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> FolderFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> ModifiedFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> SourceFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> SizeFacetOptions { get; } = new();

    public IReadOnlyList<ResultSortOption> ResultSortOptions { get; } =
        new[]
        {
            new ResultSortOption(ResultSortMode.Relevance, "Relevance"),
            new ResultSortOption(ResultSortMode.Recency, "Recency"),
            new ResultSortOption(ResultSortMode.Filename, "Filename"),
            new ResultSortOption(ResultSortMode.HitCount, "Hit count"),
        };

    public IReadOnlyList<ResultGroupOption> ResultGroupOptions { get; } =
        new[]
        {
            new ResultGroupOption(ResultGroupMode.File, "File"),
            new ResultGroupOption(ResultGroupMode.Folder, "Folder"),
            new ResultGroupOption(ResultGroupMode.FileType, "File type"),
            new ResultGroupOption(ResultGroupMode.ModifiedDate, "Modified date"),
        };

    /// <summary>
    /// Filtered view of <see cref="Files"/>. The results list binds to
    /// <c>Files</c> directly; WPF resolves to this same default view, so
    /// changing the filter here filters what the list shows without
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
    [ObservableProperty] private string _excludeFileNamePattern = string.Empty;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private string? _selectedRecentPath;

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
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private int _totalHits;
    [ObservableProperty] private int _filesMatched;
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _previewContent = string.Empty;
    [ObservableProperty] private bool _isPreviewPaneVisible = true;
    [ObservableProperty] private double _previewPaneWidth = 360;
    [ObservableProperty] private int _selectedDetailsTabIndex;
    [ObservableProperty] private ResultSortOption? _selectedSortOption;
    [ObservableProperty] private ResultGroupOption? _selectedGroupOption;
    [ObservableProperty] private ResultFacetOption? _selectedFileTypeFacet;
    [ObservableProperty] private ResultFacetOption? _selectedFolderFacet;
    [ObservableProperty] private ResultFacetOption? _selectedModifiedFacet;
    [ObservableProperty] private ResultFacetOption? _selectedSourceFacet;
    [ObservableProperty] private ResultFacetOption? _selectedSizeFacet;

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
        ExportResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FilesVisible));
        ExportResultsCommand.NotifyCanExecuteChanged();
        if (!_suppressResultViewMaintenance)
            RebuildFacetOptions();
    }

    private void OnFavoriteResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var file in Files)
            file.IsFavorite = _history.IsFavorite(file.FullPath);
    }

    [RelayCommand]
    private void ClearRefinement() => RefinementQuery = string.Empty;

    [RelayCommand]
    private void ClearResultFacets()
    {
        SelectFacetValue(FileTypeFacetOptions, option => SelectedFileTypeFacet = option, ResultFacetOption.AllValue);
        SelectFacetValue(FolderFacetOptions, option => SelectedFolderFacet = option, ResultFacetOption.AllValue);
        SelectFacetValue(ModifiedFacetOptions, option => SelectedModifiedFacet = option, ResultFacetOption.AllValue);
        SelectFacetValue(SourceFacetOptions, option => SelectedSourceFacet = option, ResultFacetOption.AllValue);
        SelectFacetValue(SizeFacetOptions, option => SelectedSizeFacet = option, ResultFacetOption.AllValue);
    }

    private bool FilterFiles(object obj)
    {
        if (obj is not FileResultViewModel file) return true;

        if (!FacetAllows(SelectedFileTypeFacet, file.Extension)) return false;
        if (!FacetAllows(SelectedFolderFacet, file.Directory)) return false;
        if (!FacetAllows(SelectedModifiedFacet, file.ModifiedDateFacet)) return false;
        if (!FacetAllows(SelectedSourceFacet, file.SourceGroup)) return false;
        if (!FacetAllows(SelectedSizeFacet, file.SizeFacet)) return false;

        if (string.IsNullOrWhiteSpace(RefinementQuery)) return true;

        var needle = RefinementQuery;
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        if (file.FileName.Contains(needle, ci)) return true;
        foreach (var hit in file.Hits)
            if (hit.LineContent.Contains(needle, ci))
                return true;
        return false;
    }

    private static bool FacetAllows(ResultFacetOption? facet, string value) =>
        facet is null ||
        string.Equals(facet.Value, ResultFacetOption.AllValue, StringComparison.Ordinal) ||
        string.Equals(facet.Value, NormalizeFacetValue(value), StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private FileResultViewModel? _selectedFile;

    [ObservableProperty]
    private SavedSearchSettings? _selectedSavedSearch;

    public bool HasSelectedFile => SelectedFile is not null;

    public string PreviewPaneToggleText => "Preview";

    public string ResultsSummaryText => $"{FilesMatched:n0} files · {TotalHits:n0} hits";

    /// <summary>Heading above the results list ("Find …" once a query is set).</summary>
    public string ResultsContextText =>
        string.IsNullOrWhiteSpace(QueryText) ? "Results" : $"Find “{QueryText.Trim()}”";

    public string FilePatternSummary =>
        string.IsNullOrWhiteSpace(FileNamePattern) ? "All files" : FileNamePattern;

    public string ExcludePatternSummary =>
        string.IsNullOrWhiteSpace(ExcludeFileNamePattern) ? "No excludes" : ExcludeFileNamePattern;

    public string MatchCaseSummary => MatchCase ? "Match case on" : "Match case off";

    public string SubfoldersSummary => IncludeSubfolders ? "Subfolders on" : "Subfolders off";

    public string DocumentExtractionSummary =>
        EnableDocumentExtraction ? "Office/PDF on" : "Office/PDF off";

    public string UnknownFileTypesSummary =>
        SkipUnknownFileTypes ? "Known types only" : "Unknown text allowed";

    public string IndexSummaryText => UseIndex ? "Use index on" : "Use index off";

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
        RenameResultCommand.NotifyCanExecuteChanged();
        DeleteResultCommand.NotifyCanExecuteChanged();
        if (value is not null)
            SelectedDetailsTabIndex = HitsTabIndex;
        _ = LoadPreviewAsync(value);
    }

    partial void OnSelectedSavedSearchChanged(SavedSearchSettings? value) =>
        ApplySavedSearch(value);

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

    partial void OnSelectedSortOptionChanged(ResultSortOption? value) => ApplyResultViewShape();

    partial void OnSelectedGroupOptionChanged(ResultGroupOption? value) => ApplyResultViewShape();

    partial void OnSelectedFileTypeFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedFolderFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedModifiedFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedSourceFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedSizeFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    // ----- commands -----

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneVisible = !IsPreviewPaneVisible;

    [RelayCommand(CanExecute = nameof(CanCopyPreview))]
    private void CopyPreview()
    {
        _fileLauncher.CopyToClipboard(PreviewContent);
        _status.Text = "Copied preview to clipboard.";
    }

    private bool CanCopyPreview() => !string.IsNullOrEmpty(PreviewContent);

    [RelayCommand(CanExecute = nameof(CanCopyFileContent))]
    private async Task CopyFileContentAsync()
    {
        var file = SelectedFile;
        if (file is null) return;

        try
        {
            _status.Text = "Reading file content...";
            var text = await _previewService
                .LoadFullTextAsync(file.FullPath, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(text))
            {
                _status.Text = "No extractable text content for this file type.";
                return;
            }

            _fileLauncher.CopyToClipboard(text);
            _status.Text = $"Copied file content ({text.Length:n0} characters) to clipboard.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't copy content: {ex.Message}";
        }
    }

    private bool CanCopyFileContent() => HasSelectedFile;

    [RelayCommand(CanExecute = nameof(CanExportResults))]
    private async Task ExportResultsAsync(string? formatName)
    {
        if (_fileSavePicker is null)
            return;

        var format = ParseExportFormat(formatName);
        var visibleFiles = VisibleResultFiles().ToList();
        if (visibleFiles.Count == 0)
            return;

        var path = _fileSavePicker.PickSaveFile(
            "Export search results",
            "CSV (*.csv)|*.csv|JSON (*.json)|*.json|JSON Lines (*.jsonl)|*.jsonl|Markdown (*.md)|*.md",
            BuildDefaultExportFileName(format));
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var text = RenderExport(format, visibleFiles);
            await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
            _status.Text = $"Exported {visibleFiles.Count:n0} result files to {path}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't export results: {ex.Message}";
        }
    }

    private bool CanExportResults(string? formatName) => _fileSavePicker is not null && FilesVisible > 0;

    [RelayCommand(CanExecute = nameof(CanTogglePinResult))]
    private void TogglePinResult(FileResultViewModel? file)
    {
        if (file is null)
            return;

        _settingsService.Update(settings =>
        {
            for (var i = settings.QuickSearchPinnedPaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(settings.QuickSearchPinnedPaths[i], file.FullPath, StringComparison.OrdinalIgnoreCase))
                    settings.QuickSearchPinnedPaths.RemoveAt(i);
            }

            if (!file.IsPinned)
                settings.QuickSearchPinnedPaths.Insert(0, file.FullPath);
        });

        file.IsPinned = !file.IsPinned;
        _status.Text = file.IsPinned ? "Pinned result." : "Unpinned result.";
    }

    private bool CanTogglePinResult(FileResultViewModel? file) => file is not null;

    [RelayCommand(CanExecute = nameof(CanToggleFavoriteResult))]
    private void ToggleFavoriteResult(FileResultViewModel? file)
    {
        if (file is null)
            return;

        file.IsFavorite = _history.ToggleFavorite(file.FullPath);
        _status.Text = file.IsFavorite ? "Added favorite." : "Removed favorite.";
    }

    private bool CanToggleFavoriteResult(FileResultViewModel? file) => file is not null;

    [RelayCommand]
    private async Task OpenFavoriteResultAsync(FavoriteResultSettings? favorite)
    {
        if (favorite is null || string.IsNullOrWhiteSpace(favorite.Path))
            return;

        _fileLauncher.Open(favorite.Path);
        if (_indexUsageStore is null)
            return;

        try
        {
            await _indexUsageStore.RecordFileOpenedAsync(favorite.Path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanRenameResult))]
    private async Task RenameResultAsync(FileResultViewModel? file)
    {
        if (file is null || _fileOperationService is null)
            return;

        var oldPath = file.FullPath;
        var result = await _fileOperationService
            .RenameFileAsync(oldPath, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.NewPath))
        {
            _status.Text = result.Message;
            return;
        }

        _filesByPath.Remove(oldPath);
        file.UpdatePath(result.NewPath);
        _filesByPath[result.NewPath] = file;
        UpdatePinnedPath(oldPath, result.NewPath);
        _history.UpdateFavoritePath(oldPath, result.NewPath);
        file.IsFavorite = _history.IsFavorite(result.NewPath);
        RebuildFacetOptions();
        RefreshFilesView();
        if (ReferenceEquals(SelectedFile, file))
            _ = LoadPreviewAsync(file);
        _status.Text = result.Message;
    }

    private bool CanRenameResult(FileResultViewModel? file) => _fileOperationService is not null && file is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteResult))]
    private async Task DeleteResultAsync(FileResultViewModel? file)
    {
        if (file is null || _fileOperationService is null)
            return;

        var result = await _fileOperationService
            .MoveFileToRecycleBinAsync(file.FullPath, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            _status.Text = result.Message;
            return;
        }

        _filesByPath.Remove(file.FullPath);
        RemovePinnedPath(file.FullPath);
        _history.RemoveFavoritePath(file.FullPath);
        var removedHits = file.HitCount;
        Files.Remove(file);
        TotalHits = Math.Max(0, TotalHits - removedHits);
        FilesMatched = Files.Count;
        if (ReferenceEquals(SelectedFile, file))
        {
            SelectedFile = null;
            PreviewContent = string.Empty;
        }

        RebuildFacetOptions();
        RefreshFilesView();
        _status.Text = result.Message;
    }

    private bool CanDeleteResult(FileResultViewModel? file) => _fileOperationService is not null && file is not null;

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
        _history.RecordSearch(CreateSavedSearch());

        RefinementQuery = string.Empty;
        _filesByPath.Clear();
        Files.Clear();
        SelectedFile = null;
        PreviewContent = string.Empty;
        TotalHits = 0;
        FilesMatched = 0;
        ElapsedText = "—";
        _nextResultRank = 0;
        RebuildFacetOptions();

        Query query;
        try
        {
            query = _queryFactory.Build(QueryText, SearchMode, MatchCase);
        }
        catch (Exception ex)
        {
            _status.Text = $"Invalid query: {ex.Message}";
            return;
        }

        IsSearching = true;
        _status.Text = "Searching...";
        SearchCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ExcludeFileExtensionPatternCommand.NotifyCanExecuteChanged();

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
                    _status.Text = message;
                },
                QueryText,
                SearchMode);

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
            _status.Text = string.IsNullOrEmpty(routeStatus)
                ? $"Done — {TotalHits} hits in {FilesMatched} files"
                : $"{routeStatus}; done — {TotalHits} hits in {FilesMatched} files";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            _status.Text = $"Canceled — {TotalHits} hits in {FilesMatched} files";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _status.Text = $"Error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
            SearchCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ExcludeFileExtensionPatternCommand.NotifyCanExecuteChanged();
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

        _suppressResultViewMaintenance = true;
        try
        {
            while (drained < maxPerDrain && pendingHits.TryDequeue(out var hit))
            {
                if (!_filesByPath.TryGetValue(hit.Path, out var file))
                {
                    var recordOpened = _indexUsageStore is null
                        ? null
                        : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
                    file = new FileResultViewModel(hit.Path, _fileLauncher, recordOpened, _nextResultRank++)
                    {
                        IsPinned = IsPinned(hit.Path),
                        IsFavorite = _history.IsFavorite(hit.Path),
                    };
                    _filesByPath[hit.Path] = file;
                    Files.Add(file);
                }

                file.AddHit(hit);
                total++;
                drained++;
            }
        }
        finally
        {
            _suppressResultViewMaintenance = false;
        }

        if (drained == 0)
            return;

        TotalHits = total;
        FilesMatched = Files.Count;
        RebuildFacetOptions();
        RefreshFilesView();
        _status.Text = $"Searching... {TotalHits:n0} hits in {FilesMatched:n0} files";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _searchCts?.Cancel();
    }

    private bool CanCancel() => IsSearching;

    [RelayCommand]
    private void ApplyFilePatternPreset(string? pattern) =>
        FileNamePattern = pattern?.Trim() ?? string.Empty;

    [RelayCommand(CanExecute = nameof(CanExcludeFileExtensionPattern))]
    private async Task ExcludeFileExtensionPatternAsync(FileResultViewModel? file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.ExtensionPattern))
            return;

        ExcludeFileNamePattern = AppendPattern(ExcludeFileNamePattern, file.ExtensionPattern);
        _status.Text = $"Added exclude pattern {file.ExtensionPattern}; reapplying search...";
        await SearchAsync().ConfigureAwait(true);
    }

    private bool CanExcludeFileExtensionPattern(FileResultViewModel? file) =>
        !IsSearching && !string.IsNullOrWhiteSpace(file?.ExtensionPattern);

    [RelayCommand]
    private void ApplyCustomScope(SearchScope? scope)
    {
        if (scope is null)
            return;

        FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty;
        _status.Text = $"Scope set to {scope.Name}.";
    }

    private SavedSearchSettings CreateSavedSearch() =>
        new()
        {
            QueryText = QueryText,
            SearchPath = SearchPath,
            FileNamePattern = FileNamePattern,
            ExcludeFileNamePattern = ExcludeFileNamePattern,
            IncludeSubfolders = IncludeSubfolders,
            SearchMode = SearchMode,
            MatchCase = MatchCase,
            EnableDocumentExtraction = EnableDocumentExtraction,
            SkipUnknownFileTypes = SkipUnknownFileTypes,
            UseIndex = UseIndex,
            MinSizeKB = MinSizeKB,
            MaxSizeKB = MaxSizeKB,
            ModifiedAfterEnabled = ModifiedAfterEnabled,
            ModifiedAfter = ModifiedAfter,
            ModifiedBeforeEnabled = ModifiedBeforeEnabled,
            ModifiedBefore = ModifiedBefore,
            AdditionalPlainTextExtensions = AdditionalPlainTextExtensions,
        };

    private void ApplySavedSearch(SavedSearchSettings? search)
    {
        if (search is null)
            return;

        SearchPath = search.SearchPath;
        QueryText = search.QueryText;
        FileNamePattern = search.FileNamePattern;
        ExcludeFileNamePattern = search.ExcludeFileNamePattern;
        IncludeSubfolders = search.IncludeSubfolders;
        SearchMode = search.SearchMode;
        MatchCase = search.MatchCase;
        EnableDocumentExtraction = search.EnableDocumentExtraction;
        SkipUnknownFileTypes = search.SkipUnknownFileTypes;
        UseIndex = search.UseIndex;
        MinSizeKB = Math.Max(0, search.MinSizeKB);
        MaxSizeKB = Math.Max(0, search.MaxSizeKB);
        ModifiedAfterEnabled = search.ModifiedAfterEnabled;
        ModifiedAfter = search.ModifiedAfter == default ? DateTime.Today.AddDays(-7) : search.ModifiedAfter;
        ModifiedBeforeEnabled = search.ModifiedBeforeEnabled;
        ModifiedBefore = search.ModifiedBefore == default ? DateTime.Today : search.ModifiedBefore;
        AdditionalPlainTextExtensions = search.AdditionalPlainTextExtensions?.Trim() ?? string.Empty;

        if (_isInitialized)
            _status.Text = "Saved search loaded.";
    }

    public void Dispose()
    {
        _searchCts?.Dispose();
        _previewCts?.Dispose();
        _refinementDebounceCts?.Dispose();
        _history.FavoriteResults.CollectionChanged -= OnFavoriteResultsChanged;
    }

    // ----- helpers -----

    private void ApplyResultViewShape()
    {
        using (FilesView.DeferRefresh())
        {
            FilesView.SortDescriptions.Clear();
            FilesView.GroupDescriptions.Clear();

            switch (SelectedGroupOption?.Value ?? ResultGroupMode.File)
            {
                case ResultGroupMode.Folder:
                    FilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.Directory)));
                    break;
                case ResultGroupMode.FileType:
                    FilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.FileTypeGroup)));
                    break;
                case ResultGroupMode.ModifiedDate:
                    FilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.ModifiedDateGroup)));
                    break;
            }

            switch (SelectedSortOption?.Value ?? ResultSortMode.Relevance)
            {
                case ResultSortMode.Recency:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.ModifiedSortTicks), ListSortDirection.Descending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                    break;
                case ResultSortMode.Filename:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.FileName), ListSortDirection.Ascending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.Directory), ListSortDirection.Ascending));
                    break;
                case ResultSortMode.HitCount:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.HitCount), ListSortDirection.Descending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                    break;
                default:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.BestScore), ListSortDirection.Descending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                    break;
            }
        }

        OnPropertyChanged(nameof(FilesVisible));
        ExportResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnFacetSelectionChanged()
    {
        if (_isRebuildingFacetOptions)
            return;

        RefreshFilesView();
    }

    private void RebuildFacetOptions()
    {
        _isRebuildingFacetOptions = true;
        try
        {
            var fileTypeValue = SelectedFileTypeFacet?.Value ?? ResultFacetOption.AllValue;
            var folderValue = SelectedFolderFacet?.Value ?? ResultFacetOption.AllValue;
            var modifiedValue = SelectedModifiedFacet?.Value ?? ResultFacetOption.AllValue;
            var sourceValue = SelectedSourceFacet?.Value ?? ResultFacetOption.AllValue;
            var sizeValue = SelectedSizeFacet?.Value ?? ResultFacetOption.AllValue;

            ReplaceFacetOptions(
                FileTypeFacetOptions,
                BuildFacetOptions(
                    Files,
                    file => NormalizeFacetValue(file.Extension),
                    file => string.IsNullOrWhiteSpace(file.Extension) ? "No extension" : $".{file.Extension}",
                    "All types"));
            ReplaceFacetOptions(
                FolderFacetOptions,
                BuildFacetOptions(
                    Files,
                    file => NormalizeFacetValue(file.Directory),
                    file => string.IsNullOrWhiteSpace(file.Directory) ? "No folder" : file.Directory,
                    "All folders",
                    maxValues: 50));
            ReplaceFacetOptions(
                ModifiedFacetOptions,
                BuildFixedFacetOptions(
                    Files,
                    file => file.ModifiedDateFacet,
                    "Any date",
                    new[]
                    {
                        ("today", "Today"),
                        ("last7", "Last 7 days"),
                        ("last30", "Last 30 days"),
                        ("older", "Older"),
                        ("unknown", "Unknown"),
                    }));
            ReplaceFacetOptions(
                SourceFacetOptions,
                BuildFacetOptions(
                    Files,
                    file => NormalizeFacetValue(file.SourceGroup),
                    file => file.SourceGroup,
                    "All sources"));
            ReplaceFacetOptions(
                SizeFacetOptions,
                BuildFixedFacetOptions(
                    Files,
                    file => file.SizeFacet,
                    "Any size",
                    new[]
                    {
                        ("small", "Under 100 KB"),
                        ("medium", "100 KB to 10 MB"),
                        ("large", "10 MB and larger"),
                        ("unknown", "Unknown"),
                    }));

            SelectFacetValue(FileTypeFacetOptions, option => SelectedFileTypeFacet = option, fileTypeValue);
            SelectFacetValue(FolderFacetOptions, option => SelectedFolderFacet = option, folderValue);
            SelectFacetValue(ModifiedFacetOptions, option => SelectedModifiedFacet = option, modifiedValue);
            SelectFacetValue(SourceFacetOptions, option => SelectedSourceFacet = option, sourceValue);
            SelectFacetValue(SizeFacetOptions, option => SelectedSizeFacet = option, sizeValue);
        }
        finally
        {
            _isRebuildingFacetOptions = false;
        }
    }

    private static IEnumerable<ResultFacetOption> BuildFacetOptions(
        IEnumerable<FileResultViewModel> files,
        Func<FileResultViewModel, string> valueSelector,
        Func<FileResultViewModel, string> labelSelector,
        string allLabel,
        int maxValues = 25)
    {
        var list = files.ToList();
        yield return new ResultFacetOption(ResultFacetOption.AllValue, allLabel, list.Count);

        foreach (var group in list
                     .GroupBy(valueSelector, StringComparer.OrdinalIgnoreCase)
                     .Select(group => new
                     {
                         Value = group.Key,
                         Label = group.Select(labelSelector).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? group.Key,
                         Count = group.Count(),
                     })
                     .OrderByDescending(group => group.Count)
                     .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
                     .Take(maxValues))
        {
            yield return new ResultFacetOption(group.Value, group.Label, group.Count);
        }
    }

    private static IEnumerable<ResultFacetOption> BuildFixedFacetOptions(
        IEnumerable<FileResultViewModel> files,
        Func<FileResultViewModel, string> valueSelector,
        string allLabel,
        IEnumerable<(string Value, string Label)> values)
    {
        var list = files.ToList();
        var counts = list
            .GroupBy(valueSelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        yield return new ResultFacetOption(ResultFacetOption.AllValue, allLabel, list.Count);
        foreach (var value in values)
        {
            if (counts.TryGetValue(value.Value, out var count) && count > 0)
                yield return new ResultFacetOption(value.Value, value.Label, count);
        }
    }

    private static void ReplaceFacetOptions(
        ObservableCollection<ResultFacetOption> target,
        IEnumerable<ResultFacetOption> source)
    {
        target.Clear();
        foreach (var option in source)
            target.Add(option);
    }

    private static void SelectFacetValue(
        ObservableCollection<ResultFacetOption> options,
        Action<ResultFacetOption?> select,
        string value)
    {
        select(options.FirstOrDefault(option =>
            string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault());
    }

    private IEnumerable<FileResultViewModel> VisibleResultFiles() =>
        FilesView.Cast<FileResultViewModel>();

    private bool IsPinned(string path) =>
        _settingsService.Current.QuickSearchPinnedPaths.Any(pinned =>
            string.Equals(pinned, path, StringComparison.OrdinalIgnoreCase));

    private void UpdatePinnedPath(string oldPath, string newPath)
    {
        _settingsService.Update(settings =>
        {
            for (var i = 0; i < settings.QuickSearchPinnedPaths.Count; i++)
            {
                if (string.Equals(settings.QuickSearchPinnedPaths[i], oldPath, StringComparison.OrdinalIgnoreCase))
                    settings.QuickSearchPinnedPaths[i] = newPath;
            }
        });
    }

    private void RemovePinnedPath(string path)
    {
        _settingsService.Update(settings =>
        {
            for (var i = settings.QuickSearchPinnedPaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(settings.QuickSearchPinnedPaths[i], path, StringComparison.OrdinalIgnoreCase))
                    settings.QuickSearchPinnedPaths.RemoveAt(i);
            }
        });
    }

    private static string NormalizeFacetValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "__none" : value.Trim();

    private static string BuildDefaultExportFileName(SearchResultExportFormat format)
    {
        var extension = format switch
        {
            SearchResultExportFormat.Csv => "csv",
            SearchResultExportFormat.JsonLines => "jsonl",
            SearchResultExportFormat.Markdown => "md",
            _ => "json",
        };
        return $"FileSearch-results.{extension}";
    }

    private static SearchResultExportFormat ParseExportFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "csv" => SearchResultExportFormat.Csv,
            "jsonl" or "jsonlines" or "json-lines" => SearchResultExportFormat.JsonLines,
            "markdown" or "md" => SearchResultExportFormat.Markdown,
            _ => SearchResultExportFormat.Json,
        };

    private string RenderExport(SearchResultExportFormat format, IReadOnlyList<FileResultViewModel> files) =>
        format switch
        {
            SearchResultExportFormat.Csv => RenderExportCsv(files),
            SearchResultExportFormat.JsonLines => RenderExportJsonLines(files),
            SearchResultExportFormat.Markdown => RenderExportMarkdown(files),
            _ => RenderExportJson(files),
        };

    private string RenderExportJson(IReadOnlyList<FileResultViewModel> files)
    {
        var document = new SearchResultsExportDocument(
            QueryText.Trim(),
            SearchPath,
            DateTime.UtcNow,
            files.Count,
            files.Sum(file => file.HitCount),
            files.Select(ToExportFile).ToArray());
        return JsonSerializer.Serialize(document, s_exportJsonOptions);
    }

    private string RenderExportJsonLines(IReadOnlyList<FileResultViewModel> files)
    {
        var sb = new StringBuilder();
        foreach (var hit in files.SelectMany(file => file.Hits.Select(hit => ToExportHit(file, hit))))
            sb.AppendLine(JsonSerializer.Serialize(hit, s_exportJsonLinesOptions));
        return sb.ToString();
    }

    private string RenderExportCsv(IReadOnlyList<FileResultViewModel> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,fileName,folder,extension,sizeBytes,modifiedUtc,source,hitCount,lineNumber,kind,line");
        foreach (var file in files)
        {
            foreach (var hit in file.Hits)
            {
                sb.Append(CsvField(file.FullPath)).Append(',');
                sb.Append(CsvField(file.FileName)).Append(',');
                sb.Append(CsvField(file.Directory)).Append(',');
                sb.Append(CsvField(file.Extension)).Append(',');
                sb.Append(file.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
                sb.Append(CsvField(file.ModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',');
                sb.Append(CsvField(file.SourceGroup)).Append(',');
                sb.Append(file.HitCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(CsvField(hit.Kind.ToString())).Append(',');
                sb.AppendLine(CsvField(hit.LineContent));
            }
        }

        return sb.ToString();
    }

    private string RenderExportMarkdown(IReadOnlyList<FileResultViewModel> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FileSearch Results");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Query: {QueryText.Trim()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Folder: {SearchPath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Files: {files.Count:n0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Hits: {files.Sum(file => file.HitCount):n0}");

        foreach (var file in files)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {file.FullPath}");
            sb.AppendLine();
            foreach (var hit in file.Hits)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- L{hit.LineNumber}: {hit.LineContent.Trim()}");
        }

        return sb.ToString();
    }

    private static readonly SearchValues<char> s_csvSpecialChars = SearchValues.Create(",\"\n\r");

    private static readonly JsonSerializerOptions s_exportJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions s_exportJsonLinesOptions = new();

    private static string CsvField(string value)
    {
        if (!value.AsSpan().ContainsAny(s_csvSpecialChars))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static ExportFile ToExportFile(FileResultViewModel file) =>
        new(
            file.FullPath,
            file.FileName,
            file.Directory,
            file.Extension,
            file.SizeBytes,
            file.ModifiedUtc,
            file.SourceGroup,
            file.HitCount,
            file.Hits.Select(hit => ToExportHit(file, hit)).ToArray());

    private static ExportHit ToExportHit(FileResultViewModel file, Hit hit) =>
        new(
            file.FullPath,
            file.FileName,
            file.Directory,
            file.Extension,
            file.SizeBytes,
            file.ModifiedUtc,
            file.SourceGroup,
            file.HitCount,
            hit.LineNumber,
            hit.Kind.ToString(),
            hit.LineContent);

    private WalkerOptions BuildWalkerOptions()
    {
        var include = ParsePatterns(FileNamePattern);
        var exclude = ParsePatterns(ExcludeFileNamePattern);

        return new WalkerOptions
        {
            IncludeGlobs = include,
            ExcludeGlobs = exclude,
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

    /// <summary>Walker options for index builds of a saved location; strips
    /// search-only filters and applies the location's own toggles.</summary>
    internal WalkerOptions BuildIndexWalkerOptions(IndexedLocationSettings settings) =>
        new()
        {
            IncludeGlobs = Array.Empty<string>(),
            ExcludeGlobs = Array.Empty<string>(),
            IncludeExtensions = BuildIndexIncludedExtensions(settings),
            ExcludeExtensions = BuildIndexExcludedExtensions(settings),
            IncludeDirectories = IndexFilterListSettings.ParseFolders(settings.IncludedFolders)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ExcludeDirectories = BuildIndexExcludedDirectories(settings),
            Recursive = settings.Recursive,
            IncludeHidden = settings.IncludeHidden,
            MinFileSizeBytes = 0,
            MaxFileSizeBytes = 0,
            ModifiedAfterUtc = null,
            ModifiedBeforeUtc = null,
        };

    private HashSet<string> BuildIndexIncludedExtensions(IndexedLocationSettings settings)
    {
        var explicitIncludes = ExtensionList.Parse(settings.IncludedExtensions);
        if (explicitIncludes.Length > 0)
            return explicitIncludes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return settings.SkipUnknownFileTypes
            ? BuildKnownTextExtensions()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> BuildIndexExcludedExtensions(IndexedLocationSettings settings)
    {
        var extensions = ExtensionList.Parse(settings.ExcludedExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!settings.EnableDocumentExtraction)
        {
            foreach (var extension in _fileTypeOptions.DocumentExtensions)
                extensions.Add(extension);
        }

        return extensions;
    }

    private static HashSet<string> BuildIndexExcludedDirectories(IndexedLocationSettings settings)
    {
        var directories = WalkerOptions.DefaultExcludeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in IndexFilterListSettings.ParseFolders(settings.ExcludedFolders))
            directories.Add(folder);
        return directories;
    }

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
        _status.Text = $"Searching... {TotalHits:n0} hits in {FilesMatched:n0} files; {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} scanned{skipped}{failed}";
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
            var hitLines = file.Hits
                .Where(h => h.Kind == HitKind.Content && h.LineNumber > 0)
                .Select(h => h.LineNumber)
                .ToList();
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

    private static string AppendPattern(string raw, string pattern)
    {
        var trimmed = pattern.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return raw.Trim();

        var existing = ParsePatterns(raw).ToList();
        if (existing.Any(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase)))
            return string.Join("; ", existing);

        existing.Add(trimmed);
        return string.Join("; ", existing);
    }

    private static string[] ParsePatterns(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(s_patternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Persists this view model's slice of the settings.</summary>
    public void SaveOptions()
    {
        _settingsService.Update(settings =>
        {
            settings.SkipUnknownFileTypes = SkipUnknownFileTypes;
            settings.UseIndex = UseIndex;
        });
    }

    partial void OnFileNamePatternChanged(string value) =>
        OnPropertyChanged(nameof(FilePatternSummary));

    partial void OnExcludeFileNamePatternChanged(string value) =>
        OnPropertyChanged(nameof(ExcludePatternSummary));

    partial void OnIncludeSubfoldersChanged(bool value) =>
        OnPropertyChanged(nameof(SubfoldersSummary));

    partial void OnMatchCaseChanged(bool value) =>
        OnPropertyChanged(nameof(MatchCaseSummary));

    partial void OnEnableDocumentExtractionChanged(bool value) =>
        OnPropertyChanged(nameof(DocumentExtractionSummary));

    partial void OnSkipUnknownFileTypesChanged(bool value)
    {
        OnPropertyChanged(nameof(UnknownFileTypesSummary));

        // Persist eagerly like UseIndex — the old monolith saved this on
        // every flow; without this, a crash silently reverts the toggle.
        if (_isInitialized)
            SaveOptions();
    }

    partial void OnUseIndexChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexSummaryText));
        if (_isInitialized)
            SaveOptions();
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

    partial void OnSearchPathChanged(string value)
    {
        if (!string.Equals(SelectedRecentPath, value, StringComparison.OrdinalIgnoreCase))
            SelectedRecentPath = value;
    }

    partial void OnSelectedRecentPathChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !string.Equals(SearchPath, value, StringComparison.OrdinalIgnoreCase))
        {
            SearchPath = value;
        }
    }

    partial void OnFilesMatchedChanged(int value) =>
        OnPropertyChanged(nameof(ResultsSummaryText));

    partial void OnTotalHitsChanged(int value) =>
        OnPropertyChanged(nameof(ResultsSummaryText));
}
