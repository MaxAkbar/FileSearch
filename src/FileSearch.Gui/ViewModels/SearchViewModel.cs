using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
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
using FileSearch.WindowsOcr;

namespace FileSearch.Gui.ViewModels;

public sealed partial class SearchTargetOption : ObservableObject
{
    public SearchTargetOption(SearchTarget value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public SearchTarget Value { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Search inputs, options, execution, results, and the preview pane.
/// Owns the UseIndex/SkipUnknownFileTypes slice of the persisted settings.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private const int HitsTabIndex = 1;

    /// <summary>Context radius for "Show whole file": large enough for nearly any
    /// source file, bounded so a huge log can't flood the preview.</summary>
    private const int WholeFileContextLines = 20_000;

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
    private readonly IGroundedAnswerBuilder _groundedAnswerBuilder;
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
    private bool _updatingSearchTargets;
    private int _nextResultRank;
    private bool _hasMailResults;

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
        IFileOperationService? fileOperationService = null,
        IGroundedAnswerBuilder? groundedAnswerBuilder = null)
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
        _groundedAnswerBuilder = groundedAnswerBuilder ?? new GroundedAnswerBuilder();
        _history = history;
        _status = status;

        // Set up the filtered view used by the "Filter" tab.
        FilesView = new ResultCollectionView(Files);
        FilesView.Filter = FilterFiles;
        Files.CollectionChanged += OnFilesChanged;
        _history.FavoriteResults.CollectionChanged += OnFavoriteResultsChanged;

        SelectedSortOption = ResultSortOptions[0];
        SelectedGroupOption = ResultGroupOptions.First(option => option.Value == ResultGroupMode.Folder);
        foreach (var option in SearchTargetOptions)
            option.PropertyChanged += OnSearchTargetOptionPropertyChanged;
        SearchTargetOptions[0].IsSelected = true;
        RebuildFacetOptions();
        ApplyResultViewShape();

        var saved = _settingsService.Current;
        SkipUnknownFileTypes = saved.SkipUnknownFileTypes;
        EnableImageOcr = saved.EnableImageOcr;
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
        _history.UpdateActiveScope(FileNamePattern);

        _isInitialized = true;
    }

    public ObservableCollection<FileResultViewModel> Files { get; } = new();

    public ObservableCollection<ResultFacetOption> FileTypeFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> FolderFacetOptions { get; } = new();

    /// <summary>Folder facet options narrowed by <see cref="FolderFacetFilterText"/> (the
    /// search field at the top of the Folder filter dropdown).</summary>
    public ObservableCollection<ResultFacetOption> FilteredFolderFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> ModifiedFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> SourceFacetOptions { get; } = new();

    public ObservableCollection<ResultFacetOption> SizeFacetOptions { get; } = new();

    public ObservableCollection<QueryChipViewModel> QueryChips { get; } = new();

    public bool HasQueryChips => QueryChips.Count > 0;

    public IReadOnlyList<ResultSortOption> ResultSortOptions { get; } =
        new[]
        {
            new ResultSortOption(ResultSortMode.Relevance, "Relevance"),
            new ResultSortOption(ResultSortMode.Filename, "Name"),
            new ResultSortOption(ResultSortMode.Recency, "Modified"),
            new ResultSortOption(ResultSortMode.Size, "Size"),
            new ResultSortOption(ResultSortMode.HitCount, "Hits"),
            new ResultSortOption(ResultSortMode.FileType, "Type"),
        };

    public IReadOnlyList<ResultGroupOption> ResultGroupOptions { get; } =
        new[]
        {
            new ResultGroupOption(ResultGroupMode.File, "None"),
            new ResultGroupOption(ResultGroupMode.Folder, "Folder"),
            new ResultGroupOption(ResultGroupMode.FileType, "Type"),
            new ResultGroupOption(ResultGroupMode.ModifiedDate, "Date"),
            new ResultGroupOption(ResultGroupMode.Source, "Source"),
        };

    /// <summary>
    /// Filtered, sorted and grouped view of <see cref="Files"/>. The results
    /// list binds to this view without changing the underlying collection.
    /// </summary>
    public ICollectionView FilesView { get; }

    /// <summary>Count of files currently passing the filter.</summary>
    public int FilesVisible =>
        (FilesView as ListCollectionView)?.Count ?? Files.Count;

    public IReadOnlyList<QueryMode> AvailableModes { get; } =
        new[] { QueryMode.Unified, QueryMode.Boolean, QueryMode.Regex, QueryMode.PlainText };

    public IReadOnlyList<SearchTargetOption> SearchTargetOptions { get; } =
        new[]
        {
            new SearchTargetOption(SearchTarget.Content, "Contents"),
            new SearchTargetOption(SearchTarget.FileNames, "File names"),
            new SearchTargetOption(SearchTarget.FolderNames, "Folder names"),
        };

    // --- search inputs (Main tab) ---
    [ObservableProperty] private string _searchPath = Environment.CurrentDirectory;
    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private string _fileNamePattern = string.Empty;
    [ObservableProperty] private string _excludeFileNamePattern = string.Empty;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private string? _selectedRecentPath;

    // --- options (Options tab) ---
    [ObservableProperty] private QueryMode _searchMode = QueryMode.Unified;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _enableDocumentExtraction = true;
    [ObservableProperty] private bool _enableImageOcr;
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

    /// <summary>Hits the search found beyond <see cref="MaxRetainedHits"/>; counted, not shown.</summary>
    [ObservableProperty] private long _hiddenHits;
    [ObservableProperty] private int _filesMatched;
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _previewContent = string.Empty;
    [ObservableProperty] private ImageOcrPreviewViewModel? _imageOcrPreview;
    [ObservableProperty] private bool _isPreviewPaneVisible = true;
    [ObservableProperty] private bool _isSearchOptionsVisible;
    [ObservableProperty] private bool _isResultFiltersVisible;
    [ObservableProperty] private bool _isResultSortAndGroupVisible;
    [ObservableProperty] private bool _isCompactResultLayout;
    [ObservableProperty] private string _folderFacetFilterText = string.Empty;
    [ObservableProperty] private double _previewPaneWidth = 468;
    [ObservableProperty] private int _selectedDetailsTabIndex;
    [ObservableProperty] private ResultSortOption? _selectedSortOption;
    [ObservableProperty] private ResultGroupOption? _selectedGroupOption;
    [ObservableProperty] private ResultFacetOption? _selectedFileTypeFacet;
    [ObservableProperty] private ResultFacetOption? _selectedFolderFacet;
    [ObservableProperty] private ResultFacetOption? _selectedModifiedFacet;
    [ObservableProperty] private ResultFacetOption? _selectedSourceFacet;
    [ObservableProperty] private ResultFacetOption? _selectedSizeFacet;

