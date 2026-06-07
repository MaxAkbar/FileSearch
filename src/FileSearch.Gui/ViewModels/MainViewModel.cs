using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using Microsoft.Win32;

namespace FileSearch.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject
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
    private readonly ISettingsStore _settingsStore;
    private readonly IFileTypeOptionsStore _fileTypeOptionsStore;
    private readonly FileTypeOptions _fileTypeOptions;
    private readonly IShellIntegrationService _shellIntegrationService;

    private readonly Dictionary<string, FileResultViewModel> _filesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;

    public MainViewModel(
        ISearcher searcher,
        IExtractorRegistry extractorRegistry,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IThemeService themeService,
        IFileLauncher fileLauncher,
        ISettingsStore settingsStore,
        IFileTypeOptionsStore fileTypeOptionsStore,
        IShellIntegrationService shellIntegrationService)
    {
        _searcher = searcher;
        _extractorRegistry = extractorRegistry;
        _queryFactory = queryFactory;
        _previewService = previewService;
        _themeService = themeService;
        _fileLauncher = fileLauncher;
        _settingsStore = settingsStore;
        _fileTypeOptionsStore = fileTypeOptionsStore;
        _fileTypeOptions = _fileTypeOptionsStore.Load();
        _shellIntegrationService = shellIntegrationService;

        // Set up the filtered view used by the "Filter" tab.
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = FilterFiles;
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilesVisible));

        // Load search history, then seed the input fields with the most
        // recent entry so the user lands back where they left off.
        var saved = _settingsStore.Load();
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

        RecentQueries.CollectionChanged += (_, _) => ClearRecentQueriesCommand.NotifyCanExecuteChanged();
        RecentPaths.CollectionChanged += (_, _) => ClearRecentPathsCommand.NotifyCanExecuteChanged();
        CustomScopes.CollectionChanged += (_, _) => ClearCustomScopesCommand.NotifyCanExecuteChanged();

        if (RecentQueries.Count > 0) QueryText = RecentQueries[0];
        if (RecentPaths.Count > 0) SearchPath = RecentPaths[0];
        SkipUnknownFileTypes = saved.SkipUnknownFileTypes;
        if (_fileTypeOptions.AdditionalPlainTextExtensions.Count == 0 && !string.IsNullOrWhiteSpace(saved.AdditionalPlainTextExtensions))
        {
            _fileTypeOptions.AdditionalPlainTextExtensions = ParseExtensions(saved.AdditionalPlainTextExtensions).ToList();
            _fileTypeOptionsStore.Save(_fileTypeOptions);
        }
        AdditionalPlainTextExtensions = string.Join("; ", _fileTypeOptions.AdditionalPlainTextExtensions);
    }

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

    public ObservableCollection<SearchScope> CustomScopes { get; } = new();

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
    [ObservableProperty] private int _totalHits;
    [ObservableProperty] private int _filesMatched;
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _previewContent = string.Empty;
    [ObservableProperty] private bool _isPreviewPaneVisible = true;
    [ObservableProperty] private double _previewPaneWidth = 360;
    [ObservableProperty] private int _selectedDetailsTabIndex;

    // --- in-memory filter over the results ("search the search") ---
    [ObservableProperty] private string _refinementQuery = string.Empty;

    partial void OnRefinementQueryChanged(string value)
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
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder to search",
            InitialDirectory = string.IsNullOrEmpty(SearchPath) ? Environment.CurrentDirectory : SearchPath,
        };
        if (dialog.ShowDialog() == true)
            SearchPath = dialog.FolderName;
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
        try
        {
            IProgress<SearchProgress> progress = new Progress<SearchProgress>(UpdateProgressStatus);
            var request = new SearchRequest(query, new[] { SearchPath }, BuildWalkerOptions(), progress.Report);

            await foreach (var hit in _searcher.SearchAsync(request, token).ConfigureAwait(true))
            {
                if (!_filesByPath.TryGetValue(hit.Path, out var file))
                {
                    file = new FileResultViewModel(hit.Path, _fileLauncher);
                    _filesByPath[hit.Path] = file;
                    Files.Add(file);
                    FilesMatched = Files.Count;
                }
                file.AddHit(hit);
                TotalHits++;

                if ((TotalHits & 0x3F) == 0)
                    StatusText = $"Searching... {TotalHits} hits in {FilesMatched} files";
            }

            stopwatch.Stop();
            ElapsedText = $"{stopwatch.Elapsed.TotalSeconds:0.00}s";
            StatusText = $"Done — {TotalHits} hits in {FilesMatched} files";
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _searchCts?.Cancel();

    private bool CanCancel() => IsSearching;

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
        RemoveFromList(RecentPaths, path);
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
        RemoveFromList(RecentQueries, query);
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
            MaxFileSizeBytes = MaxSizeKB > 0
                ? (long)MaxSizeKB * 1024
                : 0, // 0 = no max
            ModifiedAfterUtc = ModifiedAfterEnabled ? ModifiedAfter.ToUniversalTime() : null,
            ModifiedBeforeUtc = ModifiedBeforeEnabled
                ? ModifiedBefore.AddDays(1).AddSeconds(-1).ToUniversalTime() // inclusive end-of-day
                : null,
        };
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

    internal static string[] ParseExtensions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw.Split(new[] { ';', ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeExtension)
            .Where(extension => extension.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (extension.StartsWith("*.", StringComparison.Ordinal))
            extension = extension[1..];
        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;
        return extension.ToLowerInvariant();
    }

    private void UpdateProgressStatus(SearchProgress progress)
    {
        if (!IsSearching) return;

        var skipped = progress.FilesSkipped > 0 ? $", {progress.FilesSkipped:n0} skipped" : string.Empty;
        var failed = progress.FilesFailed > 0 ? $", {progress.FilesFailed:n0} failed" : string.Empty;
        StatusText = $"Searching... {TotalHits:n0} hits in {FilesMatched:n0} files; {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} scanned{skipped}{failed}";
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

    private static string[] ParsePatterns(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ----- search history -----

    private void RecordHistory(string query, string path)
    {
        PromoteToFront(RecentQueries, query);
        PromoteToFront(RecentPaths, path);

        // Persist immediately so history survives a crash.
        SaveSettings();
    }

    private void SaveSettings()
    {
        var settings = _settingsStore.Load();
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
        _settingsStore.Save(settings);
    }

    private static void RemoveFromList(ObservableCollection<string> list, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
                list.RemoveAt(i);
        }
    }

    private static void PromoteToFront(ObservableCollection<string> list, string value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var matchIndex = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (i != matchIndex && string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                if (i < matchIndex)
                    matchIndex--;
            }
        }

        if (matchIndex < 0)
        {
            list.Insert(0, trimmed);
        }
        else
        {
            if (!string.Equals(list[matchIndex], trimmed, StringComparison.Ordinal))
                list[matchIndex] = trimmed;
            if (matchIndex > 0)
                list.Move(matchIndex, 0);
        }

        while (list.Count > MaxHistoryEntries)
            list.RemoveAt(list.Count - 1);
    }

    partial void OnFileNamePatternChanged(string value) =>
        OnPropertyChanged(nameof(FilePatternSummary));

    partial void OnIncludeSubfoldersChanged(bool value) =>
        OnPropertyChanged(nameof(SubfoldersSummary));

    partial void OnMatchCaseChanged(bool value) =>
        OnPropertyChanged(nameof(MatchCaseSummary));

    partial void OnEnableDocumentExtractionChanged(bool value) =>
        OnPropertyChanged(nameof(DocumentExtractionSummary));

    partial void OnSkipUnknownFileTypesChanged(bool value) =>
        OnPropertyChanged(nameof(UnknownFileTypesSummary));

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
}
