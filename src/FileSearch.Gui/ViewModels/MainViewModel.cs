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

    // Extensions of files that the "Office/PDF documents" toggle gates.
    // When the toggle is off, files with these extensions are excluded
    // from the walk so we never even open them.
    private static readonly string[] s_documentExtensions =
        { "*.pdf", "*.docx", "*.xlsx" };

    private readonly ISearcher _searcher;
    private readonly IQueryFactory _queryFactory;
    private readonly IFilePreviewService _previewService;
    private readonly IThemeService _themeService;
    private readonly IFileLauncher _fileLauncher;
    private readonly ISettingsStore _settingsStore;
    private readonly IShellIntegrationService _shellIntegrationService;

    private readonly Dictionary<string, FileResultViewModel> _filesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;

    public MainViewModel(
        ISearcher searcher,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IThemeService themeService,
        IFileLauncher fileLauncher,
        ISettingsStore settingsStore,
        IShellIntegrationService shellIntegrationService)
    {
        _searcher = searcher;
        _queryFactory = queryFactory;
        _previewService = previewService;
        _themeService = themeService;
        _fileLauncher = fileLauncher;
        _settingsStore = settingsStore;
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
        if (RecentQueries.Count > 0) QueryText = RecentQueries[0];
        if (RecentPaths.Count > 0) SearchPath = RecentPaths[0];
    }

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

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
    [ObservableProperty] private int _minSizeKB;
    [ObservableProperty] private int _maxSizeKB;

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

    partial void OnSelectedFileChanged(FileResultViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedFile));
        if (value is not null)
            SelectedDetailsTabIndex = HitsTabIndex;
        _ = LoadPreviewAsync(value);
    }

    // ----- commands -----

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
            var request = new SearchRequest(query, new[] { SearchPath }, BuildWalkerOptions());

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
        var exclude = new List<string>();
        if (!EnableDocumentExtraction)
            exclude.AddRange(s_documentExtensions);

        return new WalkerOptions
        {
            IncludeGlobs = include,
            ExcludeGlobs = exclude,
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
        var settings = _settingsStore.Load();
        settings.RecentQueries = RecentQueries.ToList();
        settings.RecentPaths = RecentPaths.ToList();
        _settingsStore.Save(settings);
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
}
