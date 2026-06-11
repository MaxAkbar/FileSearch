using System;
using System.Collections.ObjectModel;
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
    private readonly StatusBarViewModel _status;

    public HistoryViewModel(ISettingsService settingsService, StatusBarViewModel status)
    {
        _settingsService = settingsService;
        _status = status;

        var saved = _settingsService.Current;
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

        RecentQueries.CollectionChanged += (_, _) => ClearRecentQueriesCommand.NotifyCanExecuteChanged();
        RecentPaths.CollectionChanged += (_, _) => ClearRecentPathsCommand.NotifyCanExecuteChanged();
        CustomScopes.CollectionChanged += (_, _) => ClearCustomScopesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Dropdown for the "Containing text" field (most-recent first).</summary>
    public ObservableCollection<string> RecentQueries { get; } = new();

    /// <summary>Dropdown for the "Look in" field (most-recent first).</summary>
    public ObservableCollection<string> RecentPaths { get; } = new();

    public ObservableCollection<SearchScope> CustomScopes { get; } = new();

    /// <summary>Promotes the attempt into both dropdowns and persists.</summary>
    public void RecordSearch(string query, string path)
    {
        SearchHistory.PromoteToFront(RecentQueries, query, MaxHistoryEntries);
        SearchHistory.PromoteToFront(RecentPaths, path, MaxHistoryEntries);

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
        SaveHistory();
    }

    [RelayCommand(CanExecute = nameof(CanClearRecentQueries))]
    private void ClearRecentQueries()
    {
        RecentQueries.Clear();
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
}
