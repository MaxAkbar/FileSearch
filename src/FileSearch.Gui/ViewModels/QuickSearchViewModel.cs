using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
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
    private const int DrainDelayMilliseconds = 16;
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
    private readonly IVolumeNameIndex? _volumeNameIndex;
    private readonly Dictionary<string, FileResultViewModel> _filesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenHits = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private bool _isPreparing;
    private bool _hitLimitReached;
    private long _searchGeneration;

    public QuickSearchViewModel(
        ISearcher searcher,
        IQueryFactory queryFactory,
        IFilePreviewService previewService,
        IFileLauncher fileLauncher,
        ISettingsService settingsService,
        SearchViewModel mainSearch,
        IFolderPicker folderPicker,
        IIndexUsageStore? indexUsageStore = null,
        IVolumeNameIndex? volumeNameIndex = null)
    {
        _volumeNameIndex = volumeNameIndex;
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
    [ObservableProperty] private ImageOcrPreviewViewModel? _imageOcrPreview;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _stageText = string.Empty;

    public bool HasSelectedResult => SelectedResult is not null;

    public bool HasResults => Results.Count > 0;

    public bool HasImageOcrPreview => ImageOcrPreview is not null;

    public string ResultCountText => Results.Count == 1 ? "1 result" : $"{Results.Count:n0} results";

    public void PrepareForShow()
    {
        CancelSearch();
        IsSearching = false;
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
            ImageOcrPreview = null;
            LoadPinnedResults();
        }
        finally
        {
            _isPreparing = false;
        }
    }

    public void Dismiss()
    {
        CancelSearch();
        _previewCts?.Cancel();
        IsSearching = false;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_isPreparing)
            return;

        CancelSearch();
        _previewCts?.Cancel();
        IsPreviewVisible = false;
        PreviewContent = string.Empty;
        ImageOcrPreview = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearching = false;
            LoadPinnedResults();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts?.Dispose();
        _searchCts = cts;
        _ = SearchAfterDebounceAsync(value.Trim(), _searchGeneration, cts.Token);
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
        OpenOcrPreviewCommand.NotifyCanExecuteChanged();
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
            ImageOcrPreview = await ImageOcrPreviewViewModel
                .TryCreateAsync(result.FullPath, result.Hits, token)
                .ConfigureAwait(true);
            var storedPreview = result.HasStructuredSnippets
                ? result.BuildStoredHitPreview()
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(storedPreview))
            {
                PreviewContent = storedPreview;
                return;
            }

            var lines = result.Hits
                .Where(hit => hit.Kind == HitKind.Content && hit.LineNumber > 0)
                .Select(hit => hit.LineNumber)
                .ToList();
            var preview = await _previewService
                .LoadHitsPreviewAsync(result.FullPath, lines, contextLines: 2, token)
                .ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                var previewText = string.IsNullOrWhiteSpace(preview)
                    ? result.BuildStoredHitPreview()
                    : preview;
                PreviewContent = string.IsNullOrWhiteSpace(previewText)
                    ? "(no extractable preview)"
                    : previewText;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewContent = $"(failed to load preview: {ex.Message})";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOcrPreview))]
    private async Task OpenOcrPreviewAsync(FileResultViewModel? result)
    {
        result ??= SelectedResult;
        if (result is null)
            return;

        await result.OpenImageOcrPreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUseResult(FileResultViewModel? result) => result is not null || SelectedResult is not null;

    private bool CanOpenOcrPreview(FileResultViewModel? result) =>
        (result ?? SelectedResult)?.HasImageOcrPreview == true;

    partial void OnImageOcrPreviewChanged(ImageOcrPreviewViewModel? value) =>
        OnPropertyChanged(nameof(HasImageOcrPreview));

    public bool CanSearchContent => SelectedScope?.Value != QuickSearchScopeKind.EntireMachineMetadata;

    public bool IsFolderScope => SelectedScope?.Value == QuickSearchScopeKind.CurrentFolder;

    public string ContentSearchSummary
    {
        get
        {
            if (!CanSearchContent)
                return "Content search requires an indexed scope";

            return _mainSearch.EnableImageOcr
                ? "Include indexed content matches, including image OCR when indexed"
                : "Include indexed content matches";
        }
    }

    public AppShortcutGesture GetShortcut(QuickSearchShortcutAction action)
    {
        var shortcuts = _settingsService.Current.QuickSearchShortcuts ?? QuickSearchShortcutSettings.CreateDefaults();
        return action switch
        {
            QuickSearchShortcutAction.Close => NormalizeShortcutGesture(shortcuts.Close),
            QuickSearchShortcutAction.FocusResults => NormalizeShortcutGesture(shortcuts.FocusResults),
            QuickSearchShortcutAction.OpenSelectedResult => NormalizeShortcutGesture(shortcuts.OpenSelectedResult),
            QuickSearchShortcutAction.RevealSelectedResult => NormalizeShortcutGesture(shortcuts.RevealSelectedResult),
            QuickSearchShortcutAction.CopySelectedResultPath => NormalizeShortcutGesture(shortcuts.CopySelectedResultPath),
            QuickSearchShortcutAction.PinSelectedResult => NormalizeShortcutGesture(shortcuts.PinSelectedResult),
            QuickSearchShortcutAction.PreviewSelectedResult => NormalizeShortcutGesture(shortcuts.PreviewSelectedResult),
            _ => AppShortcutGesture.Disabled,
        };
    }

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
        CancelSearch();
        _previewCts?.Cancel();
        _searchCts?.Dispose();
        _previewCts?.Dispose();
    }

    private async Task SearchAfterDebounceAsync(string text, long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, token).ConfigureAwait(true);
            if (IsCurrentSearch(generation, token))
                await SearchAsync(text, generation, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsCurrentSearch(long generation, CancellationToken token) =>
        generation == _searchGeneration && !token.IsCancellationRequested;

    private void CancelSearch()
    {
        _searchGeneration++;
        _searchCts?.Cancel();
    }

    private void RestartSearch(string text)
    {
        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts?.Dispose();
        _searchCts = cts;
        _ = SearchAfterDebounceAsync(text, _searchGeneration, cts.Token);
    }

    private async Task SearchAsync(string text, long generation, CancellationToken token)
    {
        ResetResults();
        IsSearching = true;
        ImageOcrPreview = null;
        StageText = "Filename and path matches";
        StatusText = "Searching...";

        var scope = SelectedScope?.Value ?? CurrentConfiguredScope();
        var machine = scope == QuickSearchScopeKind.EntireMachineMetadata;
        var roots = machine ? GetMachineMetadataRoots() : ResolveRoots(scope);
        if (roots.Count == 0)
        {
            IsSearching = false;
            StatusText = machine ? "No ready fixed drives were found."
                : scope == QuickSearchScopeKind.CurrentFolder ? "Choose an existing folder to search."
                : "No searchable roots are configured.";
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

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producerToken = producerCts.Token;
        var pendingHits = Channel.CreateBounded<QueuedHit>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var metadataProducer = Task.Run(() => EnqueueMetadataMatchesAsync(
            roots, text, pendingHits.Writer,
            machine ? MachineMetadataScanBudgetMilliseconds : ScopedMetadataScanBudgetMilliseconds,
            producerToken), producerToken);
        var includeContent = !machine && IncludeContentMatches && CanSearchContent;
        var request = includeContent ? new SearchRequest(
            query, roots, _mainSearch.BuildQuickSearchWalkerOptions(),
            Progress: null, UseIndex: true, Status: null, RawQuery: text, Mode: QueryMode.PlainText) : null;
        var contentProducer = request is not null
            ? Task.Run(async () =>
            {
                await foreach (var hit in _searcher.SearchAsync(request, producerToken).ConfigureAwait(false))
                    await pendingHits.Writer.WriteAsync(new QueuedHit(hit, false), producerToken).ConfigureAwait(false);
            }, producerToken)
            : Task.CompletedTask;
        var producers = Task.WhenAll(metadataProducer, contentProducer);

        try
        {
            while (IsCurrentSearch(generation, token))
            {
                DrainPendingHits(pendingHits.Reader, producerCts);
                if (_hitLimitReached || (producers.IsCompleted && !pendingHits.Reader.TryPeek(out _)))
                    break;
                await Task.Delay(DrainDelayMilliseconds, token).ConfigureAwait(true);
            }

            await producers.ConfigureAwait(true);
            if (IsCurrentSearch(generation, token))
                FinishSearchStatus();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSearch(generation, token) && _hitLimitReached)
                FinishSearchStatus();
        }
        catch (Exception ex)
        {
            if (IsCurrentSearch(generation, token))
                StatusText = $"{(machine ? "Metadata search" : "Search")} failed: {ex.Message}";
        }
        finally
        {
            // Cancel and observe both writers, including a writer waiting on
            // a full channel. An obsolete search must never publish UI state.
            producerCts.Cancel();
            try { await producers.ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            pendingHits.Writer.TryComplete();
            if (IsCurrentSearch(generation, token))
                IsSearching = false;
        }
    }

    private void DrainPendingHits(ChannelReader<QueuedHit> pendingHits, CancellationTokenSource producerCts)
    {
        var started = Stopwatch.GetTimestamp();
        var drained = 0;
        while (drained < 128 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(8) &&
               pendingHits.TryRead(out var pending))
        {
            if (_seenHits.Count >= MaxHits || _filesByPath.Count >= MaxResultFiles)
            {
                _hitLimitReached = true;
                producerCts.Cancel();
                break;
            }

            var hit = pending.Hit;
            var key = $"{hit.Path}\0{hit.Kind}\0{hit.LineNumber}\0{hit.LineContent}";
            drained++;
            if (!_seenHits.Add(key))
                continue;

            if (!_filesByPath.TryGetValue(hit.Path, out var file))
            {
                var recordOpened = _indexUsageStore is null ? null
                    : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
                file = new FileResultViewModel(hit.Path, _fileLauncher, recordOpened, isDirectory: pending.IsDirectory)
                {
                    IsPinned = IsPinned(hit.Path),
                };
                file.AddHit(hit);
                _filesByPath[hit.Path] = file;
                Results.Add(file);
                SelectedResult ??= file;
            }
            else
            {
                file.AddHit(hit);
            }

            if (hit.Kind == HitKind.Content)
                StageText = "Indexed lexical content matches";
            else if (hit.Route == HitRoute.Indexed && StageText.Length > 0 && !StageText.StartsWith("Indexed", StringComparison.Ordinal))
                StageText = "Drive name index matches";

            if (_seenHits.Count >= MaxHits || _filesByPath.Count >= MaxResultFiles)
            {
                _hitLimitReached = true;
                producerCts.Cancel();
                break;
            }
        }

        if (drained > 0)
        {
            OnPropertyChanged(nameof(ResultCountText));
            StatusText = $"{Results.Count:n0} files, {_seenHits.Count:n0} hits";
        }
    }

    private readonly record struct QueuedHit(Hit Hit, bool IsDirectory);

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
        ImageOcrPreview = null;
        OnPropertyChanged(nameof(ResultCountText));
    }

    private void LoadPinnedResults()
    {
        ResetResults();
        StageText = "Pinned results";

        foreach (var path in _settingsService.Current.QuickSearchPinnedPaths
                     .Where(path => File.Exists(path) || Directory.Exists(path))
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

    /// <summary>
    /// Answers filename matches from the drive name index for roots on
    /// indexed drives (complete and ranked), and falls back to the
    /// time-boxed folder walk only for roots the index does not cover.
    /// </summary>
    private async Task EnqueueMetadataMatchesAsync(
        IReadOnlyList<string> roots,
        string text,
        ChannelWriter<QueuedHit> pendingHits,
        int scanBudgetMilliseconds,
        CancellationToken token)
    {
        var uncovered = roots;
        if (_volumeNameIndex is not null)
        {
            var result = await _volumeNameIndex
                .SearchAsync(new VolumeTermSearchRequest(text, MaxResultFiles, ScopeRoots: roots), token)
                .ConfigureAwait(false);
            foreach (var match in result.Matches)
            {
                token.ThrowIfCancellationRequested();
                await pendingHits.WriteAsync(new QueuedHit(new Hit(
                    match.Path,
                    0,
                    match.IsDirectory ? "Folder name match" : "Filename/path match",
                    Array.Empty<MatchSpan>(),
                    HitKind.Metadata,
                    Score: match.Score,
                    Route: HitRoute.Indexed), match.IsDirectory), token).ConfigureAwait(false);
            }

            uncovered = result.UncoveredRoots;
        }

        if (uncovered.Count > 0)
            await EnqueueFilesystemMetadataMatchesAsync(uncovered, text, pendingHits, scanBudgetMilliseconds, token).ConfigureAwait(false);
    }

    private static async Task EnqueueFilesystemMetadataMatchesAsync(
        IReadOnlyList<string> roots,
        string text,
        ChannelWriter<QueuedHit> pendingHits,
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

                await pendingHits.WriteAsync(new QueuedHit(new Hit(
                    path,
                    0,
                    "Filename/path match",
                    Array.Empty<MatchSpan>(),
                    HitKind.Metadata,
                    Score: 1), false), token).ConfigureAwait(false);
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

    private static AppShortcutGesture NormalizeShortcutGesture(AppShortcutGesture gesture) =>
        Enum.IsDefined(gesture) ? gesture : AppShortcutGesture.Disabled;
}
