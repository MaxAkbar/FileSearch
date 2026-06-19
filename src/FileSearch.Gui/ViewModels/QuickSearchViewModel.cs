using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

public sealed record QuickSearchScopeOption(QuickSearchScopeKind Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed partial class QuickSearchViewModel : ObservableObject, IDisposable
{
    private const int SearchDebounceMilliseconds = 35;
    private const int DrainDelayMilliseconds = 35;
    private const int MaxResultFiles = 80;
    private const int MaxHits = 500;
    private const int MaxPinnedResults = 20;
    private const int ScopedMetadataScanBudgetMilliseconds = 500;
    private const int MachineMetadataScanBudgetMilliseconds = 1500;

    private readonly ISearcher _searcher;
    private readonly IQueryFactory _queryFactory;
    private readonly IFilePreviewService _previewService;
    private readonly IFileLauncher _fileLauncher;
    private readonly ISettingsService _settingsService;
    private readonly SearchViewModel _mainSearch;
    private readonly IFolderPicker _folderPicker;
    private readonly IIndexUsageStore? _indexUsageStore;
    private readonly Dictionary<string, FileResultViewModel> _filesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenHits = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private bool _isPreparing;
    private bool _hitLimitReached;

    public QuickSearchViewModel(
        ISearcher searcher,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IFileLauncher fileLauncher,
        ISettingsService settingsService,
        SearchViewModel mainSearch,
        IFolderPicker folderPicker,
        IIndexUsageStore? indexUsageStore = null)
    {
        _searcher = searcher;
        _queryFactory = queryFactory;
        _previewService = previewService;
        _fileLauncher = fileLauncher;
        _settingsService = settingsService;
        _mainSearch = mainSearch;
        _folderPicker = folderPicker;
        _indexUsageStore = indexUsageStore;
        _includeContentMatches = _settingsService.Current.QuickSearchIncludeContent;
        _quickFolderPath = ResolveInitialQuickFolderPath();

        Results.CollectionChanged += OnResultsChanged;
        RefreshScopeOptions();
        SelectedScope = ScopeOptions.FirstOrDefault(option => option.Value == CurrentConfiguredScope())
            ?? ScopeOptions.FirstOrDefault();
        LoadPinnedResults();
    }

    public event EventHandler? RequestHide;

    public event EventHandler? ExternalDialogOpened;

    public event EventHandler? ExternalDialogClosed;

    public ObservableCollection<FileResultViewModel> Results { get; } = new();

    public ObservableCollection<QuickSearchScopeOption> ScopeOptions { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private QuickSearchScopeOption? _selectedScope;
    [ObservableProperty] private FileResultViewModel? _selectedResult;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private bool _includeContentMatches;
    [ObservableProperty] private string _quickFolderPath = string.Empty;
    [ObservableProperty] private string _previewContent = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _stageText = string.Empty;

    public bool HasSelectedResult => SelectedResult is not null;

    public bool HasResults => Results.Count > 0;

    public string ResultCountText => Results.Count == 1 ? "1 result" : $"{Results.Count:n0} results";

    public void PrepareForShow()
    {
        _isPreparing = true;
        try
        {
            RefreshScopeOptions();
            SelectedScope = ScopeOptions.FirstOrDefault(option => option.Value == CurrentConfiguredScope())
                ?? ScopeOptions.FirstOrDefault();
            IncludeContentMatches = _settingsService.Current.QuickSearchIncludeContent;
            QuickFolderPath = ResolveInitialQuickFolderPath();
            SearchText = string.Empty;
            IsPreviewVisible = false;
            PreviewContent = string.Empty;
            LoadPinnedResults();
        }
        finally
        {
            _isPreparing = false;
        }
    }

    public void Dismiss()
    {
        _searchCts?.Cancel();
        _previewCts?.Cancel();
        IsSearching = false;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_isPreparing)
            return;

        _searchCts?.Cancel();
        _previewCts?.Cancel();
        IsPreviewVisible = false;
        PreviewContent = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearching = false;
            LoadPinnedResults();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts?.Dispose();
        _searchCts = cts;
        _ = SearchAfterDebounceAsync(value.Trim(), cts.Token);
    }

    partial void OnSelectedScopeChanged(QuickSearchScopeOption? value)
    {
        if (value is null)
            return;

        if (_settingsService.Current.QuickSearchRememberLastScope)
        {
            _settingsService.Update(settings => settings.QuickSearchLastScope = value.Value);
        }

        if (!_isPreparing && !string.IsNullOrWhiteSpace(SearchText))
            RestartSearch(SearchText.Trim());

        OnPropertyChanged(nameof(CanSearchContent));
        OnPropertyChanged(nameof(ContentSearchSummary));
        OnPropertyChanged(nameof(IsFolderScope));
    }

    partial void OnIncludeContentMatchesChanged(bool value)
    {
        _settingsService.Update(settings => settings.QuickSearchIncludeContent = value);
        OnPropertyChanged(nameof(ContentSearchSummary));

        if (!_isPreparing && !string.IsNullOrWhiteSpace(SearchText))
            RestartSearch(SearchText.Trim());
    }

    partial void OnQuickFolderPathChanged(string value)
    {
        _settingsService.Update(settings => settings.QuickSearchFolderPath = value.Trim());

        if (!_isPreparing && IsFolderScope && !string.IsNullOrWhiteSpace(SearchText))
            RestartSearch(SearchText.Trim());
    }

    partial void OnSelectedResultChanged(FileResultViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedResult));
        OpenResultCommand.NotifyCanExecuteChanged();
        RevealResultCommand.NotifyCanExecuteChanged();
        CopyResultPathCommand.NotifyCanExecuteChanged();
        PinResultCommand.NotifyCanExecuteChanged();
        PreviewResultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private async Task OpenResultAsync(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        await result.OpenCommand.ExecuteAsync(null).ConfigureAwait(true);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void RevealResult(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        result.RevealInExplorerCommand.Execute(null);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void CopyResultPath(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        result.CopyPathCommand.Execute(null);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void PinResult(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        if (IsPinned(result.FullPath))
        {
            _settingsService.Update(settings =>
            {
                settings.QuickSearchPinnedPaths.RemoveAll(path =>
                    string.Equals(path, result.FullPath, StringComparison.OrdinalIgnoreCase));
            });

            result.IsPinned = false;
            if (string.IsNullOrWhiteSpace(SearchText))
                LoadPinnedResults();

            StatusText = "Unpinned result.";
            return;
        }

        _settingsService.Update(settings =>
        {
            settings.QuickSearchPinnedPaths.RemoveAll(path =>
                string.Equals(path, result.FullPath, StringComparison.OrdinalIgnoreCase));
            settings.QuickSearchPinnedPaths.Insert(0, result.FullPath);
            if (settings.QuickSearchPinnedPaths.Count > MaxPinnedResults)
                settings.QuickSearchPinnedPaths.RemoveRange(MaxPinnedResults, settings.QuickSearchPinnedPaths.Count - MaxPinnedResults);
        });
        result.IsPinned = true;
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private async Task PreviewResultAsync(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        IsPreviewVisible = true;
        PreviewContent = "Loading preview...";
        try
        {
            var lines = result.Hits
                .Where(hit => hit.Kind == HitKind.Content && hit.LineNumber > 0)
                .Select(hit => hit.LineNumber)
                .ToList();
            var preview = await _previewService
                .LoadHitsPreviewAsync(result.FullPath, lines, contextLines: 2, token)
                .ConfigureAwait(true);
            if (!token.IsCancellationRequested)
                PreviewContent = string.IsNullOrWhiteSpace(preview) ? "(no extractable preview)" : preview;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewContent = $"(failed to load preview: {ex.Message})";
        }
    }

    private bool CanUseResult(FileResultViewModel? result) => result is not null || SelectedResult is not null;

    public bool CanSearchContent => SelectedScope?.Value != QuickSearchScopeKind.EntireMachineMetadata;

    public bool IsFolderScope => SelectedScope?.Value == QuickSearchScopeKind.CurrentFolder;

    public string ContentSearchSummary =>
        CanSearchContent
            ? "Include indexed content matches"
            : "Content search requires an indexed scope";

    [RelayCommand]
    private void ChooseQuickFolder()
    {
        var initialDirectory = Directory.Exists(QuickFolderPath)
            ? QuickFolderPath
            : Directory.Exists(_mainSearch.SearchPath)
                ? _mainSearch.SearchPath
                : Environment.CurrentDirectory;
        ExternalDialogOpened?.Invoke(this, EventArgs.Empty);
        try
        {
            var folder = _folderPicker.PickFolder("Select Quick Search folder", initialDirectory);
            if (!string.IsNullOrWhiteSpace(folder))
                QuickFolderPath = folder;
        }
        finally
        {
            ExternalDialogClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _previewCts?.Cancel();
        _searchCts?.Dispose();
        _previewCts?.Dispose();
    }

    private async Task SearchAfterDebounceAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, token).ConfigureAwait(true);
            await SearchAsync(text, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SearchAsync(string text, CancellationToken token)
    {
        ResetResults();
        IsSearching = true;
        StageText = "Filename and path matches";
        StatusText = "Searching...";

        var scope = SelectedScope?.Value ?? CurrentConfiguredScope();
        if (scope == QuickSearchScopeKind.EntireMachineMetadata)
        {
            await SearchMachineMetadataAsync(text, token).ConfigureAwait(true);
            return;
        }

        Query query;
        try
        {
            query = _queryFactory.Build(text, QueryMode.PlainText, caseSensitive: false);
        }
        catch (Exception ex)
        {
            IsSearching = false;
            StatusText = $"Invalid query: {ex.Message}";
            return;
        }

        var roots = ResolveRoots(scope);
        if (roots.Count == 0)
        {
            IsSearching = false;
            StatusText = scope == QuickSearchScopeKind.CurrentFolder
                ? "Choose an existing folder to search."
                : "No searchable roots are configured.";
            return;
        }

        var pendingHits = new ConcurrentQueue<Hit>();
        var includeContent = IncludeContentMatches && CanSearchContent;
        var metadataProducer = Task.Run(
            () => EnqueueFilesystemMetadataMatches(
                roots,
                text,
                pendingHits,
                ScopedMetadataScanBudgetMilliseconds,
                token),
            token);
        var contentProducer = includeContent
            ? Task.Run(async () =>
            {
                var request = new SearchRequest(
                    query,
                    roots,
                    BuildWalkerOptions(),
                    Progress: null,
                    UseIndex: true,
                    Status: null,
                    RawQuery: text,
                    Mode: QueryMode.PlainText);

                await foreach (var hit in _searcher.SearchAsync(request, token).ConfigureAwait(false))
                    pendingHits.Enqueue(hit);
            }, token)
            : Task.CompletedTask;

        try
        {
            while (true)
            {
                DrainPendingHits(pendingHits);
                if ((metadataProducer.IsCompleted && contentProducer.IsCompleted && pendingHits.IsEmpty) || _hitLimitReached)
                    break;

                await Task.Delay(DrainDelayMilliseconds, token).ConfigureAwait(true);
            }

            await Task.WhenAll(metadataProducer, contentProducer).ConfigureAwait(true);
            DrainPendingHits(pendingHits);
            FinishSearchStatus();
        }
        catch (OperationCanceledException)
        {
            if (_hitLimitReached || !token.IsCancellationRequested)
                FinishSearchStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SearchMachineMetadataAsync(string text, CancellationToken token)
    {
        var pendingHits = new ConcurrentQueue<Hit>();
        var roots = GetMachineMetadataRoots();
        if (roots.Count == 0)
        {
            IsSearching = false;
            StatusText = "No ready fixed drives were found.";
            return;
        }

        var producer = Task.Run(
            () => EnqueueFilesystemMetadataMatches(
                roots,
                text,
                pendingHits,
                MachineMetadataScanBudgetMilliseconds,
                token),
            token);

        try
        {
            while (true)
            {
                DrainPendingHits(pendingHits);
                if ((producer.IsCompleted && pendingHits.IsEmpty) || _hitLimitReached)
                    break;

                await Task.Delay(DrainDelayMilliseconds, token).ConfigureAwait(true);
            }

            await producer.ConfigureAwait(true);
            DrainPendingHits(pendingHits);
            FinishSearchStatus();
        }
        catch (OperationCanceledException)
        {
            if (_hitLimitReached)
                FinishSearchStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"Metadata search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void RestartSearch(string text)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts?.Dispose();
        _searchCts = cts;
        _ = SearchAfterDebounceAsync(text, cts.Token);
    }

    private void DrainPendingHits(ConcurrentQueue<Hit> pendingHits)
    {
        var drained = 0;
        while (drained < 250 && pendingHits.TryDequeue(out var hit))
        {
            if (_seenHits.Count >= MaxHits || _filesByPath.Count >= MaxResultFiles)
            {
                _hitLimitReached = true;
                _searchCts?.Cancel();
                break;
            }

            var key = $"{hit.Path}\0{hit.Kind}\0{hit.LineNumber}\0{hit.LineContent}";
            if (!_seenHits.Add(key))
            {
                drained++;
                continue;
            }

            if (!_filesByPath.TryGetValue(hit.Path, out var file))
            {
                var recordOpened = _indexUsageStore is null
                    ? null
                    : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
                file = new FileResultViewModel(hit.Path, _fileLauncher, recordOpened);
                file.IsPinned = IsPinned(hit.Path);
                _filesByPath[hit.Path] = file;
                Results.Add(file);
                if (SelectedResult is null)
                    SelectedResult = file;
            }

            file.AddHit(hit);
            if (hit.Kind == HitKind.Content)
                StageText = "Indexed lexical content matches";

            drained++;
        }

        if (drained > 0)
        {
            OnPropertyChanged(nameof(ResultCountText));
            StatusText = $"{Results.Count:n0} files, {_seenHits.Count:n0} hits";
        }
    }

    private void FinishSearchStatus()
    {
        StatusText = _hitLimitReached
            ? $"Showing first {Results.Count:n0} files"
            : Results.Count == 0
                ? "No matches"
                : $"{Results.Count:n0} files, {_seenHits.Count:n0} hits";
        StageText = Results.Count == 0 ? string.Empty : StageText;
    }

    private void ResetResults()
    {
        _hitLimitReached = false;
        _filesByPath.Clear();
        _seenHits.Clear();
        Results.Clear();
        SelectedResult = null;
        PreviewContent = string.Empty;
        OnPropertyChanged(nameof(ResultCountText));
    }

    private void LoadPinnedResults()
    {
        ResetResults();
        StageText = "Pinned results";

        foreach (var path in _settingsService.Current.QuickSearchPinnedPaths
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(MaxPinnedResults))
        {
            var recordOpened = _indexUsageStore is null
                ? null
                : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
            var file = new FileResultViewModel(path, _fileLauncher, recordOpened);
            file.IsPinned = true;
            file.AddHit(new Hit(path, 0, "Pinned result", Array.Empty<MatchSpan>(), HitKind.Metadata));
            _filesByPath[path] = file;
            Results.Add(file);
        }

        SelectedResult = Results.FirstOrDefault();
        StatusText = Results.Count == 0 ? "Type to search" : $"{Results.Count:n0} pinned";
        OnPropertyChanged(nameof(ResultCountText));
    }

    private void RefreshScopeOptions()
    {
        var previous = SelectedScope?.Value;
        ScopeOptions.Clear();
        ScopeOptions.Add(new QuickSearchScopeOption(QuickSearchScopeKind.CurrentFolder, "Selected folder"));
        ScopeOptions.Add(new QuickSearchScopeOption(QuickSearchScopeKind.SelectedIndexedLocations, "Selected indexed locations"));
        ScopeOptions.Add(new QuickSearchScopeOption(QuickSearchScopeKind.AllIndexedLocations, "All indexed locations"));
        ScopeOptions.Add(new QuickSearchScopeOption(QuickSearchScopeKind.EntireMachineMetadata, "Entire machine metadata"));

        if (previous is not null)
            SelectedScope = ScopeOptions.FirstOrDefault(option => option.Value == previous.Value) ?? SelectedScope;
    }

    private QuickSearchScopeKind CurrentConfiguredScope() =>
        _settingsService.Current.QuickSearchRememberLastScope
            ? NormalizeScope(_settingsService.Current.QuickSearchLastScope)
            : NormalizeScope(_settingsService.Current.QuickSearchDefaultScope);

    private static QuickSearchScopeKind NormalizeScope(QuickSearchScopeKind scope) =>
        Enum.IsDefined(scope) ? scope : QuickSearchScopeKind.AllIndexedLocations;

    private IReadOnlyList<string> ResolveRoots(QuickSearchScopeKind scope)
    {
        if (scope == QuickSearchScopeKind.CurrentFolder)
        {
            var folder = QuickFolderPath.Trim();
            return Directory.Exists(folder) ? new[] { folder } : Array.Empty<string>();
        }

        var indexedRoots = _settingsService.Current.IndexedLocations
            .Select(location => location.Root)
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scope == QuickSearchScopeKind.AllIndexedLocations)
            return indexedRoots;

        var selected = _settingsService.Current.QuickSearchSelectedIndexedRoots
            .Where(root => indexedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return selected.Count == 0 ? indexedRoots : selected;
    }

    private static IReadOnlyList<string> GetMachineMetadataRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static WalkerOptions BuildWalkerOptions() =>
        new()
        {
            Recursive = true,
            IncludeHidden = false,
            MaxFileSizeBytes = WalkerOptions.DefaultMaxFileSizeBytes,
        };

    private static void EnqueueFilesystemMetadataMatches(
        IReadOnlyList<string> roots,
        string text,
        ConcurrentQueue<Hit> pendingHits,
        int scanBudgetMilliseconds,
        CancellationToken token)
    {
        var terms = SplitMetadataTerms(text);
        if (terms.Length == 0)
            return;

        var started = Stopwatch.GetTimestamp();
        var emitted = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (IsMetadataScanBudgetSpent(started, scanBudgetMilliseconds))
                return;

            if (!Directory.Exists(root))
                continue;

            foreach (var path in EnumerateFilesSafe(root, token))
            {
                token.ThrowIfCancellationRequested();
                if (IsMetadataScanBudgetSpent(started, scanBudgetMilliseconds))
                    return;

                if (!MetadataMatches(path, terms))
                    continue;

                pendingHits.Enqueue(new Hit(
                    path,
                    0,
                    "Filename/path match",
                    Array.Empty<MatchSpan>(),
                    HitKind.Metadata,
                    Score: 1));
                emitted++;
                if (emitted >= MaxResultFiles)
                    return;
            }
        }
    }

    private static bool IsMetadataScanBudgetSpent(long started, int scanBudgetMilliseconds) =>
        scanBudgetMilliseconds > 0 &&
        Stopwatch.GetElapsedTime(started).TotalMilliseconds >= scanBudgetMilliseconds;

    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var child in directories)
            {
                if (ShouldSkipDirectory(child))
                    continue;

                stack.Push(child);
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                return true;

            return WalkerOptions.DefaultExcludeDirectories.Contains(info.Name);
        }
        catch
        {
            return true;
        }
    }

    private static string[] SplitMetadataTerms(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MetadataMatches(string path, IReadOnlyList<string> terms)
    {
        var fileName = Path.GetFileName(path);
        foreach (var term in terms)
        {
            if (!fileName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultCountText));
    }

    private bool IsPinned(string path) =>
        _settingsService.Current.QuickSearchPinnedPaths.Any(pinned =>
            string.Equals(pinned, path, StringComparison.OrdinalIgnoreCase));

    private string ResolveInitialQuickFolderPath()
    {
        var saved = _settingsService.Current.QuickSearchFolderPath;
        if (!string.IsNullOrWhiteSpace(saved))
            return saved;

        if (!string.IsNullOrWhiteSpace(_mainSearch.SearchPath) && Directory.Exists(_mainSearch.SearchPath))
            return _mainSearch.SearchPath;

        return Environment.CurrentDirectory;
    }
}
