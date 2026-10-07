using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

public sealed partial class ReplacementItemViewModel : ObservableObject
{
    public ReplacementItemViewModel(ReplacementItem item) { Item = item; _isChecked = item.CanApply; _status = item.SkipReason ?? "Ready"; }
    public ReplacementItem Item { get; }
    public string Path => Item.Path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public int ChangeCount => Item.ChangeCount;
    public bool CanApply => Item.CanApply;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string? _destination;
    public string Preview => string.Join("\n\n", Item.Changes.Select(change => $"{change.Location}\nBEFORE\n{change.Before}\n\nAFTER\n{change.After}"));
}

public sealed partial class ReplacementViewModel : ObservableObject, IDisposable
{
    private readonly IReplacementService _service;
    private readonly SearchViewModel _search;
    private readonly ISettingsService _settings;
    private readonly HistoryViewModel _history;
    private readonly StatusBarViewModel _status;
    private readonly IIndexingService? _indexing;
    private readonly IBackgroundIndexerProcessService? _background;
    private CancellationTokenSource? _cancellation;
    private ReplacementPlan? _plan;
    private int _generation;
    private bool _disposed;
    private bool _externalBusy;

    public ReplacementViewModel(IReplacementService service, SearchViewModel search, ISettingsService settings,
        HistoryViewModel history, StatusBarViewModel status, IIndexingService? indexing = null, IBackgroundIndexerProcessService? background = null)
    {
        _service = service; _search = search; _settings = settings; _history = history; _status = status; _indexing = indexing; _background = background;
        _search.PropertyChanged += SearchChanged;
        _ = RefreshUndoAvailabilityAsync();
    }

