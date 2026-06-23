using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Recent queries/paths and saved scopes: the "pick up where you left off"
/// state. Owns its slice of the persisted settings.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private const int MaxHistoryEntries = 15;
    private static readonly char[] s_scopePatternSeparators = [';', ','];

    private readonly ISettingsService _settingsService;
    private readonly ApplicationSettingsViewModel _applicationSettings;
    private readonly StatusBarViewModel _status;
    private readonly IFileOpenPicker? _fileOpenPicker;
    private readonly IFileSavePicker? _fileSavePicker;
    private readonly ObservableCollection<SidebarScopeItem> _scopeItems = new();
    private string _activeScopePattern = string.Empty;

    public HistoryViewModel(
        ISettingsService settingsService,
        ApplicationSettingsViewModel applicationSettings,
        StatusBarViewModel status,
        IFileOpenPicker? fileOpenPicker = null,
        IFileSavePicker? fileSavePicker = null)
    {
        _settingsService = settingsService;
        _applicationSettings = applicationSettings;
        _status = status;
        _fileOpenPicker = fileOpenPicker;
        _fileSavePicker = fileSavePicker;
        ScopeList = new PagedSidebarList<SidebarScopeItem>(
            _scopeItems,
            MatchesScope,
            "scopes",
            _applicationSettings.SidebarPageSize);
        RecentPathList = new PagedSidebarList<string>(
            RecentPaths,
            MatchesText,
            "recent folders",
            _applicationSettings.SidebarPageSize);
        SavedSearchList = new PagedSidebarList<SavedSearchSettings>(
            SavedSearches,
            MatchesSavedSearch,
            "saved searches",
            _applicationSettings.SidebarPageSize);
        FavoriteResultList = new PagedSidebarList<FavoriteResultSettings>(
            FavoriteResults,
            MatchesFavorite,
            "favorites",
            _applicationSettings.SidebarPageSize);
        WorkspaceList = new PagedSidebarList<WorkspaceSettings>(
            Workspaces,
            MatchesWorkspace,
            "workspaces",
            _applicationSettings.SidebarPageSize);

        var saved = _settingsService.Current;
        foreach (var search in saved.SavedSearches.Select(NormalizeSavedSearch).Where(IsUsableSavedSearch))
            SavedSearches.Add(search);

        if (SavedSearches.Count == 0)
            LoadLegacySavedSearches(saved);

        foreach (var query in saved.RecentQueries) RecentQueries.Add(query);
        foreach (var path in saved.RecentPaths) RecentPaths.Add(path);
        foreach (var favorite in saved.FavoriteResults.Select(NormalizeFavorite).Where(IsUsableFavorite))
            FavoriteResults.Add(favorite);
        foreach (var workspace in saved.Workspaces.Select(NormalizeWorkspace).Where(IsUsableWorkspace))
            Workspaces.Add(workspace);
        foreach (var scope in saved.CustomScopes.Where(scope => !string.IsNullOrWhiteSpace(scope.Name)))
        {
            CustomScopes.Add(new SearchScope
            {
                Name = scope.Name.Trim(),
                FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
            });
        }

        SavedSearches.CollectionChanged += (_, _) => ClearSavedSearchesCommand.NotifyCanExecuteChanged();
        FavoriteResults.CollectionChanged += (_, _) => ClearFavoriteResultsCommand.NotifyCanExecuteChanged();
        Workspaces.CollectionChanged += (_, _) =>
        {
            ClearWorkspacesCommand.NotifyCanExecuteChanged();
            ExportAllWorkspacesCommand.NotifyCanExecuteChanged();
        };
        RecentQueries.CollectionChanged += (_, _) => ClearRecentQueriesCommand.NotifyCanExecuteChanged();
        RecentPaths.CollectionChanged += (_, _) => ClearRecentPathsCommand.NotifyCanExecuteChanged();
        CustomScopes.CollectionChanged += (_, _) =>
        {
            ClearCustomScopesCommand.NotifyCanExecuteChanged();
            RebuildScopeItems();
        };
        RebuildScopeItems();
        _applicationSettings.PropertyChanged += OnApplicationSettingsPropertyChanged;
    }

    public PagedSidebarList<SidebarScopeItem> ScopeList { get; }

    public PagedSidebarList<string> RecentPathList { get; }

    public PagedSidebarList<SavedSearchSettings> SavedSearchList { get; }

    public PagedSidebarList<FavoriteResultSettings> FavoriteResultList { get; }

    public PagedSidebarList<WorkspaceSettings> WorkspaceList { get; }

    public ObservableCollection<SavedSearchSettings> SavedSearches { get; } = new();

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

    public ObservableCollection<SearchScope> CustomScopes { get; } = new();

    public ObservableCollection<FavoriteResultSettings> FavoriteResults { get; } = new();

    public ObservableCollection<WorkspaceSettings> Workspaces { get; } = new();

    public void UpdateActiveScope(string? fileNamePattern)
    {
        var normalized = NormalizeScopePattern(fileNamePattern);
        _activeScopePattern = normalized;
        foreach (var scope in _scopeItems)
            scope.IsActive = string.Equals(
                NormalizeScopePattern(scope.FileNamePattern),
                normalized,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Promotes the attempt into saved searches, both legacy lists, and persists.</summary>
    public void RecordSearch(SavedSearchSettings search)
    {
        var savedSearch = NormalizeSavedSearch(search);
        if (!IsUsableSavedSearch(savedSearch))
            return;

        PromoteSavedSearch(savedSearch);
        SearchHistory.PromoteToFront(RecentQueries, savedSearch.QueryText, MaxHistoryEntries);
        SearchHistory.PromoteToFront(RecentPaths, savedSearch.SearchPath, MaxHistoryEntries);

        // Persist immediately so history survives a crash.
        SaveHistory();
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

        SaveHistory();
        _status.Text = $"Saved scope {trimmedName}.";
    }

    public void SaveWorkspace(WorkspaceSettings workspace)
    {
        var normalized = NormalizeWorkspace(workspace);
        if (!IsUsableWorkspace(normalized))
            return;

        UpsertWorkspace(normalized);
        SaveHistory();
        _status.Text = $"Saved workspace {normalized.DisplayName}.";
    }

    public void ReplaceCustomScopes(IEnumerable<SearchScope> scopes)
    {
        CustomScopes.Clear();
        foreach (var scope in scopes.Select(NormalizeScope).Where(IsUsableScope))
            CustomScopes.Add(scope);

        SaveHistory();
    }

    public void ReplaceFavoriteResults(IEnumerable<FavoriteResultSettings> favorites)
    {
        FavoriteResults.Clear();
        foreach (var favorite in favorites.Select(NormalizeFavorite).Where(IsUsableFavorite))
            FavoriteResults.Add(favorite);

        SaveHistory();
    }

    public bool IsFavorite(string path) =>
        FavoriteResults.Any(favorite =>
            string.Equals(favorite.Path, path?.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool ToggleFavorite(string path)
    {
        var trimmedPath = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath))
            return false;

        for (var i = FavoriteResults.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(FavoriteResults[i].Path, trimmedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            FavoriteResults.RemoveAt(i);
            SaveHistory();
            return false;
        }

        FavoriteResults.Insert(0, new FavoriteResultSettings
        {
            Path = trimmedPath,
            AddedUtc = DateTime.UtcNow,
        });

        while (FavoriteResults.Count > MaxFavoriteEntries)
            FavoriteResults.RemoveAt(FavoriteResults.Count - 1);

        SaveHistory();
        return true;
    }

    public void UpdateFavoritePath(string oldPath, string newPath)
    {
        var changed = false;
        foreach (var favorite in FavoriteResults)
        {
            if (!string.Equals(favorite.Path, oldPath, StringComparison.OrdinalIgnoreCase))
                continue;

            favorite.Path = newPath;
            changed = true;
        }

        if (!changed)
            return;

        FavoriteResultList.Refresh();
        SaveHistory();
    }

    public void RemoveFavoritePath(string path)
    {
        var removed = false;
        for (var i = FavoriteResults.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(FavoriteResults[i].Path, path, StringComparison.OrdinalIgnoreCase))
                continue;

            FavoriteResults.RemoveAt(i);
            removed = true;
        }

        if (removed)
            SaveHistory();
    }

    [RelayCommand]
    private void RemoveFavoriteResult(FavoriteResultSettings? favorite)
    {
        if (favorite is null)
            return;

        RemoveFavoritePath(favorite.Path);
    }

    [RelayCommand(CanExecute = nameof(CanClearFavoriteResults))]
    private void ClearFavoriteResults()
    {
        FavoriteResults.Clear();
        SaveHistory();
    }

    private bool CanClearFavoriteResults() => FavoriteResults.Count > 0;

    [RelayCommand]
    private void RemoveWorkspace(WorkspaceSettings? workspace)
    {
        if (workspace is null)
            return;

        for (var i = Workspaces.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Workspaces[i].Name, workspace.Name, StringComparison.OrdinalIgnoreCase))
                Workspaces.RemoveAt(i);
        }

        SaveHistory();
    }

    [RelayCommand]
    private void ToggleWorkspaceRunOnLoad(WorkspaceSettings? workspace)
    {
        if (workspace is null)
            return;

        var normalized = NormalizeWorkspace(workspace);
        if (!IsUsableWorkspace(normalized))
            return;

        normalized.RunOnLoad = !normalized.RunOnLoad;

        for (var i = 0; i < Workspaces.Count; i++)
        {
            if (!string.Equals(Workspaces[i].Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            Workspaces[i] = normalized;
            SaveHistory();
            _status.Text = normalized.RunOnLoad
                ? $"Workspace {normalized.DisplayName} will run when loaded."
                : $"Workspace {normalized.DisplayName} will load without running.";
            return;
        }

        UpsertWorkspace(normalized);
        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearWorkspaces))]
    private void ClearWorkspaces()
    {
        Workspaces.Clear();
        SaveHistory();
    }

    private bool CanClearWorkspaces() => Workspaces.Count > 0;

    [RelayCommand]
    private void ImportWorkspace()
    {
        if (_fileOpenPicker is null)
        {
            _status.Text = "Workspace import is not available.";
            return;
        }

        var path = _fileOpenPicker.PickOpenFile(
            "Import workspace",
            WorkspaceFileFilter);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var imported = ReadWorkspaceFile(path)
                .Select(NormalizeWorkspace)
                .Where(IsUsableWorkspace)
                .ToArray();
            if (imported.Length == 0)
            {
                _status.Text = "No usable workspaces found in the selected file.";
                return;
            }

            foreach (var workspace in imported)
                UpsertWorkspace(workspace);

            SaveHistory();
            _status.Text = imported.Length == 1
                ? $"Imported workspace {imported[0].DisplayName}."
                : $"Imported {imported.Length:n0} workspaces.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _status.Text = $"Could not import workspace: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportWorkspace(WorkspaceSettings? workspace)
    {
        if (workspace is null)
            return;

        if (_fileSavePicker is null)
        {
            _status.Text = "Workspace export is not available.";
            return;
        }

        var normalized = NormalizeWorkspace(workspace);
        if (!IsUsableWorkspace(normalized))
            return;

        var path = _fileSavePicker.PickSaveFile(
            "Export workspace",
            WorkspaceFileFilter,
            BuildWorkspaceFileName(normalized.DisplayName));
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            WriteWorkspaceFile(path, new WorkspaceFile
            {
                Workspace = normalized,
            });
            _status.Text = $"Exported workspace {normalized.DisplayName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = $"Could not export workspace: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportAllWorkspaces))]
    private void ExportAllWorkspaces()
    {
        if (_fileSavePicker is null)
        {
            _status.Text = "Workspace export is not available.";
            return;
        }

        var workspaces = Workspaces
            .Select(NormalizeWorkspace)
            .Where(IsUsableWorkspace)
            .ToArray();
        if (workspaces.Length == 0)
            return;

        var path = _fileSavePicker.PickSaveFile(
            "Export workspaces",
            WorkspaceFileFilter,
            "filesearch-workspaces.filesearch-workspace.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            WriteWorkspaceFile(path, new WorkspaceFile
            {
                Workspaces = workspaces.ToList(),
            });
            _status.Text = $"Exported {workspaces.Length:n0} workspaces.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = $"Could not export workspaces: {ex.Message}";
        }
    }

    private bool CanExportAllWorkspaces() => _fileSavePicker is not null && Workspaces.Count > 0;

    [RelayCommand]
    private void RemoveCustomScope(SearchScope? scope)
    {
        if (scope is null)
            return;

        CustomScopes.Remove(scope);
        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearCustomScopes))]
    private void ClearCustomScopes()
    {
        CustomScopes.Clear();
        SaveHistory();
    }

    private bool CanClearCustomScopes() => CustomScopes.Count > 0;

    [RelayCommand]
    private void RemoveSavedSearch(SavedSearchSettings? search)
    {
        if (search is null)
            return;

        for (var i = SavedSearches.Count - 1; i >= 0; i--)
        {
            if (HasSameSavedSearchIdentity(SavedSearches[i], search))
                SavedSearches.RemoveAt(i);
        }

        if (!SavedSearches.Any(item =>
                string.Equals(item.QueryText, search.QueryText, StringComparison.OrdinalIgnoreCase)))
            SearchHistory.Remove(RecentQueries, search.QueryText);

        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearSavedSearches))]
    private void ClearSavedSearches()
    {
        SavedSearches.Clear();
        RecentQueries.Clear();
        SaveHistory();
    }

    private bool CanClearSavedSearches() => SavedSearches.Count > 0;

    [RelayCommand]
    private void RemoveRecentPath(string? path)
    {
        SearchHistory.Remove(RecentPaths, path);
        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearRecentPaths))]
    private void ClearRecentPaths()
    {
        RecentPaths.Clear();
        SaveHistory();
    }

    private bool CanClearRecentPaths() => RecentPaths.Count > 0;

    [RelayCommand]
    private void RemoveRecentQuery(string? query)
    {
        SearchHistory.Remove(RecentQueries, query);
        for (var i = SavedSearches.Count - 1; i >= 0; i--)
        {
            if (string.Equals(SavedSearches[i].QueryText, query?.Trim(), StringComparison.OrdinalIgnoreCase))
                SavedSearches.RemoveAt(i);
        }

        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearRecentQueries))]
    private void ClearRecentQueries()
    {
        RecentQueries.Clear();
        SavedSearches.Clear();
        SaveHistory();
    }

    private bool CanClearRecentQueries() => RecentQueries.Count > 0;

    /// <summary>Persists this view model's slice of the settings.</summary>
    public void SaveHistory()
    {
        _settingsService.Update(settings =>
        {
            settings.RecentQueries = RecentQueries.ToList();
            settings.RecentPaths = RecentPaths.ToList();
            settings.SavedSearches = SavedSearches
                .Select(NormalizeSavedSearch)
                .Where(IsUsableSavedSearch)
                .ToList();
            settings.FavoriteResults = FavoriteResults
                .Select(NormalizeFavorite)
                .Where(IsUsableFavorite)
                .ToList();
            settings.Workspaces = Workspaces
                .Select(NormalizeWorkspace)
                .Where(IsUsableWorkspace)
                .ToList();
            settings.CustomScopes = CustomScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope.Name))
                .Select(scope => new SearchScope
                {
                    Name = scope.Name.Trim(),
                    FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
                })
                .ToList();
        });
    }

    private void PromoteSavedSearch(SavedSearchSettings search)
    {
        var matchIndex = -1;
        for (var i = 0; i < SavedSearches.Count; i++)
        {
            if (HasSameSavedSearchIdentity(SavedSearches[i], search))
            {
                matchIndex = i;
                break;
            }
        }

        for (var i = SavedSearches.Count - 1; i >= 0; i--)
        {
            if (i != matchIndex && HasSameSavedSearchIdentity(SavedSearches[i], search))
            {
                SavedSearches.RemoveAt(i);
                if (i < matchIndex)
                    matchIndex--;
            }
        }

        if (matchIndex < 0)
        {
            SavedSearches.Insert(0, search);
        }
        else
        {
            SavedSearches[matchIndex] = search;
            if (matchIndex > 0)
                SavedSearches.Move(matchIndex, 0);
        }

        while (SavedSearches.Count > MaxHistoryEntries)
            SavedSearches.RemoveAt(SavedSearches.Count - 1);
    }

    private void UpsertWorkspace(WorkspaceSettings normalized)
    {
        for (var i = Workspaces.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Workspaces[i].Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
                Workspaces.RemoveAt(i);
        }

        Workspaces.Insert(0, normalized);
        while (Workspaces.Count > MaxWorkspaceEntries)
            Workspaces.RemoveAt(Workspaces.Count - 1);
    }

    private static bool HasSameSavedSearchIdentity(SavedSearchSettings left, SavedSearchSettings right) =>
        string.Equals(left.QueryText?.Trim(), right.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.SearchPath?.Trim(), right.SearchPath?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.SearchTarget == right.SearchTarget;

    private void LoadLegacySavedSearches(AppSettings saved)
    {
        var path = saved.RecentPaths.FirstOrDefault() ?? string.Empty;
        foreach (var query in saved.RecentQueries)
        {
            var search = NormalizeSavedSearch(new SavedSearchSettings
            {
                QueryText = query,
                SearchPath = path,
                EnableImageOcr = saved.EnableImageOcr,
                SkipUnknownFileTypes = saved.SkipUnknownFileTypes,
                UseIndex = saved.UseIndex,
            });

            if (IsUsableSavedSearch(search))
                SavedSearches.Add(search);
        }
    }

    private static bool IsUsableSavedSearch(SavedSearchSettings search) =>
        !string.IsNullOrWhiteSpace(search.QueryText);

    private void RebuildScopeItems()
    {
        _scopeItems.Clear();
        _scopeItems.Add(new SidebarScopeItem
        {
            Name = "All files",
            FileNamePattern = string.Empty,
            Glyph = "\uE8A5",
        });
        _scopeItems.Add(new SidebarScopeItem
        {
            Name = "Source and config",
            FileNamePattern = "*.cs;*.xaml;*.csproj;*.sln;*.slnx;*.json;*.md",
            Glyph = "\uE943",
        });
        _scopeItems.Add(new SidebarScopeItem
        {
            Name = "Documents",
            FileNamePattern = "*.txt;*.md;*.rtf;*.docx;*.xlsx;*.pptx",
            Glyph = "\uE8A5",
        });
        _scopeItems.Add(new SidebarScopeItem
        {
            Name = "PDFs",
            FileNamePattern = "*.pdf",
            Glyph = "\uEA90",
        });

        foreach (var scope in CustomScopes)
        {
            _scopeItems.Add(new SidebarScopeItem
            {
                Name = scope.Name,
                FileNamePattern = scope.FileNamePattern,
                Glyph = "\uE8A5",
                IsCustom = true,
                CustomScope = scope,
            });
        }

        UpdateActiveScope(_activeScopePattern);
    }

    private static string NormalizeScopePattern(string? pattern) =>
        string.Join(
            ";",
            (pattern ?? string.Empty)
                .Split(s_scopePatternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool MatchesScope(SidebarScopeItem item, string needle) =>
        Contains(item.Name, needle) || Contains(item.FileNamePattern, needle);

    private static bool MatchesSavedSearch(SavedSearchSettings item, string needle) =>
        Contains(item.QueryText, needle) ||
        Contains(item.SearchPath, needle) ||
        Contains(item.FileNamePattern, needle) ||
        Contains(item.ExcludeFileNamePattern, needle) ||
        Contains(item.SearchMode.ToString(), needle) ||
        Contains(item.SearchTarget.ToString(), needle);

    private static bool MatchesFavorite(FavoriteResultSettings item, string needle) =>
        Contains(item.Path, needle) ||
        Contains(item.DisplayName, needle) ||
        Contains(item.Folder, needle);

    private static bool MatchesWorkspace(WorkspaceSettings item, string needle) =>
        Contains(item.Name, needle) ||
        Contains(item.Search.QueryText, needle) ||
        Contains(item.Search.SearchPath, needle) ||
        Contains(item.ResultSort, needle) ||
        Contains(item.ResultGroup, needle);

    private static bool MatchesText(string item, string needle) => Contains(item, needle);

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void OnApplicationSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApplicationSettingsViewModel.SidebarPageSize))
            return;

        ScopeList.PageSize = _applicationSettings.SidebarPageSize;
        RecentPathList.PageSize = _applicationSettings.SidebarPageSize;
        SavedSearchList.PageSize = _applicationSettings.SidebarPageSize;
        FavoriteResultList.PageSize = _applicationSettings.SidebarPageSize;
        WorkspaceList.PageSize = _applicationSettings.SidebarPageSize;
    }

    private static SavedSearchSettings NormalizeSavedSearch(SavedSearchSettings search) =>
        new()
        {
            QueryText = search.QueryText?.Trim() ?? string.Empty,
            SearchPath = search.SearchPath?.Trim() ?? string.Empty,
            FileNamePattern = search.FileNamePattern?.Trim() ?? string.Empty,
            ExcludeFileNamePattern = search.ExcludeFileNamePattern?.Trim() ?? string.Empty,
            IncludeSubfolders = search.IncludeSubfolders,
            SearchMode = search.SearchMode,
            SearchTarget = Enum.IsDefined(search.SearchTarget) ? search.SearchTarget : SearchTarget.Content,
            MatchCase = search.MatchCase,
            EnableDocumentExtraction = search.EnableDocumentExtraction,
            EnableImageOcr = search.EnableImageOcr,
            SkipUnknownFileTypes = search.SkipUnknownFileTypes,
            UseIndex = search.UseIndex,
            MinSizeKB = Math.Max(0, search.MinSizeKB),
            MaxSizeKB = Math.Max(0, search.MaxSizeKB),
            ModifiedAfterEnabled = search.ModifiedAfterEnabled,
            ModifiedAfter = search.ModifiedAfter == default ? DateTime.Today.AddDays(-7) : search.ModifiedAfter,
            ModifiedBeforeEnabled = search.ModifiedBeforeEnabled,
            ModifiedBefore = search.ModifiedBefore == default ? DateTime.Today : search.ModifiedBefore,
            AdditionalPlainTextExtensions = search.AdditionalPlainTextExtensions?.Trim() ?? string.Empty,
        };

    private const int MaxFavoriteEntries = 100;

    private const int MaxWorkspaceEntries = 30;

    private static FavoriteResultSettings NormalizeFavorite(FavoriteResultSettings favorite) =>
        new()
        {
            Path = favorite.Path?.Trim() ?? string.Empty,
            AddedUtc = favorite.AddedUtc == default ? DateTime.UtcNow : favorite.AddedUtc,
        };

    private static bool IsUsableFavorite(FavoriteResultSettings favorite) =>
        !string.IsNullOrWhiteSpace(favorite.Path);

    private static WorkspaceSettings NormalizeWorkspace(WorkspaceSettings workspace) =>
        new()
        {
            Name = workspace.Name?.Trim() ?? string.Empty,
            UpdatedUtc = workspace.UpdatedUtc == default ? DateTime.UtcNow : workspace.UpdatedUtc,
            Search = NormalizeSavedSearch(workspace.Search ?? new SavedSearchSettings()),
            CustomScopes = workspace.CustomScopes
                .Select(NormalizeScope)
                .Where(IsUsableScope)
                .ToList(),
            FavoriteResults = workspace.FavoriteResults
                .Select(NormalizeFavorite)
                .Where(IsUsableFavorite)
                .ToList(),
            PinnedPaths = workspace.PinnedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            QuickSearchSelectedIndexedRoots = workspace.QuickSearchSelectedIndexedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ResultSort = workspace.ResultSort?.Trim() ?? string.Empty,
            ResultGroup = workspace.ResultGroup?.Trim() ?? string.Empty,
            RefinementQuery = workspace.RefinementQuery?.Trim() ?? string.Empty,
            RunOnLoad = workspace.RunOnLoad,
        };

    private static bool IsUsableWorkspace(WorkspaceSettings workspace) =>
        !string.IsNullOrWhiteSpace(workspace.Name);

    private static SearchScope NormalizeScope(SearchScope scope) =>
        new()
        {
            Name = scope.Name?.Trim() ?? string.Empty,
            FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
        };

    private static bool IsUsableScope(SearchScope scope) =>
        !string.IsNullOrWhiteSpace(scope.Name);

    private static List<WorkspaceSettings> ReadWorkspaceFile(string path)
    {
        var json = File.ReadAllText(path);
        var package = JsonSerializer.Deserialize<WorkspaceFile>(json, s_workspaceJsonOptions);
        if (package?.Workspaces is { Count: > 0 })
            return package.Workspaces;
        if (package?.Workspace is not null)
            return [package.Workspace];

        var workspace = JsonSerializer.Deserialize<WorkspaceSettings>(json, s_workspaceJsonOptions);
        return workspace is null ? [] : [workspace];
    }

    private static void WriteWorkspaceFile(string path, WorkspaceFile package)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        package.Format = WorkspaceFileFormat;
        package.Version = WorkspaceFileVersion;
        var json = JsonSerializer.Serialize(package, s_workspaceJsonOptions);
        File.WriteAllText(fullPath, json);
    }

    private static string BuildWorkspaceFileName(string displayName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Join(
            "-",
            displayName.Split(invalid, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "workspace";

        return $"{safe}.filesearch-workspace.json";
    }

    private const string WorkspaceFileFormat = "FileSearch.Workspace";

    private const int WorkspaceFileVersion = 1;

    private const string WorkspaceFileFilter =
        "FileSearch workspace (*.filesearch-workspace.json;*.json)|*.filesearch-workspace.json;*.json|JSON files (*.json)|*.json|All files (*.*)|*.*";

    private static readonly JsonSerializerOptions s_workspaceJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class WorkspaceFile
    {
        public string Format { get; set; } = WorkspaceFileFormat;

        public int Version { get; set; } = WorkspaceFileVersion;

        public WorkspaceSettings? Workspace { get; set; }

        public List<WorkspaceSettings> Workspaces { get; set; } = new();
    }
}