    // --- preview code view (parsed rows + match navigator) ---
    [ObservableProperty] private IReadOnlyList<PreviewLineViewModel> _previewLines = Array.Empty<PreviewLineViewModel>();
    [ObservableProperty] private PreviewLineViewModel? _currentPreviewLine;
    [ObservableProperty] private bool _isPreviewWrapEnabled = true;
    [ObservableProperty] private bool _isWholeFilePreview;
    private PreviewLineViewModel[] _previewMatchLines = Array.Empty<PreviewLineViewModel>();
    private PreviewLineViewModel? _markedPreviewLine;

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
        NotifyFilesViewChanged();
    }

    private void NotifyFilesViewChanged()
    {
        RefreshResultGroupStates();
        OnPropertyChanged(nameof(FilesVisible));
        ExportResultsCommand.NotifyCanExecuteChanged();
        CopyGroundedAnswerCommand.NotifyCanExecuteChanged();
    }

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FilesVisible));
        ExportResultsCommand.NotifyCanExecuteChanged();
        CopyGroundedAnswerCommand.NotifyCanExecuteChanged();
        if (!_suppressResultViewMaintenance)
            RebuildFacetOptions();
    }

    private void OnFavoriteResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var file in Files)
            file.IsFavorite = !file.IsStoreMessage && _history.IsFavorite(file.FullPath);
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

    [RelayCommand]
    private void ClearResultFacet(string? key)
    {
        switch (key)
        {
            case "type":
                SelectFacetValue(FileTypeFacetOptions, option => SelectedFileTypeFacet = option, ResultFacetOption.AllValue);
                break;
            case "folder":
                SelectFacetValue(FolderFacetOptions, option => SelectedFolderFacet = option, ResultFacetOption.AllValue);
                break;
            case "date":
                SelectFacetValue(ModifiedFacetOptions, option => SelectedModifiedFacet = option, ResultFacetOption.AllValue);
                break;
            case "source":
                SelectFacetValue(SourceFacetOptions, option => SelectedSourceFacet = option, ResultFacetOption.AllValue);
                break;
            case "size":
                SelectFacetValue(SizeFacetOptions, option => SelectedSizeFacet = option, ResultFacetOption.AllValue);
                break;
        }
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

    [ObservableProperty]
    private WorkspaceSettings? _selectedWorkspace;

    public bool HasSelectedFile => SelectedFile is not null;

    public string PreviewPaneToggleText => "Preview";

    public bool HasImageOcrPreview => ImageOcrPreview is not null;

    public IReadOnlyList<SearchTarget> CurrentSearchTargets => SearchTargetOptions
        .Where(option => option.IsSelected)
        .Select(option => option.Value)
        .ToArray();

    public SearchTarget CurrentSearchTarget => CurrentSearchTargets.FirstOrDefault(SearchTarget.Content);

    public bool IsContentSearch => CurrentSearchTargets is [SearchTarget.Content];

    public string ResultsSummaryText => $"{FilesMatched:n0} {ResultItemNounPlural} · {TotalHits:n0} hits";

    /// <summary>Large results heading ("1,057 matches"); the query context until results arrive.</summary>
    public string ResultsHeadlineText =>
        FilesMatched == 0
            ? ResultsContextText
            : IsContentSearch
                ? $"{TotalHits:n0} {(TotalHits == 1 ? "match" : "matches")}"
                : $"{FilesMatched:n0} {(FilesMatched == 1 ? "item" : "items")}";

    /// <summary>Muted line beside the heading ("in 199 files").</summary>
    public string ResultsSublineText =>
        FilesMatched == 0
            ? string.Empty
            : (IsContentSearch ? $"in {FilesMatched:n0} {(FilesMatched == 1 ? ResultItemNounSingular : ResultItemNounPlural)}" : string.Empty) +
              (HiddenHits > 0
                  ? $"{(IsContentSearch ? " · " : string.Empty)}first {TotalHits:n0} of {MaxRetainedHits + HiddenHits:n0} shown"
                  : string.Empty);

    /// <summary>The ".*" toggle in the query box. Off returns to structured (Unified) matching.</summary>
    public bool IsRegexMode
    {
        get => SearchMode == QueryMode.Regex;
        set
        {
            if (value)
                SearchMode = QueryMode.Regex;
            else if (SearchMode == QueryMode.Regex)
                SearchMode = QueryMode.Unified;
        }
    }

    /// <summary>Current grouping as shown on the View button ("By folder").</summary>
    public string ResultViewButtonText => (SelectedGroupOption?.Value ?? ResultGroupMode.File) switch
    {
        ResultGroupMode.Folder => "By folder",
        ResultGroupMode.FileType => "By type",
        ResultGroupMode.ModifiedDate => "By date",
        ResultGroupMode.Source => "By source",
        _ => "Flat list",
    };

    /// <summary>
    /// True when the list is actually showing folder groups (the cards then
    /// leave the folder to the group header). False while a search streams,
    /// because grouping is applied only once it finishes.
    /// </summary>
    public bool IsGroupedByFolder => SelectedGroupOption?.Value == ResultGroupMode.Folder && !_resultGroupingDeferred;

    public bool HasPreviewLines => PreviewLines.Count > 0;

    public int PreviewMatchCount => _previewMatchLines.Length;

    public bool HasPreviewMatches => _previewMatchLines.Length > 0;

    /// <summary>Navigator label in the preview pane ("Match 1 of 2").</summary>
    public string PreviewMatchPositionText
    {
        get
        {
            if (_previewMatchLines.Length == 0)
                return HasPreviewLines ? "No line matches" : string.Empty;

            var index = CurrentPreviewLine is null ? -1 : IndexOfPreviewMatch(CurrentPreviewLine);
            return index < 0
                ? $"{_previewMatchLines.Length:n0} {(_previewMatchLines.Length == 1 ? "match" : "matches")}"
                : $"Match {index + 1:n0} of {_previewMatchLines.Length:n0}";
        }
    }

    public string PreviewCurrentLineText =>
        CurrentPreviewLine?.LineNumber is { } line ? $"Line {line:n0}" : string.Empty;

    /// <summary>Footer of the code view ("Showing lines 43–59").</summary>
    public string PreviewRangeText
    {
        get
        {
            int? first = null;
            int? last = null;
            foreach (var line in PreviewLines)
            {
                if (line.LineNumber is not { } number)
                    continue;
                first ??= number;
                last = number;
            }

            return first is null
                ? string.Empty
                : first == last
                    ? $"Showing line {first:n0}"
                    : $"Showing lines {first:n0}–{last:n0}";
        }
    }

    public string WholeFileToggleText => IsWholeFilePreview ? "Show matches only" : "Show whole file";

    /// <summary>Primary preview action ("Open at line 46").</summary>
    public string OpenPreviewTargetText =>
        CurrentPreviewLine?.LineNumber is { } line ? $"Open at line {line:n0}" : "Open";

    public IReadOnlyList<ResultFacetChip> ActiveResultFacetChips => BuildActiveResultFacetChips();

    public bool HasActiveResultFacets => ActiveResultFacetChips.Count > 0;

    public int ActiveResultFacetCount => ActiveResultFacetChips.Count;

    public bool IsComfortableResultLayout => !IsCompactResultLayout;

    /// <summary>True when sort or group deviates from the defaults (Relevance / None);
    /// drives the accent dot on the Sort &amp; Group button.</summary>
    public bool HasCustomResultArrangement =>
        (SelectedSortOption?.Value ?? ResultSortMode.Relevance) != ResultSortMode.Relevance ||
        (SelectedGroupOption?.Value ?? ResultGroupMode.File) != ResultGroupMode.File;

    /// <summary>Summary line in the Sort &amp; Group panel ("Sorted by name · grouped by folder").</summary>
    public string ResultArrangementSummaryText
    {
        get
        {
            var sort = SelectedSortOption?.Value ?? ResultSortMode.Relevance;
            var group = SelectedGroupOption?.Value ?? ResultGroupMode.File;
            var text = sort == ResultSortMode.Relevance
                ? "Sorted by relevance"
                : $"Sorted by {SelectedSortOption?.Label.ToLowerInvariant()}";
            if (group != ResultGroupMode.File)
                text += $" · grouped by {SelectedGroupOption?.Label.ToLowerInvariant()}";
            return text;
        }
    }

    public bool HasFileTypeFacetSelection => IsFacetSelection(SelectedFileTypeFacet);

    public bool HasFolderFacetSelection => IsFacetSelection(SelectedFolderFacet);

    public bool HasModifiedFacetSelection => IsFacetSelection(SelectedModifiedFacet);

    public bool HasSourceFacetSelection => IsFacetSelection(SelectedSourceFacet);

    public bool HasSizeFacetSelection => IsFacetSelection(SelectedSizeFacet);

    public bool HasNameFilterSelection =>
        !string.IsNullOrWhiteSpace(FileNamePattern) || !string.IsNullOrWhiteSpace(ExcludeFileNamePattern);

    public string FileTypeFacetButtonText => FacetButtonText("Type", SelectedFileTypeFacet);

    public string FolderFacetButtonText =>
        IsFacetSelection(SelectedFolderFacet)
            ? $"Folder: {FolderLeafName(SelectedFolderFacet!.Label)}"
            : "Folder";

    public string ModifiedFacetButtonText => FacetButtonText("Date", SelectedModifiedFacet);

    public string SourceFacetButtonText => FacetButtonText("Source", SelectedSourceFacet);

    public string SizeFacetButtonText => FacetButtonText("Size", SelectedSizeFacet);

    public string NameFilterButtonText
    {
        get
        {
            if (!HasNameFilterSelection)
                return "Name";
            var include = string.IsNullOrWhiteSpace(FileNamePattern) ? "*.*" : FileNamePattern.Trim();
            return string.IsNullOrWhiteSpace(ExcludeFileNamePattern)
                ? include
                : $"{include}  −  {ExcludeFileNamePattern.Trim()}";
        }
    }

    private static bool IsFacetSelection(ResultFacetOption? facet) =>
        facet is not null && !string.Equals(facet.Value, ResultFacetOption.AllValue, StringComparison.Ordinal);

    private static string FacetButtonText(string field, ResultFacetOption? facet) =>
        IsFacetSelection(facet) ? $"{field}: {facet!.Label}" : field;

    private static string FolderLeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(s_pathSeparators);
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }

    private static readonly char[] s_pathSeparators = { '\\', '/' };

    /// <summary>Heading above the results list ("Find …" once a query is set).</summary>
    public string ResultsContextText =>
        string.IsNullOrWhiteSpace(QueryText)
            ? "Results"
            : IsContentSearch
                ? $"Find “{QueryText.Trim()}”"
                : CurrentSearchTargets.Contains(SearchTarget.Content)
                    ? $"Find “{QueryText.Trim()}” in selected areas"
                    : $"Find names matching “{QueryText.Trim()}”";

    public string SearchTargetSummary => string.Join(", ", SearchTargetOptions
        .Where(option => option.IsSelected)
        .Select(option => option.DisplayName));

    public string QueryPlaceholderText => IsContentSearch
        ? "Search text or fields like type:pdf modified:last-month"
        : CurrentSearchTargets.Contains(SearchTarget.Content)
            ? "Search contents, file names, or folder names"
            : "Search file or folder names";

    private string ResultItemNounSingular => IsContentSearch && !_hasMailResults ? "file" : "item";
    private string ResultItemNounPlural => IsContentSearch && !_hasMailResults ? "files" : "items";

    public string FilePatternSummary =>
        string.IsNullOrWhiteSpace(FileNamePattern) ? "All files" : FileNamePattern;

    public string ExcludePatternSummary =>
        string.IsNullOrWhiteSpace(ExcludeFileNamePattern) ? "No excludes" : ExcludeFileNamePattern;

    public string MatchCaseSummary => MatchCase ? "Match case on" : "Match case off";

    public string SubfoldersSummary => IncludeSubfolders ? "Subfolders on" : "Subfolders off";

    public string DocumentExtractionSummary =>
        EnableDocumentExtraction ? "Documents on" : "Documents off";

    public string ImageOcrSummary =>
        EnableImageOcr ? "Image OCR on" : "Image OCR off";

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
        OpenPreviewTargetCommand.NotifyCanExecuteChanged();
        ToggleWholeFilePreviewCommand.NotifyCanExecuteChanged();
        IsWholeFilePreview = false;
        if (value is not null)
            SelectedDetailsTabIndex = HitsTabIndex;
        _ = LoadPreviewAsync(value);
    }

    partial void OnSelectedSavedSearchChanged(SavedSearchSettings? value) =>
        ApplySavedSearch(value);

    partial void OnSelectedWorkspaceChanged(WorkspaceSettings? value) =>
        _ = ApplyWorkspaceAsync(value);

    partial void OnPreviewContentChanged(string value)
    {
        CopyPreviewCommand.NotifyCanExecuteChanged();
        RebuildPreviewLines();
    }

    partial void OnCurrentPreviewLineChanged(PreviewLineViewModel? value)
    {
        if (_markedPreviewLine is not null)
            _markedPreviewLine.IsCurrent = false;
        _markedPreviewLine = value;
        if (value is not null)
            value.IsCurrent = true;

        OnPropertyChanged(nameof(PreviewMatchPositionText));
        OnPropertyChanged(nameof(PreviewCurrentLineText));
        OnPropertyChanged(nameof(OpenPreviewTargetText));
    }

    partial void OnIsWholeFilePreviewChanged(bool value) =>
        OnPropertyChanged(nameof(WholeFileToggleText));

    private void RebuildPreviewLines()
    {
        var lines = PreviewLineViewModel.Parse(PreviewContent, SelectedFile?.Hits);
        _previewMatchLines = lines.Where(line => line.IsHit).ToArray();
        _markedPreviewLine = null;
        PreviewLines = lines;
        CurrentPreviewLine = _previewMatchLines.Length > 0 ? _previewMatchLines[0] : null;

        OnPropertyChanged(nameof(HasPreviewLines));
        OnPropertyChanged(nameof(PreviewMatchCount));
        OnPropertyChanged(nameof(HasPreviewMatches));
        OnPropertyChanged(nameof(PreviewMatchPositionText));
        OnPropertyChanged(nameof(PreviewRangeText));
        PreviousPreviewMatchCommand.NotifyCanExecuteChanged();
        NextPreviewMatchCommand.NotifyCanExecuteChanged();
    }

    private int IndexOfPreviewMatch(PreviewLineViewModel line)
    {
        for (var i = 0; i < _previewMatchLines.Length; i++)
        {
            if (ReferenceEquals(_previewMatchLines[i], line))
                return i;
        }

        return -1;
    }

    partial void OnImageOcrPreviewChanged(ImageOcrPreviewViewModel? value) =>
        OnPropertyChanged(nameof(HasImageOcrPreview));

    partial void OnIsPreviewPaneVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewPaneToggleText));
    }

    partial void OnIsResultFiltersVisibleChanged(bool value)
    {
        if (value)
            IsResultSortAndGroupVisible = false;
    }

    partial void OnIsResultSortAndGroupVisibleChanged(bool value)
    {
        if (value)
            IsResultFiltersVisible = false;
    }

    partial void OnPreviewPaneWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinimumPreviewPaneWidth, MaximumPreviewPaneWidth);
        if (Math.Abs(value - clamped) > 0.1)
        {
            PreviewPaneWidth = clamped;
        }
    }

    partial void OnSelectedSortOptionChanged(ResultSortOption? value)
    {
        ApplyResultViewShape();
        NotifyResultArrangementChanged();
    }

    partial void OnSelectedGroupOptionChanged(ResultGroupOption? value)
    {
        ApplyResultViewShape();
        NotifyResultArrangementChanged();
    }

    partial void OnIsCompactResultLayoutChanged(bool value) =>
        OnPropertyChanged(nameof(IsComfortableResultLayout));

    partial void OnFolderFacetFilterTextChanged(string value) =>
        RefreshFilteredFolderFacetOptions();

    private void NotifyResultArrangementChanged()
    {
        OnPropertyChanged(nameof(HasCustomResultArrangement));
        OnPropertyChanged(nameof(ResultArrangementSummaryText));
        OnPropertyChanged(nameof(ResultViewButtonText));
        OnPropertyChanged(nameof(IsGroupedByFolder));
    }

    private void RefreshFilteredFolderFacetOptions()
    {
        var filter = FolderFacetFilterText;
        FilteredFolderFacetOptions.Clear();
        foreach (var option in FolderFacetOptions)
        {
            var isAllRow = string.Equals(option.Value, ResultFacetOption.AllValue, StringComparison.Ordinal);
            if (isAllRow ||
                string.IsNullOrWhiteSpace(filter) ||
                option.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredFolderFacetOptions.Add(option);
            }
        }
    }

    partial void OnSelectedFileTypeFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedFolderFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedModifiedFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedSourceFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    partial void OnSelectedSizeFacetChanged(ResultFacetOption? value) => OnFacetSelectionChanged();

    private void OnSearchTargetOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingSearchTargets || e.PropertyName != nameof(SearchTargetOption.IsSelected))
            return;

        if (!SearchTargetOptions.Any(option => option.IsSelected) && sender is SearchTargetOption option)
        {
            _updatingSearchTargets = true;
            option.IsSelected = true;
            _updatingSearchTargets = false;
        }

        NotifySearchTargetsChanged();
    }

    private void SetSearchTargets(IEnumerable<SearchTarget> targets)
    {
        var selectedTargets = targets.ToHashSet();
        if (selectedTargets.Count == 0)
            selectedTargets.Add(SearchTarget.Content);

        _updatingSearchTargets = true;
        foreach (var option in SearchTargetOptions)
            option.IsSelected = selectedTargets.Contains(option.Value);
        _updatingSearchTargets = false;
        NotifySearchTargetsChanged();
    }

    private void NotifySearchTargetsChanged()
    {
        OnPropertyChanged(nameof(CurrentSearchTargets));
        OnPropertyChanged(nameof(CurrentSearchTarget));
        OnPropertyChanged(nameof(IsContentSearch));
        OnPropertyChanged(nameof(SearchTargetSummary));
        OnPropertyChanged(nameof(QueryPlaceholderText));
        OnPropertyChanged(nameof(ResultsSummaryText));
        OnPropertyChanged(nameof(ResultsContextText));
        OnPropertyChanged(nameof(ResultsHeadlineText));
        OnPropertyChanged(nameof(ResultsSublineText));
    }

    // ----- commands -----

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneVisible = !IsPreviewPaneVisible;

    [RelayCommand]
    private void ClearQuery() => QueryText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanStepPreviewMatch))]
    private void PreviousPreviewMatch() => StepPreviewMatch(-1);

    [RelayCommand(CanExecute = nameof(CanStepPreviewMatch))]
    private void NextPreviewMatch() => StepPreviewMatch(1);

    private bool CanStepPreviewMatch() => _previewMatchLines.Length > 1;

    private void StepPreviewMatch(int delta)
    {
        var count = _previewMatchLines.Length;
        if (count == 0)
            return;

        var index = CurrentPreviewLine is null ? -1 : IndexOfPreviewMatch(CurrentPreviewLine);
        var next = index < 0
            ? (delta > 0 ? 0 : count - 1)
            : ((index + delta) % count + count) % count;
        CurrentPreviewLine = _previewMatchLines[next];
    }

    [RelayCommand(CanExecute = nameof(CanOpenPreviewTarget))]
    private Task OpenPreviewTargetAsync() =>
        SelectedFile is { } file
            ? file.OpenAtLineAsync(CurrentPreviewLine?.LineNumber)
            : Task.CompletedTask;

    private bool CanOpenPreviewTarget() => HasSelectedFile;

    [RelayCommand(CanExecute = nameof(CanToggleWholeFilePreview))]
    private Task ToggleWholeFilePreviewAsync()
    {
        IsWholeFilePreview = !IsWholeFilePreview;
        return LoadPreviewAsync(SelectedFile);
    }

    private bool CanToggleWholeFilePreview() => SelectedFile is { IsDirectory: false };

    /// <summary>"+" on the Locations section: pin a folder for one-click searching.</summary>
    [RelayCommand]
    private void AddLocation()
    {
        var folder = _folderPicker.PickFolder("Add a location", SearchPath);
        if (folder is not null)
            _history.AddLocation(folder);
    }

    [RelayCommand]
    private void UseLocation(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            SearchPath = path;
    }

    /// <summary>Pins the folder being searched to Locations.</summary>
    [RelayCommand]
    private void PinSearchFolder()
    {
        if (string.IsNullOrWhiteSpace(SearchPath))
            return;

        _history.AddLocation(SearchPath);
        _status.Text = $"Added {SearchPath.Trim()} to locations.";
    }

    /// <summary>"+" on Saved searches: keep the current query and options without running them.</summary>
    [RelayCommand]
    private void SaveCurrentSearch()
    {
        if (string.IsNullOrWhiteSpace(QueryText))
        {
            _status.Text = "Type a query before saving a search.";
            return;
        }

        _history.RecordSearch(CreateSavedSearch());
        _status.Text = "Saved search.";
    }

    [RelayCommand]
    private void HideSearchOptions() => IsSearchOptionsVisible = false;

    [RelayCommand]
    private void SetComfortableResultLayout() => IsCompactResultLayout = false;

    [RelayCommand]
    private void SetCompactResultLayout() => IsCompactResultLayout = true;

    /// <summary>Restores the matching options to their defaults ("Reset to defaults"
    /// in the search options panel). Scope, patterns, and facets are left alone.</summary>
    [RelayCommand]
    private void ResetSearchOptions()
    {
        SetSearchTargets([SearchTarget.Content]);
        SearchMode = QueryMode.Unified;
        MatchCase = false;
        IncludeSubfolders = true;
        EnableDocumentExtraction = true;
        EnableImageOcr = false;
        UseIndex = false;
    }

    [RelayCommand]
    private void HideResultFilters() => IsResultFiltersVisible = false;

    [RelayCommand]
    private void HideResultSortAndGroup() => IsResultSortAndGroupVisible = false;

    [RelayCommand]
    private void RemoveQueryChip(QueryChipViewModel? chip)
    {
        if (chip is null || string.IsNullOrWhiteSpace(QueryText))
            return;

        QueryText = RemoveQueryChipText(QueryText, chip);
    }

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
            var text = file.MailMessage is { } mail
                ? await Task.Run(() => string.Join(Environment.NewLine,
                    OutlookMailReader.ReadMessageLines(file.FullPath, mail, CancellationToken.None).Select(line => line.Content))).ConfigureAwait(true)
                : await _previewService.LoadFullTextAsync(file.FullPath, CancellationToken.None).ConfigureAwait(true);

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

    private bool CanCopyFileContent() => HasSelectedFile && SelectedFile?.IsDirectory != true;

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

    [RelayCommand(CanExecute = nameof(CanCopyGroundedAnswer))]
    private void CopyGroundedAnswer()
    {
        var visibleFiles = VisibleResultFiles().ToList();
        if (visibleFiles.Count == 0)
            return;

        var evidence = visibleFiles
            .SelectMany(file => file.Hits.Select((hit, index) =>
                GroundedAnswerEvidence.FromHit(file.FullPath, file.SearchRank, index + 1, hit)))
            .ToArray();
        var draft = _groundedAnswerBuilder.Build(QueryText, evidence);
        _fileLauncher.CopyToClipboard(draft.Markdown);
        _status.Text = $"Copied grounded answer draft with {draft.Citations.Count:n0} citation(s).";
    }

    private bool CanCopyGroundedAnswer() => FilesVisible > 0;

    [RelayCommand(CanExecute = nameof(CanTogglePinResult))]
    private void TogglePinResult(FileResultViewModel? file)
    {
        if (file is null || file.IsStoreMessage)
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

    private bool CanTogglePinResult(FileResultViewModel? file) => file is not null && !file.IsStoreMessage;

    [RelayCommand(CanExecute = nameof(CanToggleFavoriteResult))]
    private void ToggleFavoriteResult(FileResultViewModel? file)
    {
        if (file is null || file.IsStoreMessage)
            return;

        file.IsFavorite = _history.ToggleFavorite(file.FullPath);
        _status.Text = file.IsFavorite ? "Added favorite." : "Removed favorite.";
    }

    private bool CanToggleFavoriteResult(FileResultViewModel? file) => file is not null && !file.IsStoreMessage;

    [RelayCommand]
    private void SaveWorkspace()
    {
        var workspace = new WorkspaceSettings
        {
            Name = BuildWorkspaceName(),
            UpdatedUtc = DateTime.UtcNow,
            Search = CreateSavedSearch(),
            CustomScopes = _history.CustomScopes
                .Select(scope => new SearchScope
                {
                    Name = scope.Name,
                    FileNamePattern = scope.FileNamePattern,
                })
                .ToList(),
            FavoriteResults = _history.FavoriteResults
                .Select(favorite => new FavoriteResultSettings
                {
                    Path = favorite.Path,
                    AddedUtc = favorite.AddedUtc,
                })
                .ToList(),
            PinnedPaths = _settingsService.Current.QuickSearchPinnedPaths.ToList(),
            QuickSearchSelectedIndexedRoots = _settingsService.Current.QuickSearchSelectedIndexedRoots.ToList(),
            ResultSort = (SelectedSortOption?.Value ?? ResultSortMode.Relevance).ToString(),
            ResultGroup = (SelectedGroupOption?.Value ?? ResultGroupMode.File).ToString(),
            RefinementQuery = RefinementQuery,
        };

        _history.SaveWorkspace(workspace);
    }

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
        if (file is null || file.IsStoreMessage || _fileOperationService is null)
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

    private bool CanRenameResult(FileResultViewModel? file) =>
        _fileOperationService is not null && file is not null && !file.IsDirectory && !file.IsStoreMessage;

    [RelayCommand(CanExecute = nameof(CanDeleteResult))]
    private async Task DeleteResultAsync(FileResultViewModel? file)
    {
        if (file is null || file.IsStoreMessage || _fileOperationService is null)
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
            ImageOcrPreview = null;
        }

        RebuildFacetOptions();
        RefreshFilesView();
        _status.Text = result.Message;
    }

    private bool CanDeleteResult(FileResultViewModel? file) =>
        _fileOperationService is not null && file is not null && !file.IsDirectory && !file.IsStoreMessage;

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
        foreach (var file in Files)
            file.MetadataLoaded -= OnResultMetadataLoaded;
        _filesByPath.Clear();
        _hasMailResults = false;
        Files.Clear();
        SelectedFile = null;
        PreviewContent = string.Empty;
        ImageOcrPreview = null;
        TotalHits = 0;
        HiddenHits = 0;
        FilesMatched = 0;
        ElapsedText = "—";
        _nextResultRank = 0;
        _resultGroupStates.Clear();
        _pendingFileBatches.Clear();
        _drainBatchSize = 256;
        _nextResultViewRefreshUtc = DateTime.MinValue;
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
        SetResultGroupingDeferred(true);
        SearchCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ExcludeFileExtensionPatternCommand.NotifyCanExecuteChanged();

        var stopwatch = Stopwatch.StartNew();
        var routeStatus = string.Empty;
        try
        {
            IProgress<SearchProgress> progress = new Progress<SearchProgress>(UpdateProgressStatus);
            IProgress<string> routeProgress = new Progress<string>(message =>
            {
                if (!IsSearching)
                    return;

                routeStatus = message;
                _status.Text = message;
            });
            var searchTargets = CurrentSearchTargets;

            // Consume the hit stream on the thread pool and flush to the UI
            // in timed batches. The hand-off channel is bounded so the engine
            // waits whenever the UI falls behind; an unbounded queue let a
            // broad query pile up gigabytes of hits faster than the
            // dispatcher could apply them, and the GC pauses froze the UI.
            var pendingHits = Channel.CreateBounded<PendingHit>(new BoundedChannelOptions(PendingHitCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            long hitsFound = 0;
            var consumer = Task.Run(async () =>
            {
                try
                {
                    foreach (var searchTarget in searchTargets)
                    {
                        var request = new SearchRequest(
                            query,
                            new[] { SearchPath },
                            BuildWalkerOptions(searchTarget),
                            progress.Report,
                            UseIndex,
                            routeProgress.Report,
                            QueryText,
                            SearchMode,
                            searchTarget);

                        await foreach (var hit in _searcher.SearchAsync(request, token).ConfigureAwait(false))
                        {
                            // Past the cap the stream is only counted, so memory
                            // and UI work stay bounded however broad the query.
                            if (Interlocked.Increment(ref hitsFound) > MaxRetainedHits)
                                continue;

                            await pendingHits.Writer
                                .WriteAsync(new PendingHit(hit, ResolveIsDirectory(hit)), token)
                                .ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    pendingHits.Writer.TryComplete();
                }
            }, token);

            var dispatcherLag = TimeSpan.Zero;
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    _pendingFileBatches.Clear();
                    break;
                }
                // Time between drains beyond the requested delay is the layout
                // and rendering the previous drain caused; budget for it too.
                DrainPendingHits(pendingHits.Reader, Interlocked.Read(ref hitsFound), dispatcherLag);
                if (consumer.IsCompleted && !pendingHits.Reader.TryPeek(out _) && _pendingFileBatches.Count == 0)
                    break;
                var delayStarted = Stopwatch.GetTimestamp();
                await Task.Delay(DrainInterval).ConfigureAwait(true);
                dispatcherLag = Stopwatch.GetElapsedTime(delayStarted) - DrainInterval;
            }

            await consumer.ConfigureAwait(true); // surface cancellation/errors
            UpdateHiddenHits(Interlocked.Read(ref hitsFound));

            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            _status.Text = string.IsNullOrEmpty(routeStatus)
                ? $"Done — {DescribeHitTotals()}"
                : $"{routeStatus}; done — {DescribeHitTotals()}";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            _status.Text = $"Canceled — {DescribeHitTotals()}";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _status.Text = $"Error: {ex.Message}";
        }
        finally
        {
            // Streaming refreshes are throttled; settle the final
            // sort/facets/grouping exactly once however the search ended.
            _nextResultViewRefreshUtc = DateTime.MinValue;
            RebuildFacetOptions();
            if (!SetResultGroupingDeferred(false))
                RefreshFilesView();

            IsSearching = false;
            SearchCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ExcludeFileExtensionPatternCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSearch() => !IsSearching;

    /// <summary>
    /// Applies queued hits to the UI collections. Runs on the UI thread.
    /// Hits are grouped by file first so each row raises its change
    /// notifications once per drain instead of once per hit, and the batch
    /// size adapts so a drain stays within a frame budget.
    /// </summary>
    private void DrainPendingHits(ChannelReader<PendingHit> pendingHits, long hitsFound, TimeSpan dispatcherLag)
    {
        var drainStarted = Stopwatch.GetTimestamp();
        if (_pendingFileBatches.Count == 0)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, (List<Hit> Hits, bool? IsDirectory)>(_filesByPath.Comparer);
            var staged = 0;
            while (staged < _drainBatchSize && pendingHits.TryRead(out var pending))
            {
                if (!groups.TryGetValue(pending.Hit.ResultKey, out var group))
                {
                    group = (new List<Hit>(), pending.IsDirectory);
                    groups[pending.Hit.ResultKey] = group;
                    order.Add(pending.Hit.ResultKey);
                }

                group.Hits.Add(pending.Hit);
                staged++;
            }

            foreach (var path in order)
                _pendingFileBatches.Enqueue(new PendingFileBatch(path, groups[path].Hits, groups[path].IsDirectory));
        }

        if (_pendingFileBatches.Count == 0)
        {
            UpdateHiddenHits(hitsFound);
            return;
        }

        var drained = 0;
        _suppressResultViewMaintenance = true;
        try
        {
            // Keep the unapplied portion of the batch for the next UI turn.
            // Even after the writer completes, every staged hit must be drained.
            while (_pendingFileBatches.TryPeek(out var batch) && Stopwatch.GetElapsedTime(drainStarted) < DrainBudget)
            {
                var (path, hits, isDirectory) = batch;
                _pendingFileBatches.Dequeue();
                drained += hits.Count;
                if (_filesByPath.TryGetValue(path, out var file))
                {
                    file.AddHits(hits);
                    continue;
                }

                var recordOpened = _indexUsageStore is null
                    ? null
                    : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
                file = new FileResultViewModel(hits[0].Path, _fileLauncher, recordOpened, _nextResultRank++, isDirectory)
                {
                    IsPinned = hits[0].MailMessage?.StoreFingerprint is null && IsPinned(hits[0].Path),
                    IsFavorite = hits[0].MailMessage?.StoreFingerprint is null && _history.IsFavorite(hits[0].Path),
                };
                _filesByPath[path] = file;

                // Populate BEFORE publishing: the collection view's filter
                // runs the moment the item is added, against the file's
                // extension/size/date/source — which come from its hits.
                // Adding an empty row first made the filter judge a blank
                // and hide it until the next full view refresh.
                file.AddHits(hits);
                _hasMailResults |= file.HasMailMessage;
                file.MetadataLoaded += OnResultMetadataLoaded;
                if (NeedsMetadataForArrangement())
                    file.EnsureMetadataLoading();
                Files.Add(file);
            }
        }
        finally
        {
            _suppressResultViewMaintenance = false;
        }

        TotalHits += drained;
        FilesMatched = Files.Count;
        UpdateHiddenHits(hitsFound);

        // Grow the batch while the UI thread has slack and shrink it when the
        // drain plus the layout it triggers overrun, so input and rendering
        // always get time. Task.Delay alone overshoots by a timer tick, which
        // the thresholds allow for.
        var busy = Stopwatch.GetElapsedTime(drainStarted) + (dispatcherLag > TimeSpan.Zero ? dispatcherLag : TimeSpan.Zero);
        if (busy > TimeSpan.FromMilliseconds(45) && _drainBatchSize > MinDrainBatchSize)
            _drainBatchSize /= 2;
        else if (busy < TimeSpan.FromMilliseconds(20) && _drainBatchSize < MaxDrainBatchSize)
            _drainBatchSize *= 2;

        RebuildFacetOptionsIfDue();
        _status.Text = $"Searching... {DescribeHitTotals()}";
    }

    /// <summary>
    /// While results stream in, only the facet counts are refreshed, at most
    /// once a second and never more often than twenty times their own cost.
    /// The collection view is NOT refreshed here: new rows are already
    /// inserted in sort order and filtered as they are added, and a refresh
    /// resets the whole list (re-sorting, re-grouping, and re-templating
    /// every visible card), which is what froze the UI on large result sets.
    /// The search's finally block does one full refresh when it ends.
    /// </summary>
    private void RebuildFacetOptionsIfDue()
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextResultViewRefreshUtc)
            return;

        var started = Stopwatch.GetTimestamp();
        RebuildFacetOptions();
        var cost = Stopwatch.GetElapsedTime(started);
        _nextResultViewRefreshUtc = nowUtc + TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, cost.Ticks * 20));
    }

    /// <summary>
    /// Holds back the selected grouping while a search streams. WPF's grouped
    /// collection view re-measures and re-templates the visible groups every
    /// time rows are inserted into them, which kept the UI thread saturated
    /// during large searches; a flat list sorted by rank only appends below
    /// the fold. Re-enabling regroups the finished results in one refresh.
    /// Returns true when the view was reshaped (and so already refreshed).
    /// </summary>
    private bool SetResultGroupingDeferred(bool deferred)
    {
        if (_resultGroupingDeferred == deferred ||
            (deferred && (SelectedGroupOption?.Value ?? ResultGroupMode.File) == ResultGroupMode.File))
        {
            return false;
        }

        _resultGroupingDeferred = deferred;
        ApplyResultViewShape();
        OnPropertyChanged(nameof(IsGroupedByFolder));
        return true;
    }

    private void UpdateHiddenHits(long hitsFound) =>
        HiddenHits = Math.Max(0, hitsFound - MaxRetainedHits);

    // Once anything is hidden the search has found exactly the cap plus the
    // hidden count; TotalHits may still trail the cap while hits are queued.
    private string DescribeHitTotals() =>
        HiddenHits > 0
            ? $"showing the first {TotalHits:n0} of {MaxRetainedHits + HiddenHits:n0} hits in {FilesMatched:n0} {ResultItemNounPlural}"
            : $"{TotalHits:n0} hits in {FilesMatched:n0} {ResultItemNounPlural}";

    /// <summary>
    /// Folder results only come from name searches; work it out on the
    /// consumer thread so the UI never touches the disk to build a row.
    /// </summary>
    private static bool ResolveIsDirectory(Hit hit)
    {
        if (hit.Kind == HitKind.Content)
            return false;

        try
        {
            return Directory.Exists(hit.Path) && !File.Exists(hit.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hits kept and shown per search. A broad query can match hundreds of
    /// thousands of lines; past this the search keeps counting but stops
    /// retaining, so memory and UI work stay bounded.
    /// </summary>
    internal const int MaxRetainedHits = 100_000;

    private const int PendingHitCapacity = 8_192;
    private const int MinDrainBatchSize = 32;
    private const int MaxDrainBatchSize = 256;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(33);

    private int _drainBatchSize = 256;
    private readonly Dictionary<(ResultGroupMode Mode, string Key), ResultGroupState> _resultGroupStates = new();

    internal ResultGroupState GetResultGroupState(CollectionViewGroup group)
    {
        var key = (SelectedGroupOption?.Value ?? ResultGroupMode.File, Convert.ToString(group.Name, CultureInfo.CurrentCulture) ?? string.Empty);
        if (!_resultGroupStates.TryGetValue(key, out var state))
        {
            state = new ResultGroupState();
            _resultGroupStates.Add(key, state);
            UpdateResultGroupState(group, state);
        }
        return state;
    }

    private void RefreshResultGroupStates()
    {
        if (FilesView.Groups is null)
            return;
        foreach (var group in FilesView.Groups.OfType<CollectionViewGroup>())
            UpdateResultGroupState(group, GetResultGroupState(group));
    }

    private static void UpdateResultGroupState(CollectionViewGroup group, ResultGroupState state)
    {
        long matches = 0;
        foreach (var item in group.Items)
            if (item is FileResultViewModel file)
                matches += file.HitCount;
        state.Update(group.ItemCount, matches);
    }
    private bool _resultGroupingDeferred;
    private DateTime _nextResultViewRefreshUtc = DateTime.MinValue;

    private readonly record struct PendingHit(Hit Hit, bool? IsDirectory);
    private readonly record struct PendingFileBatch(string Path, IReadOnlyList<Hit> Hits, bool? IsDirectory);
    private readonly Queue<PendingFileBatch> _pendingFileBatches = new();
    private bool _metadataRefreshPending;
    private bool _isDisposed;

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

    private SavedSearchSettings CreateSavedSearch()
    {
        var searchTargets = CurrentSearchTargets;
        var legacySearchTarget = searchTargets.Count == 2 &&
                                 searchTargets.Contains(SearchTarget.FileNames) &&
                                 searchTargets.Contains(SearchTarget.FolderNames)
            ? SearchTarget.FileAndFolderNames
            : searchTargets[0];

        return new SavedSearchSettings
        {
            QueryText = QueryText,
            SearchPath = SearchPath,
            FileNamePattern = FileNamePattern,
            ExcludeFileNamePattern = ExcludeFileNamePattern,
            IncludeSubfolders = IncludeSubfolders,
            SearchMode = SearchMode,
            MatchCase = MatchCase,
            EnableDocumentExtraction = EnableDocumentExtraction,
            EnableImageOcr = EnableImageOcr,
            SkipUnknownFileTypes = SkipUnknownFileTypes,
            UseIndex = UseIndex,
            SearchTarget = legacySearchTarget,
            SearchTargets = searchTargets.ToList(),
            MinSizeKB = MinSizeKB,
            MaxSizeKB = MaxSizeKB,
            ModifiedAfterEnabled = ModifiedAfterEnabled,
            ModifiedAfter = ModifiedAfter,
            ModifiedBeforeEnabled = ModifiedBeforeEnabled,
            ModifiedBefore = ModifiedBefore,
            AdditionalPlainTextExtensions = AdditionalPlainTextExtensions,
        };
    }

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
        EnableImageOcr = search.EnableImageOcr;
        SkipUnknownFileTypes = search.SkipUnknownFileTypes;
        UseIndex = search.UseIndex;
        SetSearchTargets(search.GetSearchTargets());
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

    private async Task ApplyWorkspaceAsync(WorkspaceSettings? workspace)
    {
        if (workspace is null)
            return;

        ApplySavedSearch(workspace.Search);
        ApplyResultSort(workspace.ResultSort);
        ApplyResultGroup(workspace.ResultGroup);
        RefinementQuery = workspace.RefinementQuery?.Trim() ?? string.Empty;
        _history.ReplaceCustomScopes(workspace.CustomScopes);
        _history.ReplaceFavoriteResults(workspace.FavoriteResults);

        _settingsService.Update(settings =>
        {
            settings.QuickSearchPinnedPaths = workspace.PinnedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            settings.QuickSearchSelectedIndexedRoots = workspace.QuickSearchSelectedIndexedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        foreach (var file in Files)
        {
            file.IsPinned = !file.IsStoreMessage && IsPinned(file.FullPath);
            file.IsFavorite = !file.IsStoreMessage && _history.IsFavorite(file.FullPath);
        }

        RefreshFilesView();
        if (!workspace.RunOnLoad)
        {
            _status.Text = $"Workspace loaded: {workspace.DisplayName}.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText) || string.IsNullOrWhiteSpace(SearchPath))
        {
            _status.Text = $"Workspace loaded: {workspace.DisplayName}, but it needs search text and a folder before it can run.";
            return;
        }

        if (!SearchCommand.CanExecute(null))
        {
            _status.Text = $"Workspace loaded: {workspace.DisplayName}, but a search is already running.";
            return;
        }

        _status.Text = $"Workspace loaded: {workspace.DisplayName}; running search.";
        await SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _isDisposed = true;
        foreach (var file in Files)
            file.MetadataLoaded -= OnResultMetadataLoaded;
        _searchCts?.Dispose();
        _previewCts?.Dispose();
        _refinementDebounceCts?.Dispose();
        _history.FavoriteResults.CollectionChanged -= OnFavoriteResultsChanged;
    }

    // ----- helpers -----

    private void ApplyResultViewShape()
    {
        RequestArrangementMetadata();
        using (FilesView.DeferRefresh())
        {
            FilesView.SortDescriptions.Clear();
            FilesView.GroupDescriptions.Clear();

            switch (_resultGroupingDeferred ? ResultGroupMode.File : SelectedGroupOption?.Value ?? ResultGroupMode.File)
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
                case ResultGroupMode.Source:
                    FilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.SourceGroup)));
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
                case ResultSortMode.Size:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SizeBytes), ListSortDirection.Descending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                    break;
                case ResultSortMode.FileType:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.FileTypeGroup), ListSortDirection.Ascending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.FileName), ListSortDirection.Ascending));
                    break;
                default:
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.BestScore), ListSortDirection.Descending));
                    FilesView.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                    break;
            }
            if (FilesView.GroupDescriptions.Count > 0)
                FilesView.SortDescriptions.Insert(0, new SortDescription(nameof(FileResultViewModel.ResultGroupRank), ListSortDirection.Ascending));
        }

        NotifyFilesViewChanged();
    }

    private bool NeedsMetadataForArrangement() =>
        SelectedSortOption?.Value is ResultSortMode.Size or ResultSortMode.Recency ||
        SelectedGroupOption?.Value == ResultGroupMode.ModifiedDate ||
        HasModifiedFacetSelection || HasSizeFacetSelection;

    private void RequestArrangementMetadata()
    {
        if (!NeedsMetadataForArrangement())
            return;
        foreach (var file in Files)
            file.EnsureMetadataLoading();
    }

    private async void OnResultMetadataLoaded(object? sender, EventArgs e)
    {
        if (_isDisposed || IsSearching || sender is not FileResultViewModel file ||
            !_filesByPath.TryGetValue(file.ResultKey, out var current) || !ReferenceEquals(file, current) ||
            _metadataRefreshPending)
            return;

        _metadataRefreshPending = true;
        await Task.Delay(100).ConfigureAwait(true);
        _metadataRefreshPending = false;
        if (_isDisposed || IsSearching)
            return;
        RebuildFacetOptions();
        RefreshFilesView();
    }

    private void ApplyResultSort(string? value)
    {
        if (!Enum.TryParse<ResultSortMode>(value, ignoreCase: true, out var mode))
            mode = ResultSortMode.Relevance;

        SelectedSortOption = ResultSortOptions.FirstOrDefault(option => option.Value == mode) ?? ResultSortOptions[0];
    }

    private void ApplyResultGroup(string? value)
    {
        if (!Enum.TryParse<ResultGroupMode>(value, ignoreCase: true, out var mode))
            mode = ResultGroupMode.File;

        SelectedGroupOption = ResultGroupOptions.FirstOrDefault(option => option.Value == mode) ?? ResultGroupOptions[0];
    }

    private void OnFacetSelectionChanged()
    {
        if (_isRebuildingFacetOptions)
            return;

        RequestArrangementMetadata();
        RefreshFilesView();
        NotifyActiveResultFacetChipsChanged();
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
            RefreshFilteredFolderFacetOptions();
            NotifyActiveResultFacetChipsChanged();
        }
    }

    private List<ResultFacetChip> BuildActiveResultFacetChips()
    {
        var chips = new List<ResultFacetChip>(5);
        AddActiveFacetChip(chips, "type", "Type", SelectedFileTypeFacet);
        AddActiveFacetChip(chips, "folder", "Folder", SelectedFolderFacet);
        AddActiveFacetChip(chips, "date", "Date", SelectedModifiedFacet);
        AddActiveFacetChip(chips, "source", "Source", SelectedSourceFacet);
        AddActiveFacetChip(chips, "size", "Size", SelectedSizeFacet);
        return chips;
    }

    private static void AddActiveFacetChip(
        List<ResultFacetChip> chips,
        string key,
        string field,
        ResultFacetOption? facet)
    {
        if (facet is null || string.Equals(facet.Value, ResultFacetOption.AllValue, StringComparison.Ordinal))
            return;

        chips.Add(new ResultFacetChip(
            key,
            field,
            facet.Label,
            $"{field}: {facet.DisplayText}",
            $"Clear {field.ToLowerInvariant()} facet"));
    }

    private void NotifyActiveResultFacetChipsChanged()
    {
        OnPropertyChanged(nameof(ActiveResultFacetChips));
        OnPropertyChanged(nameof(HasActiveResultFacets));
        OnPropertyChanged(nameof(ActiveResultFacetCount));
        OnPropertyChanged(nameof(HasFileTypeFacetSelection));
        OnPropertyChanged(nameof(HasFolderFacetSelection));
        OnPropertyChanged(nameof(HasModifiedFacetSelection));
        OnPropertyChanged(nameof(HasSourceFacetSelection));
        OnPropertyChanged(nameof(HasSizeFacetSelection));
        OnPropertyChanged(nameof(FileTypeFacetButtonText));
        OnPropertyChanged(nameof(FolderFacetButtonText));
        OnPropertyChanged(nameof(ModifiedFacetButtonText));
        OnPropertyChanged(nameof(SourceFacetButtonText));
        OnPropertyChanged(nameof(SizeFacetButtonText));
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

    private string BuildWorkspaceName()
    {
        var baseName = string.IsNullOrWhiteSpace(QueryText)
            ? "Workspace"
            : QueryText.Trim();
        if (baseName.Length > 42)
            baseName = baseName[..42].Trim();

        return $"{baseName} - {DateTime.Now:yyyy-MM-dd HHmm}";
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
        sb.AppendLine("path,fileName,folder,extension,sizeBytes,modifiedUtc,source,hitCount,lineNumber,kind,location,line,snippet");
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
                sb.Append(CsvField(SourceLocationFormatter.Format(hit.Anchor, hit.Locator))).Append(',');
                sb.Append(CsvField(hit.LineContent)).Append(',');
                sb.AppendLine(CsvField(hit.Snippet?.Text ?? string.Empty));
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
            {
                var location = SourceLocationFormatter.Format(hit.Anchor, hit.Locator);
                var anchor = string.IsNullOrWhiteSpace(location) ? string.Empty : $" [{location}]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- L{hit.LineNumber}{anchor}: {hit.LineContent.Trim()}");
                if (!string.IsNullOrWhiteSpace(hit.Snippet?.Text))
                {
                    sb.AppendLine();
                    sb.AppendLine("  ```text");
                    foreach (var line in hit.Snippet.Text.Trim().Split(s_markdownLineSeparators, StringSplitOptions.None))
                        sb.Append("  ").AppendLine(line);
                    sb.AppendLine("  ```");
                }
            }
        }

        return sb.ToString();
    }

    private static readonly SearchValues<char> s_csvSpecialChars = SearchValues.Create(",\"\n\r");
    private static readonly string[] s_markdownLineSeparators = { "\r\n", "\n" };

    private static readonly JsonSerializerOptions s_exportJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions s_exportJsonLinesOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
            hit.LineContent,
            SourceLocationFormatter.Format(hit.Anchor, hit.Locator),
            hit.Anchor,
            hit.Locator,
            hit.Snippet);

    private WalkerOptions BuildWalkerOptions(SearchTarget searchTarget)
    {
        var include = ParsePatterns(FileNamePattern);
        var exclude = ParsePatterns(ExcludeFileNamePattern);
        var isNameSearch = searchTarget != SearchTarget.Content;

        return new WalkerOptions
        {
            IncludeGlobs = include,
            ExcludeGlobs = exclude,
            IncludeExtensions = !isNameSearch && SkipUnknownFileTypes
                ? BuildKnownTextExtensions(EnableImageOcr)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExcludeExtensions = isNameSearch
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : BuildExcludedExtensions(EnableDocumentExtraction, EnableImageOcr),
            Recursive = IncludeSubfolders,
            MinFileSizeBytes = (long)Math.Max(0, MinSizeKB) * 1024,
            // No explicit max falls back to the shared default cap so GUI and
            // CLI searches agree; enter a larger value to raise it.
            MaxFileSizeBytes = MaxSizeKB > 0
                ? (long)MaxSizeKB * 1024
                : WalkerOptions.DefaultMaxFileSizeBytes,
            AllowLargeMailStores = MaxSizeKB <= 0,
            ModifiedAfterUtc = ModifiedAfterEnabled ? ModifiedAfter.ToUniversalTime() : null,
            ModifiedBeforeUtc = ModifiedBeforeEnabled
                ? ModifiedBefore.AddDays(1).AddSeconds(-1).ToUniversalTime() // inclusive end-of-day
                : null,
            EnableOcr = EnableImageOcr,
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
            EnableOcr = settings.EnableImageOcr,
        };

    private HashSet<string> BuildIndexIncludedExtensions(IndexedLocationSettings settings)
    {
        var explicitIncludes = ExtensionList.Parse(settings.IncludedExtensions);
        if (explicitIncludes.Length > 0)
            return explicitIncludes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return settings.SkipUnknownFileTypes
            ? BuildKnownTextExtensions(settings.EnableImageOcr)
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
            // Also respect the toggle with file-type settings saved before mail support.
            extensions.UnionWith([".msg", ".pst", ".ost"]);
        }

        if (!settings.EnableImageOcr)
        {
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
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

    internal WalkerOptions BuildQuickSearchWalkerOptions() =>
        new()
        {
            Recursive = true,
            IncludeHidden = false,
            IncludeExtensions = SkipUnknownFileTypes
                ? BuildKnownTextExtensions(EnableImageOcr)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExcludeExtensions = BuildExcludedExtensions(EnableDocumentExtraction, EnableImageOcr),
            MaxFileSizeBytes = WalkerOptions.DefaultMaxFileSizeBytes,
            EnableOcr = EnableImageOcr,
        };

    private HashSet<string> BuildKnownTextExtensions(bool includeImageOcr)
    {
        var extensions = _extractorRegistry.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!includeImageOcr)
        {
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
                extensions.Remove(extension);
        }

        foreach (var extension in ParseExtensions(AdditionalPlainTextExtensions))
            extensions.Add(extension);
        return extensions;
    }

    private HashSet<string> BuildExcludedExtensions(bool enableDocumentExtraction, bool enableImageOcr)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!enableDocumentExtraction)
        {
            foreach (var extension in _fileTypeOptions.DocumentExtensions)
                extensions.Add(extension);
            extensions.UnionWith([".msg", ".pst", ".ost"]);
        }

        if (!enableImageOcr)
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
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
        _status.Text = $"Searching... {DescribeHitTotals()}; {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} scanned{skipped}{failed}";
    }

    private async Task LoadPreviewAsync(FileResultViewModel? file)
    {
        _previewCts?.Cancel();
        if (file is null || file.Hits.Count == 0)
        {
            PreviewContent = string.Empty;
            ImageOcrPreview = null;
            return;
        }

        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        try
        {
            ImageOcrPreview = await ImageOcrPreviewViewModel
                .TryCreateAsync(file.FullPath, file.Hits, token)
                .ConfigureAwait(true);
            if (file.MailMessage is { } mail)
            {
                var mailPreview = await _previewService.LoadMailMessagePreviewAsync(file.FullPath, mail,
                    file.Hits.Select(hit => hit.LineNumber).ToArray(), IsWholeFilePreview ? WholeFileContextLines : 3, token)
                    .ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                    PreviewContent = string.IsNullOrWhiteSpace(mailPreview) ? file.BuildStoredHitPreview() : mailPreview;
                return;
            }
            if (IsWholeFilePreview)
            {
                var wholeFile = await LoadWholeFilePreviewAsync(file, token).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                    PreviewContent = string.IsNullOrWhiteSpace(wholeFile)
                        ? file.BuildStoredHitPreview()
                        : wholeFile;
                return;
            }

            var storedPreview = file.HasStructuredSnippets
                ? file.BuildStoredHitPreview()
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(storedPreview))
            {
                PreviewContent = storedPreview;
                return;
            }

            var hitLines = file.Hits
                .Where(h => h.Kind == HitKind.Content && h.LineNumber > 0)
                .Select(h => h.LineNumber)
                .ToList();
            var content = await _previewService
                .LoadHitsPreviewAsync(file.FullPath, hitLines, contextLines: 3, token)
                .ConfigureAwait(true);
            if (!token.IsCancellationRequested)
                PreviewContent = string.IsNullOrWhiteSpace(content)
                    ? file.BuildStoredHitPreview()
                    : content;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreviewContent = $"(failed to load preview: {ex.Message})";
        }
    }

    /// <summary>
    /// The numbered listing for "Show whole file": every line around the hits
    /// (bounded by <see cref="WholeFileContextLines"/>), or — for files whose
    /// hits carry no line numbers — the extracted text numbered from 1.
    /// </summary>
    private async Task<string> LoadWholeFilePreviewAsync(FileResultViewModel file, CancellationToken token)
    {
        var hitLines = file.Hits
            .Where(h => h.Kind == HitKind.Content && h.LineNumber > 0)
            .Select(h => h.LineNumber)
            .ToList();
        if (hitLines.Count > 0)
        {
            return await _previewService
                .LoadHitsPreviewAsync(file.FullPath, hitLines, WholeFileContextLines, token)
                .ConfigureAwait(true);
        }

        var text = await _previewService.LoadFullTextAsync(file.FullPath, token).ConfigureAwait(true);
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder();
        var number = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (++number > WholeFileContextLines)
                break;
            sb.Append("  ")
              .Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(6))
              .Append("  ")
              .Append(line)
              .Append('\n');
        }

        return sb.ToString();
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

    private void RefreshQueryChips()
    {
        QueryChips.Clear();

        if (SearchMode == QueryMode.Unified && !string.IsNullOrWhiteSpace(QueryText))
        {
            try
            {
                if (_queryFactory.Build(QueryText, QueryMode.Unified, MatchCase) is UnifiedQuery unified)
                {
                    foreach (var chip in unified.Chips)
                        QueryChips.Add(new QueryChipViewModel(chip, ReplaceQueryChipValue));
                }
            }
            catch
            {
                // Partial queries should not interrupt typing. The search
                // command still reports parser errors when the user runs it.
            }
        }

        OnPropertyChanged(nameof(HasQueryChips));
    }

    private static string RemoveQueryChipText(string query, QueryChipViewModel chip)
    {
        if (!TryFindQueryChipSpan(query, chip, out var start, out var length))
            return query;

        var end = start + length;
        var left = start;
        while (left > 0 && char.IsWhiteSpace(query[left - 1]))
            left--;

        var right = end;
        while (right < query.Length && char.IsWhiteSpace(query[right]))
            right++;

        if (left < start && right > end)
        {
            start = left;
        }
        else
        {
            start = left;
            end = right;
        }

        return (query[..start] + query[end..]).Trim();
    }

    private void ReplaceQueryChipValue(QueryChipViewModel chip, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            QueryText = RemoveQueryChipText(QueryText, chip);
            return;
        }

        QueryText = ReplaceQueryChipText(QueryText, chip, value);
    }

    private static string ReplaceQueryChipText(string query, QueryChipViewModel chip, string value)
    {
        if (!TryFindQueryChipSpan(query, chip, out var start, out var length))
            return query;

        var existing = query.Substring(start, length);
        var replacementValue = FormatQueryChipReplacement(chip, existing, value);
        var colon = existing.IndexOf(':', StringComparison.Ordinal);
        var replacement = colon > 0
            ? existing[..(colon + 1)] + replacementValue
            : FieldNameForReplacement(chip) is { } fieldName
                ? fieldName + ":" + replacementValue
                : replacementValue;

        return query[..start] + replacement + query[(start + length)..];
    }

    private static bool TryFindQueryChipSpan(string query, QueryChipViewModel chip, out int start, out int length)
    {
        start = chip.Position;
        length = chip.Length;
        if (start >= 0 &&
            length > 0 &&
            start + length <= query.Length &&
            (length != chip.RawText.Length ||
             string.Equals(query.Substring(start, length), chip.RawText, StringComparison.Ordinal)))
        {
            return true;
        }

        start = query.IndexOf(chip.RawText, StringComparison.Ordinal);
        length = chip.RawText.Length;
        return start >= 0 && length > 0 && start + length <= query.Length;
    }

    private static string? FieldNameForReplacement(QueryChipViewModel chip)
    {
        var field = chip.Field.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)
            ? chip.Field[4..]
            : chip.Field;
        return field.ToLowerInvariant() switch
        {
            "created" => "created",
            "extension" => "ext",
            "extractor" => "extractor",
            "folder" => "folder",
            "modified" => "modified",
            "name" => "name",
            "path" => "path",
            "root" => "root",
            "semantic" => "semantic",
            "size" => "size",
            "status" => "status",
            "type" => "type",
            _ => null,
        };
    }

    private static string FormatQueryChipValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        var needsQuotes = trimmed.Any(ch => char.IsWhiteSpace(ch) || ch is '(' or ')' or '"');
        return needsQuotes
            ? "\"" + trimmed.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : trimmed;
    }

    private static string FormatQueryChipReplacement(QueryChipViewModel chip, string existing, string value)
    {
        var replacement = FormatQueryChipValue(value);
        if (replacement.Length == 0)
            return replacement;

        if (chip.Field.Contains("fuzzy", StringComparison.OrdinalIgnoreCase))
        {
            var suffixStart = existing.LastIndexOf('~');
            var suffix = suffixStart >= 0 ? existing[suffixStart..] : "~";
            if (!replacement.EndsWith(suffix, StringComparison.Ordinal))
                replacement += suffix;
        }

        return replacement;
    }

    /// <summary>Persists this view model's slice of the settings.</summary>
    public void SaveOptions()
    {
        _settingsService.Update(settings =>
        {
            settings.SkipUnknownFileTypes = SkipUnknownFileTypes;
            settings.EnableImageOcr = EnableImageOcr;
            settings.UseIndex = UseIndex;
        });
    }

    partial void OnFileNamePatternChanged(string value)
    {
        OnPropertyChanged(nameof(FilePatternSummary));
        OnPropertyChanged(nameof(NameFilterButtonText));
        OnPropertyChanged(nameof(HasNameFilterSelection));
        _history.UpdateActiveScope(value);
    }

    partial void OnExcludeFileNamePatternChanged(string value)
    {
        OnPropertyChanged(nameof(ExcludePatternSummary));
        OnPropertyChanged(nameof(NameFilterButtonText));
        OnPropertyChanged(nameof(HasNameFilterSelection));
    }

    partial void OnIncludeSubfoldersChanged(bool value) =>
        OnPropertyChanged(nameof(SubfoldersSummary));

    partial void OnMatchCaseChanged(bool value)
    {
        OnPropertyChanged(nameof(MatchCaseSummary));
        RefreshQueryChips();
    }

    partial void OnSearchModeChanged(QueryMode value)
    {
        OnPropertyChanged(nameof(IsRegexMode));
        RefreshQueryChips();
    }

    partial void OnEnableDocumentExtractionChanged(bool value) =>
        OnPropertyChanged(nameof(DocumentExtractionSummary));

    partial void OnEnableImageOcrChanged(bool value)
    {
        OnPropertyChanged(nameof(ImageOcrSummary));
        if (_isInitialized)
            SaveOptions();
    }

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

    partial void OnQueryTextChanged(string value)
    {
        OnPropertyChanged(nameof(ResultsContextText));
        OnPropertyChanged(nameof(ResultsHeadlineText));
        RefreshQueryChips();
    }

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

    partial void OnFilesMatchedChanged(int value)
    {
        OnPropertyChanged(nameof(ResultsSummaryText));
        OnPropertyChanged(nameof(ResultsHeadlineText));
        OnPropertyChanged(nameof(ResultsSublineText));
    }

    partial void OnTotalHitsChanged(int value)
    {
        OnPropertyChanged(nameof(ResultsSummaryText));
        OnPropertyChanged(nameof(ResultsHeadlineText));
        if (HiddenHits > 0)
            OnPropertyChanged(nameof(ResultsSublineText));
    }

    partial void OnHiddenHitsChanged(long value) =>
        OnPropertyChanged(nameof(ResultsSublineText));
}