    public ObservableCollection<ReplacementItemViewModel> Items { get; } = [];
    public IReadOnlyList<ReplacementTarget> Targets { get; } = Enum.GetValues<ReplacementTarget>();
    public IReadOnlyList<ReplacementNameTarget> NameTargets { get; } = Enum.GetValues<ReplacementNameTarget>();
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceWith = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private ReplacementTarget _target;
    [ObservableProperty] private ReplacementNameTarget _nameTarget = ReplacementNameTarget.Both;
    [ObservableProperty] private bool _includeExtensions;
    [ObservableProperty] private bool _includeFormulas;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string _summary = "Preview changes before applying.";
    [ObservableProperty] private ReplacementItemViewModel? _selectedItem;
    public bool IsNames => Target == ReplacementTarget.Names;
    public bool IsContents => !IsNames;
    public bool CanEdit => !IsBusy && !_externalBusy;
    internal bool CanStartExternalOperation => !IsBusy && !_search.IsSearching;
    internal void SetExternalBusy(bool busy)
    {
        _externalBusy = busy; _search.IsReplacementBusy = busy || IsBusy;
        OnPropertyChanged(nameof(CanEdit)); PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged(); ToggleCommand.NotifyCanExecuteChanged(); ClearFindCommand.NotifyCanExecuteChanged();
    }
    public string ScopeText => $"Folder: {_search.SearchPath} · {(_search.IncludeSubfolders ? "including subfolders" : "this folder only")} · {_search.FilePatternSummary}";
    public string SelectedPreview => SelectedItem is { } item
        ? item.Path + (item.Destination is { } destination ? "\nProposed path: " + destination : "") + "\n\n" + item.Preview
        : "Select an item to review its changes.";
    public int CheckedChanges => Items.Where(item => item.IsChecked && item.CanApply).Sum(item => item.ChangeCount);

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle() => IsOpen = !IsOpen;
    private bool CanToggle() => CanEdit && !_search.IsSearching;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && string.IsNullOrEmpty(FindText) && _search.SearchMode is QueryMode.PlainText or QueryMode.Regex)
        {
            FindText = _search.QueryText;
        }
    }
    partial void OnFindTextChanged(string value) => Invalidate();
    partial void OnReplaceWithChanged(string value) => Invalidate();
    partial void OnUseRegexChanged(bool value) => Invalidate();
    partial void OnMatchCaseChanged(bool value) => Invalidate();
    partial void OnTargetChanged(ReplacementTarget value) { OnPropertyChanged(nameof(IsNames)); OnPropertyChanged(nameof(IsContents)); Invalidate(); }
    partial void OnNameTargetChanged(ReplacementNameTarget value) => Invalidate();
    partial void OnIncludeExtensionsChanged(bool value) => Invalidate();
    partial void OnIncludeFormulasChanged(bool value) => Invalidate();
    partial void OnSelectedItemChanged(ReplacementItemViewModel? value) => OnPropertyChanged(nameof(SelectedPreview));
    partial void OnCanUndoChanged(bool value) => UndoCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        _search.IsReplacementBusy = value || _externalBusy;
        OnPropertyChanged(nameof(CanStartExternalOperation));
        OnPropertyChanged(nameof(CanEdit));
        PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); UndoCommand.NotifyCanExecuteChanged(); ToggleCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ClearFindCommand.NotifyCanExecuteChanged();
    }

    private void SearchChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchViewModel.IsSearching)) { ToggleCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanStartExternalOperation)); }
        if (args.PropertyName is nameof(SearchViewModel.SearchPath) or nameof(SearchViewModel.FileNamePattern) or nameof(SearchViewModel.ExcludeFileNamePattern) or
            nameof(SearchViewModel.IncludeSubfolders) or nameof(SearchViewModel.EnableDocumentExtraction) or nameof(SearchViewModel.SkipUnknownFileTypes) or
            nameof(SearchViewModel.AdditionalPlainTextExtensions) or nameof(SearchViewModel.MinSizeKB) or nameof(SearchViewModel.MaxSizeKB) or
            nameof(SearchViewModel.ModifiedAfterEnabled) or nameof(SearchViewModel.ModifiedBeforeEnabled) or nameof(SearchViewModel.ModifiedAfter) or nameof(SearchViewModel.ModifiedBefore))
        { OnPropertyChanged(nameof(ScopeText)); Invalidate(); }
    }

    private void Invalidate()
    {
        _generation++; _plan = null;
        foreach (var item in Items) item.PropertyChanged -= ItemChanged;
        Items.Clear(); SelectedItem = null;
        Summary = "Preview changes before applying.";
        PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CheckedChanges));
    }

    private bool CanPreview() => CanEdit && !_search.IsSearching && !string.IsNullOrEmpty(FindText) && Directory.Exists(_search.SearchPath);
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        var generation = _generation;
        IsBusy = true; _cancellation?.Dispose(); _cancellation = new(); Summary = "Scanning the current folder…";
        try
        {
            var request = new ReplacementRequest([_search.SearchPath], _search.BuildReplacementWalkerOptions(Target), FindText, ReplaceWith, Target,
                UseRegex, MatchCase, NameTarget, IncludeExtensions, IncludeFormulas, _settings.Current.IndexedLocations.Select(location => location.Root).ToArray(),
                _search.AdditionalPlainTextExtensions.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(extension => extension.StartsWith('.') ? extension : "." + extension).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var plan = await _service.PreviewAsync(request, _cancellation.Token);
            if (generation != _generation) { Summary = "Options changed; preview again."; return; }
            _plan = plan;
            foreach (var item in plan.Items)
            {
                var row = new ReplacementItemViewModel(item); row.PropertyChanged += ItemChanged; Items.Add(row);
            }
            SelectedItem = Items.FirstOrDefault(item => item.CanApply) ?? Items.FirstOrDefault();
            UpdateSelection();
            Summary = $"{Items.Count(item => item.CanApply):N0} items · {CheckedChanges:N0} changes checked · {Items.Count(item => !item.CanApply):N0} skipped";
        }
        catch (OperationCanceledException) { Summary = "Preview cancelled. No changes applied."; }
        catch (Exception exception) { Summary = "Preview failed: " + exception.Message; }
        finally { IsBusy = false; _status.Text = Summary; }
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ReplacementItemViewModel.IsChecked)) UpdateSelection();
    }
    private void UpdateSelection()
    {
        foreach (var row in Items)
        {
            var path = row.Item.NewPath;
            if (path is null) continue;
            foreach (var parent in Items.Where(item => item.IsChecked && item.CanApply && item.Item.IsDirectory && item != row).OrderByDescending(item => item.Path.Length))
                path = Remap(path, parent.Path, parent.Item.NewPath!, true);
            row.Destination = path;
        }
        OnPropertyChanged(nameof(CheckedChanges)); ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedPreview));
    }
    private bool CanApply() => CanEdit && _plan is not null && Items.Any(item => item.IsChecked && item.CanApply);
    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunBatchAsync(false);
    private bool CanRunUndo() => CanEdit && !_search.IsSearching && CanUndo;
    [RelayCommand(CanExecute = nameof(CanRunUndo))]
    private Task UndoAsync() => RunBatchAsync(true);

    private async Task RunBatchAsync(bool undo)
    {
        if (!undo && _plan is null) return;
        IsBusy = true; _cancellation?.Dispose(); _cancellation = new(); Summary = undo ? "Undoing the last batch…" : "Applying checked changes…";
        try
        {
            var result = undo ? await _service.UndoAsync(_cancellation.Token) :
                await _service.ApplyAsync(_plan!, Items.Where(item => item.IsChecked && item.CanApply).Select(item => item.Item.Id).ToHashSet(), _cancellation.Token);
            _plan = null;
            UpdateSavedPaths(result);
            Summary = $"{(undo ? "Undo" : "Apply")}: {result.SucceededCount:N0} succeeded · {result.Outcomes.Count - result.SucceededCount:N0} skipped or failed{(result.Cancelled ? " · cancelled" : "")}";
            if (undo) InvalidateRowsAfterUndo(result);
            try { await RefreshIndexesAsync(result); }
            catch (Exception exception) { Summary += " · index refresh failed: " + exception.Message; }
            try { await _search.RefreshAfterReplacementAsync(); }
            catch (Exception exception) { Summary += " · results refresh failed: " + exception.Message; }
        }
        catch (Exception exception) { Summary = "Operation failed: " + exception.Message + ". Recovery data is retained."; }
        finally { IsBusy = false; _status.Text = Summary; await RefreshUndoAvailabilityAsync(); }
    }

    internal async Task HandleExternalBatchAsync(ReplacementBatchResult result)
    {
        Invalidate();
        UpdateSavedPaths(result);
        try { await RefreshIndexesAsync(result); }
        catch (Exception exception) { _status.Text = "Changes completed; index refresh failed: " + exception.Message; }
        try { await _search.RefreshAfterReplacementAsync(); }
        catch (Exception exception) { _status.Text = "Changes completed; results refresh failed: " + exception.Message; }
        await RefreshUndoAvailabilityAsync();
    }

    private void UpdateSavedPaths(ReplacementBatchResult result)
    {
            foreach (var outcome in result.Outcomes)
            {
                var row = Items.FirstOrDefault(item => string.Equals(item.Path, outcome.Path, StringComparison.OrdinalIgnoreCase));
                if (row is not null) row.Status = outcome.Message;
                if (outcome.Succeeded && outcome.NewPath is not null)
                {
                    _history.RemapReplacementPaths(outcome.Path, outcome.NewPath, outcome.IsDirectory);
                    _search.RemapReplacementPaths(outcome.Path, outcome.NewPath, outcome.IsDirectory);
                    _settings.Update(settings =>
                    {
                        settings.QuickSearchPinnedPaths = settings.QuickSearchPinnedPaths.Select(path => Remap(path, outcome.Path, outcome.NewPath, outcome.IsDirectory)).ToList();
                        settings.QuickSearchFolderPath = Remap(settings.QuickSearchFolderPath, outcome.Path, outcome.NewPath, outcome.IsDirectory);
                    });
                }
            }
    }

    private void InvalidateRowsAfterUndo(ReplacementBatchResult result)
    {
        foreach (var row in Items) row.PropertyChanged -= ItemChanged;
        Items.Clear(); SelectedItem = null;
        foreach (var outcome in result.Outcomes)
            Items.Add(new ReplacementItemViewModel(new(Guid.NewGuid().ToString("N"), outcome.Path, outcome.IsDirectory, "", "", 0,
                [new("Undo", outcome.Path, outcome.NewPath ?? outcome.Message)], outcome.NewPath, outcome.Message)));
        SelectedItem = Items.FirstOrDefault();
    }

    private async Task RefreshIndexesAsync(ReplacementBatchResult result)
    {
        foreach (var location in _settings.Current.IndexedLocations)
        {
            if (!result.Outcomes.Any(outcome => outcome.Succeeded && (Within(outcome.Path, location.Root) || outcome.NewPath is not null && Within(outcome.NewPath, location.Root)))) continue;
            var options = _search.BuildIndexWalkerOptions(location);
            var indexed = new IndexedLocation(location.Root, options, location.WatchEnabled);
            if (_background is null || !await _background.QueueRootRefreshAsync(indexed, IndexQueuePriority.High, CancellationToken.None))
                if (_indexing is not null) await _indexing.EnqueueRootRefreshAsync(location.Root, options, IndexQueuePriority.High, CancellationToken.None);
        }
    }

    private async Task RefreshUndoAvailabilityAsync()
    {
        try { var available = await _service.CanUndoAsync(CancellationToken.None); if (!_disposed) CanUndo = available; }
        catch (Exception exception) { if (!_disposed) _status.Text = "Could not read replacement recovery data: " + exception.Message; }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))] private void Cancel() { _cancellation?.Cancel(); if (_search.CancelCommand.CanExecute(null)) _search.CancelCommand.Execute(null); }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void ClearFind() => FindText = "";
    internal static bool Within(string path, string root) => !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root) &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    internal static string Remap(string path, string oldPath, string newPath, bool directory) =>
        path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? newPath : directory && Within(path, oldPath) ? newPath.TrimEnd('\\', '/') + path[oldPath.TrimEnd('\\', '/').Length..] : path;
    public void Dispose() { if (_disposed) return; _disposed = true; _search.PropertyChanged -= SearchChanged; _cancellation?.Cancel(); _cancellation?.Dispose(); }
}
