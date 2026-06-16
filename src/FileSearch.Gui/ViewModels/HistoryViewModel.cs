using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Recent queries/paths and saved scopes: the "pick up where you left off"
/// state. Owns its slice of the persisted settings.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private const int MaxHistoryEntries = 15;

    private readonly ISettingsService _settingsService;
    private readonly ApplicationSettingsViewModel _applicationSettings;
    private readonly StatusBarViewModel _status;
    private readonly ObservableCollection<SidebarScopeItem> _scopeItems = new();

    public HistoryViewModel(
        ISettingsService settingsService,
        ApplicationSettingsViewModel applicationSettings,
        StatusBarViewModel status)
    {
        _settingsService = settingsService;
        _applicationSettings = applicationSettings;
        _status = status;
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

        var saved = _settingsService.Current;
        foreach (var search in saved.SavedSearches.Select(NormalizeSavedSearch).Where(IsUsableSavedSearch))
            SavedSearches.Add(search);

        if (SavedSearches.Count == 0)
            LoadLegacySavedSearches(saved);

        foreach (var query in saved.RecentQueries) RecentQueries.Add(query);
        foreach (var path in saved.RecentPaths) RecentPaths.Add(path);
        foreach (var scope in saved.CustomScopes.Where(scope => !string.IsNullOrWhiteSpace(scope.Name)))
        {
            CustomScopes.Add(new SearchScope
            {
                Name = scope.Name.Trim(),
                FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
            });
        }

        SavedSearches.CollectionChanged += (_, _) => ClearSavedSearchesCommand.NotifyCanExecuteChanged();
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

    public ObservableCollection<SavedSearchSettings> SavedSearches { get; } = new();

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

    public ObservableCollection<SearchScope> CustomScopes { get; } = new();

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

    private static bool HasSameSavedSearchIdentity(SavedSearchSettings left, SavedSearchSettings right) =>
        string.Equals(left.QueryText?.Trim(), right.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.SearchPath?.Trim(), right.SearchPath?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void LoadLegacySavedSearches(AppSettings saved)
    {
        var path = saved.RecentPaths.FirstOrDefault() ?? string.Empty;
        foreach (var query in saved.RecentQueries)
        {
            var search = NormalizeSavedSearch(new SavedSearchSettings
            {
                QueryText = query,
                SearchPath = path,
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
    }

    private static bool MatchesScope(SidebarScopeItem item, string needle) =>
        Contains(item.Name, needle) || Contains(item.FileNamePattern, needle);

    private static bool MatchesSavedSearch(SavedSearchSettings item, string needle) =>
        Contains(item.QueryText, needle) ||
        Contains(item.SearchPath, needle) ||
        Contains(item.FileNamePattern, needle) ||
        Contains(item.ExcludeFileNamePattern, needle) ||
        Contains(item.SearchMode.ToString(), needle);

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
            MatchCase = search.MatchCase,
            EnableDocumentExtraction = search.EnableDocumentExtraction,
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
}
