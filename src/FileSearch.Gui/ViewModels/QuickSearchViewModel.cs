using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly HashSet<SearchHitIdentity> _seenHits = [];

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private bool _isPreparing;
    private bool _hitLimitReached;
    private bool _hasIndexedContent, _hasLiveContent;
    private long _searchGeneration;
    private bool _hasExplicitSelection;
    private string _locationScopeNotice = string.Empty;

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
        _mainSearch.RefreshLocationSelections();
        _mainSearch.SelectedIndexedRoots.CollectionChanged += OnSelectedIndexedRootsChanged;
        _mainSearch.PropertyChanged += OnMainSearchStateChanged;

        Results.CollectionChanged += OnResultsChanged;
        RefreshScopeOptions();
        SelectedScope = ScopeOptions.FirstOrDefault(option => option.Value == CurrentConfiguredScope())
            ?? ScopeOptions.FirstOrDefault();
        LoadPinnedResults();
    }

    public event EventHandler? RequestHide;

    public event EventHandler? RequestOpenMainWindow;

    public event EventHandler? ExternalDialogOpened;

    public event EventHandler? ExternalDialogClosed;

    public ObservableCollection<FileResultViewModel> Results { get; } = new();

    public ObservableCollection<QuickSearchScopeOption> ScopeOptions { get; } = new();

    public ObservableCollection<string> SelectedIndexedRoots => _mainSearch.SelectedIndexedRoots;

    public ObservableCollection<LocationSelectionOption> LocationSelections => _mainSearch.LocationSelections;

    public string LocationSelectionSummary => _mainSearch.LocationSelectionSummary;

    public bool IsSelectedIndexedScope => SelectedScope?.Value == QuickSearchScopeKind.SelectedIndexedLocations;

    public string LocationScopeNotice => _locationScopeNotice;

    public string FullSearchHandoffSummary => SelectedScope?.Value == QuickSearchScopeKind.EntireMachineMetadata
        ? "Entire machine metadata stays in Quick Search. Choose a folder or indexed locations to open full search."
        : !_mainSearch.SearchCommand.CanExecute(null)
            ? "Finish or cancel the current main-window operation before opening full search."
            : "Open full search with this scope and Quick matching: all literal words in names/paths, one literal phrase in content.";

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private QuickSearchScopeOption? _selectedScope;
    private FileResultViewModel? _selectedResult;
    private bool _changingPreviewSelection;
    public FileResultViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            // A collection move can transiently update a WPF selection binding.
            // Keep the model and its preview stable while applying ranked rows.
            if (_updatingRankedResults)
            {
                if (!ReferenceEquals(value, _selectedResult)) _selectionBindingNeedsRestore = true;
                return;
            }
            if (value is not null && !string.IsNullOrWhiteSpace(SearchText) && !Results.Contains(value)) return;
            if (!SetProperty(ref _selectedResult, value)) return;
            if (!_selectingAutomatically) MarkSelectionExplicit();
            OnSelectedResultChanged(value);
        }
    }
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

    /// <summary>Keeps an intentional result selection steady as later matches arrive.</summary>
    public void MarkSelectionExplicit()
    {
        if (SelectedResult is not null && !_hasExplicitSelection) _hasExplicitSelection = true;
    }

    public bool HasImageOcrPreview => ImageOcrPreview is not null;

    public string ResultCountText => Results.Count == 1 ? "1 result" : $"{Results.Count:n0} results";

    public void PrepareForShow()
    {
        CancelSearch();
        IsSearching = false;
        _isPreparing = true;
        try
        {
            _mainSearch.RefreshLocationSelections();
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
        OpenFullSearchCommand.NotifyCanExecuteChanged();
        if (_isPreparing)
            return;

        CancelSearch();
        _previewCts?.Cancel();
        IsPreviewVisible = false;
        PreviewContent = string.Empty;
        ImageOcrPreview = null;
        ResetResults();

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
        OnPropertyChanged(nameof(IsSelectedIndexedScope));
        RefreshLocationScopeNotice();
        NotifyFullSearchHandoffChanged();
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
        RefreshLocationScopeNotice();

        if (!_isPreparing && IsFolderScope && !string.IsNullOrWhiteSpace(SearchText))
            RestartSearch(SearchText.Trim());
    }

    private void OnSelectedResultChanged(FileResultViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedResult));
        OpenResultCommand.NotifyCanExecuteChanged();
        RevealResultCommand.NotifyCanExecuteChanged();
        CopyResultPathCommand.NotifyCanExecuteChanged();
        PinResultCommand.NotifyCanExecuteChanged();
        PreviewResultCommand.NotifyCanExecuteChanged();
        OpenOcrPreviewCommand.NotifyCanExecuteChanged();
        if (IsPreviewVisible && value is not null && !_changingPreviewSelection)
            _ = LoadPreviewAsync(value);
        else if (value is null)
        {
            _previewCts?.Cancel();
            PreviewContent = string.Empty;
            ImageOcrPreview = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private async Task OpenResultAsync(FileResultViewModel? result)
    {
        if (!CanUseResult(result)) return;
        result ??= SelectedResult;
        if (result is null)
            return;

        await result.OpenCommand.ExecuteAsync(null).ConfigureAwait(true);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void RevealResult(FileResultViewModel? result)
    {
        if (!CanUseResult(result)) return;
        result ??= SelectedResult;
        if (result is null)
            return;

        result.RevealInExplorerCommand.Execute(null);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanUseResult))]
    private void CopyResultPath(FileResultViewModel? result)
    {
        if (!CanUseResult(result)) return;
        result ??= SelectedResult;
        if (result is null)
            return;

        result.CopyPathCommand.Execute(null);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanPinResult))]
    private void PinResult(FileResultViewModel? result)
    {
        if (!CanPinResult(result)) return;
        result ??= SelectedResult;
        if (result is null || result.IsStoreMessage)
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
            else
                ProjectRankedResults();

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
    private Task PreviewResultAsync(FileResultViewModel? result)
    {
        if (!CanUseResult(result)) return Task.CompletedTask;
        result ??= SelectedResult;
        if (result is null)
            return Task.CompletedTask;

        _changingPreviewSelection = true;
        try { SelectedResult = result; MarkSelectionExplicit(); }
        finally { _changingPreviewSelection = false; }
        return LoadPreviewAsync(result);
    }

    private async Task LoadPreviewAsync(FileResultViewModel result)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        IsPreviewVisible = true;
        ImageOcrPreview = null;
        PreviewContent = "Loading preview...";
        try
        {
            var imagePreview = await ImageOcrPreviewViewModel
                .TryCreateAsync(result.FullPath, result.Hits, token)
                .ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            ImageOcrPreview = imagePreview;
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
            var preview = result.MailMessage is { } mail
                ? await _previewService.LoadMailMessagePreviewAsync(result.FullPath, mail, lines, contextLines: 2, token).ConfigureAwait(true)
                : await _previewService.LoadHitsPreviewAsync(result.FullPath, lines, contextLines: 2, token).ConfigureAwait(true);
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
            if (!token.IsCancellationRequested)
                PreviewContent = $"(failed to load preview: {ex.Message})";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOcrPreview))]
    private async Task OpenOcrPreviewAsync(FileResultViewModel? result)
    {
        if (!CanOpenOcrPreview(result)) return;
        result ??= SelectedResult;
        if (result is null)
            return;

        await result.OpenImageOcrPreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUseResult(FileResultViewModel? result)
    {
        var candidate = result ?? SelectedResult;
        return candidate is not null && (string.IsNullOrWhiteSpace(SearchText) || Results.Contains(candidate));
    }
    private bool CanPinResult(FileResultViewModel? result) => CanUseResult(result) && (result ?? SelectedResult) is { IsStoreMessage: false };

    private bool CanOpenOcrPreview(FileResultViewModel? result) =>
        CanUseResult(result) && (result ?? SelectedResult)?.HasImageOcrPreview == true;

    [RelayCommand(CanExecute = nameof(CanOpenFullSearch))]
    private async Task OpenFullSearchAsync()
    {
        if (!CanOpenFullSearch()) return;
        var scope = SelectedScope?.Value ?? CurrentConfiguredScope();
        if (!_mainSearch.ApplyQuickSearchHandoff(SearchText.Trim(), LocationScopeFor(scope),
            SelectedIndexedRoots.ToArray(), QuickFolderPath, IncludeContentMatches && CanSearchContent, SelectedResult))
        {
            StatusText = string.IsNullOrWhiteSpace(_mainSearch.HandoffFailureText)
                ? "Finish or cancel the current main-window operation before opening full search."
                : _mainSearch.HandoffFailureText;
            return;
        }
        Dismiss();
        RequestOpenMainWindow?.Invoke(this, EventArgs.Empty);
        RequestHide?.Invoke(this, EventArgs.Empty);
        await _mainSearch.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private void RefreshLocationSelections() => _mainSearch.RefreshLocationSelections();

    private bool CanOpenFullSearch() => !string.IsNullOrWhiteSpace(SearchText) &&
        SelectedScope?.Value != QuickSearchScopeKind.EntireMachineMetadata && _mainSearch.SearchCommand.CanExecute(null);

    private void NotifyFullSearchHandoffChanged()
    {
        OpenFullSearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FullSearchHandoffSummary));
    }

    private void RefreshLocationScopeNotice()
    {
        var scope = SelectedScope?.Value ?? CurrentConfiguredScope();
        _locationScopeNotice = scope switch
        {
            QuickSearchScopeKind.EntireMachineMetadata => "Filename/path metadata across ready fixed drives; indexed content is unavailable in this scope.",
            QuickSearchScopeKind.CurrentFolder => "Selected folder; availability is checked when searching.",
            _ => _mainSearch.GetLocationScopeNotice(LocationScopeFor(scope)),
        };
        OnPropertyChanged(nameof(LocationScopeNotice));
        OnPropertyChanged(nameof(LocationSelectionSummary));
    }

    private void OnSelectedIndexedRootsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshLocationScopeNotice();
        if (!_isPreparing && IsSelectedIndexedScope && !string.IsNullOrWhiteSpace(SearchText)) RestartSearch(SearchText.Trim());
    }

    private void OnMainSearchStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SearchViewModel.IsSearching) or nameof(SearchViewModel.IsReplacementBusy))
            NotifyFullSearchHandoffChanged();
        if (args.PropertyName is nameof(SearchViewModel.LocationSelections) or nameof(SearchViewModel.LocationScopeNotice))
        {
            RefreshLocationScopeNotice();
            if (args.PropertyName == nameof(SearchViewModel.LocationSelections) && !_isPreparing && !IsFolderScope &&
                SelectedScope?.Value != QuickSearchScopeKind.EntireMachineMetadata && !string.IsNullOrWhiteSpace(SearchText))
                RestartSearch(SearchText.Trim());
        }
        if (args.PropertyName == nameof(SearchViewModel.LocationSelectionSummary)) OnPropertyChanged(nameof(LocationSelectionSummary));
    }

    private static LocationScopeKind LocationScopeFor(QuickSearchScopeKind scope) => scope switch
    {
        QuickSearchScopeKind.SelectedIndexedLocations => LocationScopeKind.SelectedIndexedLocations,
        QuickSearchScopeKind.AllIndexedLocations => LocationScopeKind.AllIndexedLocations,
        _ => LocationScopeKind.SelectedFolder,
    };

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
        _mainSearch.SelectedIndexedRoots.CollectionChanged -= OnSelectedIndexedRootsChanged;
        _mainSearch.PropertyChanged -= OnMainSearchStateChanged;
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
        ResetResults();
        var cts = new CancellationTokenSource();
        _searchCts?.Dispose();
        _searchCts = cts;
        _ = SearchAfterDebounceAsync(text, _searchGeneration, cts.Token);
    }

    private async Task SearchAsync(string text, long generation, CancellationToken token)
    {
        ResetResults();
        _nameRanking = QuickSearchNameRanking.Compile(text);
        IsSearching = true;
        ImageOcrPreview = null;
        StageText = "Filename and path matches";
        StatusText = "Searching...";

        var scope = SelectedScope?.Value ?? CurrentConfiguredScope();
        var machine = scope == QuickSearchScopeKind.EntireMachineMetadata;
        IReadOnlyList<string> roots;
        if (machine) roots = await Task.Run(GetMachineMetadataRoots, token).ConfigureAwait(true);
        else
        {
            string folder = QuickFolderPath;
            string[] selected = SelectedIndexedRoots.ToArray();
            var resolution = await _mainSearch.ResolveLocationScopeAsync(LocationScopeFor(scope), folder, selected, token).ConfigureAwait(true);
            if (scope == QuickSearchScopeKind.CurrentFolder)
            {
                var folderRoots = resolution.Roots;
                bool exists = await Task.Run(() => folderRoots.Any(Directory.Exists), token).WaitAsync(token).ConfigureAwait(true);
                if (!exists) resolution = new SearchLocationResolution([], "Choose an existing folder to search.");
            }
            if (!IsCurrentSearch(generation, token)) return;
            roots = resolution.Roots;
            _locationScopeNotice = resolution.Notice;
            OnPropertyChanged(nameof(LocationScopeNotice));
        }
        if (!IsCurrentSearch(generation, token)) return;
        if (roots.Count == 0)
        {
            IsSearching = false;
            StatusText = machine ? "No ready fixed drives were found."
                : scope == QuickSearchScopeKind.CurrentFolder ? "Choose an existing folder to search."
                : LocationScopeNotice;
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
        using var contentCts = CancellationTokenSource.CreateLinkedTokenSource(producerToken);
        var contentToken = contentCts.Token;
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
        var contentStatuses = new List<string>();
        var statusGate = new Lock();
        void RecordContentStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.Length > 512)
            {
                int length = char.IsHighSurrogate(message[510]) ? 510 : 511;
                message = message[..length] + "…";
            }
            lock (statusGate)
                if (contentStatuses.Count < 8 && !contentStatuses.Contains(message, StringComparer.Ordinal)) contentStatuses.Add(message);
        }
        void FinishCurrentSearch()
        {
            if (metadataProducer.IsCompletedSuccessfully) _hitLimitReached |= metadataProducer.Result;
            FinishSearchStatus();
            string coverage;
            lock (statusGate) coverage = string.Join(" · ", contentStatuses);
            if (coverage.Length > 0) StatusText += " · Content: " + coverage;
        }
        var includeContent = !machine && IncludeContentMatches && CanSearchContent;
        var request = includeContent ? new SearchRequest(
            query, roots, _mainSearch.BuildQuickSearchWalkerOptions(),
            Progress: null, UseIndex: true, Status: RecordContentStatus, RawQuery: text, Mode: QueryMode.PlainText) : null;
        var contentProducer = request is not null
            ? Task.Run(async () =>
            {
                try
                {
                    await foreach (var hit in _searcher.SearchAsync(request, contentToken).ConfigureAwait(false))
                        await pendingHits.Writer.WriteAsync(new QueuedHit(hit, false, FromContentSearch: true), contentToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (contentToken.IsCancellationRequested && !producerToken.IsCancellationRequested) { }
            }, contentToken)
            : Task.CompletedTask;
        var producers = Task.WhenAll(metadataProducer, contentProducer);

        try
        {
            while (IsCurrentSearch(generation, token))
            {
                DrainPendingHits(pendingHits.Reader, contentCts);
                if (producers.IsCompleted && !pendingHits.Reader.TryPeek(out _))
                    break;
                await Task.Delay(DrainDelayMilliseconds, token).ConfigureAwait(true);
            }

            await producers.ConfigureAwait(true);
            if (IsCurrentSearch(generation, token))
                FinishCurrentSearch();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSearch(generation, token) && _hitLimitReached)
                FinishCurrentSearch();
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

    private void DrainPendingHits(ChannelReader<QueuedHit> pendingHits, CancellationTokenSource contentCts)
    {
        var started = Stopwatch.GetTimestamp();
        var drained = 0;
        while (drained < 128 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(8) &&
               pendingHits.TryRead(out var pending))
        {
            var hit = pending.Hit;
            var key = SearchHitIdentity.Create(hit);
            drained++;
            if (pending.FromContentSearch)
            {
                if (_contentLimitReached) continue;
                if (hit.Kind == HitKind.Content)
                {
                    _hasIndexedContent |= hit.Route == HitRoute.Indexed;
                    _hasLiveContent |= hit.Route == HitRoute.Live;
                    StageText = _hasIndexedContent && _hasLiveContent ? "Indexed and live content matches"
                        : _hasIndexedContent ? "Indexed content matches" : "Live content fallback matches";
                }
                if (!_contentHitsSeen.Add(key)) continue;
                _contentFilesSeen.Add(hit.ResultKey);
                if (_contentHitsSeen.Count >= MaxHits || _contentFilesSeen.Count >= MaxResultFiles)
                {
                    _contentLimitReached = _hitLimitReached = true;
                    contentCts.Cancel();
                }
            }
            if (hit.Kind == HitKind.Metadata && hit.Route == HitRoute.Indexed && !_hasIndexedContent && !_hasLiveContent)
                StageText = pending.FromContentSearch ? "Indexed name and path matches" : "Drive name index matches";
            if (_seenHits.Contains(key))
                continue;

            if (!_filesByPath.TryGetValue(hit.ResultKey, out var file))
            {
                var recordOpened = _indexUsageStore is null ? null
                    : new Func<string, CancellationToken, Task>(_indexUsageStore.RecordFileOpenedAsync);
                file = new FileResultViewModel(hit.Path, _fileLauncher, recordOpened, isDirectory: pending.IsDirectory)
                {
                    IsPinned = hit.MailMessage?.StoreFingerprint is null && IsPinned(hit.Path),
                };
                file.AddHit(hit);
                if (!AdmitCandidate(file)) { _hitLimitReached = true; continue; }
                _filesByPath[hit.ResultKey] = file;
            }
            else
            {
                file.AddHit(hit);
            }
            _seenHits.Add(key);
        }

        if (drained > 0)
        {
            ProjectRankedResults();
            OnPropertyChanged(nameof(ResultCountText));
            StatusText = $"{Results.Count:n0} results, {Results.Sum(file => file.HitCount):n0} hits";
        }
    }

    private readonly record struct QueuedHit(Hit Hit, bool IsDirectory, bool FromContentSearch = false);

    private void FinishSearchStatus()
    {
        StatusText = _hitLimitReached
            ? $"Showing first {Results.Count:n0} ranked results · limited search"
            : Results.Count == 0
                ? "No matches"
                : $"{Results.Count:n0} results, {Results.Sum(file => file.HitCount):n0} hits";
        StageText = Results.Count == 0 ? string.Empty : StageText;
    }

    private void ResetResults()
    {
        _hitLimitReached = false;
        _contentLimitReached = false;
        _hasExplicitSelection = false;
        _selectionBindingNeedsRestore = false;
        _nameRanking = null;
        _hasIndexedContent = _hasLiveContent = false;
        _filesByPath.Clear();
        _seenHits.Clear();
        _contentHitsSeen.Clear();
        _contentFilesSeen.Clear();
        SelectAutomatically(null);
        Results.Clear();
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

        SelectAutomatically(Results.FirstOrDefault());
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
        Enum.IsDefined(scope) ? scope : QuickSearchScopeKind.CurrentFolder;

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
    private async Task<bool> EnqueueMetadataMatchesAsync(
        IReadOnlyList<string> roots,
        string text,
        ChannelWriter<QueuedHit> pendingHits,
        int scanBudgetMilliseconds,
        CancellationToken token)
    {
        var uncovered = roots;
        bool limited = false;
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
            limited = result.TotalMatches > result.Matches.Count;
        }

        if (uncovered.Count > 0)
            limited |= await EnqueueRankedFilesystemMetadataMatchesAsync(uncovered, text, pendingHits, scanBudgetMilliseconds, token).ConfigureAwait(false);
        return limited;
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
