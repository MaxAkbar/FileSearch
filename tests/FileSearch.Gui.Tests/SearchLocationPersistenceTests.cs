using System.Text.Json;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class SearchLocationPersistenceTests
{
    [Fact]
    public void LegacyDefinitionsRemainSelectedFolderSearches()
    {
        var saved = JsonSerializer.Deserialize<SavedSearchSettings>("""
            {"QueryText":"needle","SearchPath":"C:\\legacy"}
            """)!;
        var settings = new FakeSettingsService();
        settings.Current.SavedSearches.Add(saved);
        var history = CreateHistory(settings);
        var restored = Assert.Single(history.SavedSearches);

        Assert.Equal(LocationScopeKind.SelectedFolder, restored.LocationScope);
        Assert.False(restored.UseQuickSearchMatching);
        Assert.Empty(restored.SelectedIndexedRoots);
        Assert.Equal(@"C:\legacy", restored.SearchPath);
        Assert.StartsWith(@"C:\legacy |", restored.Summary);
    }

    [Fact]
    public void UndefinedScopeAndNullRootsUseSafeFolderDefaults()
    {
        var search = JsonSerializer.Deserialize<SavedSearchSettings>("""
            {"QueryText":"needle","SearchPath":" C:\\chosen ","LocationScope":999,"SelectedIndexedRoots":null}
            """)!;
        var history = CreateHistory(new FakeSettingsService());
        history.RecordSearch(search);
        var restored = Assert.Single(history.SavedSearches);

        Assert.Equal(LocationScopeKind.SelectedFolder, restored.LocationScope);
        Assert.Empty(restored.SelectedIndexedRoots);
        Assert.Equal(@"C:\chosen", restored.SearchPath);
    }

    [Fact]
    public void MissingAndRemovedSelectedRootsRemainExplicitAndOwnedByTheSavedDefinition()
    {
        var settings = new FakeSettingsService();
        var history = CreateHistory(settings);
        var source = new SavedSearchSettings
        {
            QueryText = "needle",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@" C:\missing\ ", @"c:\MISSING", @"\\offline\share\docs", " ", null!],
        };
        history.RecordSearch(source);
        source.SelectedIndexedRoots.Clear();
        var restored = Assert.Single(history.SavedSearches);

        Assert.Equal([@"C:\missing", @"\\offline\share\docs"], restored.SelectedIndexedRoots);
        Assert.Equal(restored.SelectedIndexedRoots, Assert.Single(settings.Current.SavedSearches).SelectedIndexedRoots);
        Assert.NotSame(restored.SelectedIndexedRoots, Assert.Single(settings.Current.SavedSearches).SelectedIndexedRoots);
        Assert.Equal("2 selected indexed locations", restored.LocationSummary);
        Assert.Empty(history.RecentPaths);
        Assert.Empty(settings.Current.IndexedLocations);
    }

    [Fact]
    public void ScopeKindsAndSelectedRootSetsHaveDistinctHistoryIdentities()
    {
        var history = CreateHistory(new FakeSettingsService());
        history.RecordSearch(new() { QueryText = "needle", SearchPath = @"C:\one" });
        history.RecordSearch(new() { QueryText = "needle", LocationScope = LocationScopeKind.AllIndexedLocations });
        history.RecordSearch(new()
        {
            QueryText = "needle",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"C:\one"],
        });
        history.RecordSearch(new()
        {
            QueryText = "needle",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"C:\two"],
        });
        history.RecordSearch(new() { QueryText = "needle", LocationScope = LocationScopeKind.SelectedIndexedLocations });

        Assert.Equal(5, history.SavedSearches.Count);
        Assert.Equal("No indexed locations selected", history.SavedSearches[0].LocationSummary);
        Assert.Equal([@"C:\one"], history.RecentPaths);
        Assert.Single(history.RecentQueries);
    }

    [Fact]
    public void SelectedRootOrderCasingAndUnusedFolderDoNotDuplicateTheSameSearch()
    {
        var history = CreateHistory(new FakeSettingsService());
        history.RecordSearch(new()
        {
            QueryText = "needle",
            SearchPath = @"C:\unused-old",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"C:\One\", @"C:\Two"],
        });
        history.RecordSearch(new()
        {
            QueryText = "NEEDLE",
            SearchPath = @"C:\unused-new",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"c:\two", @"c:\one"],
            MatchCase = true,
        });

        var saved = Assert.Single(history.SavedSearches);
        Assert.True(saved.MatchCase);
        Assert.Equal(@"C:\unused-new", saved.SearchPath);
        Assert.Equal([@"c:\two", @"c:\one"], saved.SelectedIndexedRoots);
        Assert.Empty(history.RecentPaths);
    }

    [Fact]
    public void AllIndexedIdentityIgnoresRememberedFolderAndDormantSelectedRoots()
    {
        var history = CreateHistory(new FakeSettingsService());
        history.RecordSearch(new()
        {
            QueryText = "needle",
            SearchPath = @"C:\first",
            LocationScope = LocationScopeKind.AllIndexedLocations,
            SelectedIndexedRoots = [@"C:\one"],
        });
        history.RecordSearch(new()
        {
            QueryText = "needle",
            SearchPath = @"C:\second",
            LocationScope = LocationScopeKind.AllIndexedLocations,
            SelectedIndexedRoots = [@"C:\two"],
        });

        var saved = Assert.Single(history.SavedSearches);
        Assert.Equal("All indexed locations", saved.LocationSummary);
        Assert.Contains("All indexed locations", saved.Summary);
        Assert.Equal([@"C:\two"], saved.SelectedIndexedRoots);
        Assert.Empty(history.RecentPaths);
    }

    [Fact]
    public void QuickMatchingProfileHasItsOwnSavedIdentityAndSummary()
    {
        var settings = new FakeSettingsService();
        var history = CreateHistory(settings);
        history.RecordSearch(new() { QueryText = "john smith", SearchPath = @"C:\docs" });
        history.RecordSearch(new() { QueryText = "john smith", SearchPath = @"C:\docs", UseQuickSearchMatching = true });

        Assert.Equal(2, history.SavedSearches.Count);
        Assert.True(history.SavedSearches[0].UseQuickSearchMatching);
        Assert.Contains("Quick Search matching", history.SavedSearches[0].Summary);
        Assert.False(history.SavedSearches[1].UseQuickSearchMatching);
        Assert.True(settings.Current.SavedSearches[0].UseQuickSearchMatching);
    }

    [Theory]
    [InlineData(LocationScopeKind.SelectedIndexedLocations)]
    [InlineData(LocationScopeKind.AllIndexedLocations)]
    public void WorkspaceExportImportRetainsSearchScopeSeparatelyFromQuickPreferences(LocationScopeKind scope)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.filesearch-workspace.json");
        try
        {
            var settings = new FakeSettingsService();
            var exporter = CreateHistory(settings, savePath: path);
            exporter.SaveWorkspace(new()
            {
                Name = "Review",
                Search = new SavedSearchSettings
                {
                    QueryText = "needle",
                    LocationScope = scope,
                    UseQuickSearchMatching = true,
                    SelectedIndexedRoots = [@"C:\missing", @"\\offline\share\docs"],
                },
                QuickSearchSelectedIndexedRoots = [@"C:\quick-only"],
                RunOnLoad = true,
            });
            var exported = Assert.Single(exporter.Workspaces);
            exporter.ExportWorkspaceCommand.Execute(exported);
            Assert.Contains(scope.ToString(), File.ReadAllText(path));

            var importedSettings = new FakeSettingsService();
            var importer = CreateHistory(importedSettings, openPath: path);
            importer.ImportWorkspaceCommand.Execute(null);
            var imported = Assert.Single(importer.Workspaces);
            Assert.Equal(scope, imported.Search.LocationScope);
            Assert.True(imported.Search.UseQuickSearchMatching);
            Assert.Equal([@"C:\missing", @"\\offline\share\docs"], imported.Search.SelectedIndexedRoots);
            Assert.Equal([@"C:\quick-only"], imported.QuickSearchSelectedIndexedRoots);
            Assert.True(imported.RunOnLoad);
            Assert.Contains(imported.Search.LocationSummary, imported.Summary);
            Assert.Contains("Quick Search matching", imported.Summary);
            Assert.Empty(importedSettings.Current.IndexedLocations);
            Assert.Equal(scope, Assert.Single(importedSettings.Current.Workspaces).Search.LocationScope);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReplacementFolderRemappingUpdatesSavedAndWorkspaceSelectedRoots()
    {
        var history = CreateHistory(new FakeSettingsService());
        var search = new SavedSearchSettings
        {
            QueryText = "needle",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"C:\old", @"C:\old\docs", @"C:\older"],
        };
        history.RecordSearch(search);
        history.SaveWorkspace(new() { Name = "Review", Search = search });
        history.RemapReplacementPaths(@"C:\old", @"C:\renamed", directory: true);

        string[] expected = [@"C:\renamed", @"C:\renamed\docs", @"C:\older"];
        Assert.Equal(expected, Assert.Single(history.SavedSearches).SelectedIndexedRoots);
        Assert.Equal(expected, Assert.Single(history.Workspaces).Search.SelectedIndexedRoots);
    }

    [Fact]
    public void SidebarSearchFindsLocationScopeLabelsAndExplicitMissingRoots()
    {
        var history = CreateHistory(new FakeSettingsService());
        history.RecordSearch(new() { QueryText = "needle", LocationScope = LocationScopeKind.AllIndexedLocations });
        history.RecordSearch(new()
        {
            QueryText = "other",
            LocationScope = LocationScopeKind.SelectedIndexedLocations,
            SelectedIndexedRoots = [@"\\offline\share\docs"],
        });
        history.SaveWorkspace(new() { Name = "Review", Search = history.SavedSearches[0] });
        history.SavedSearchList.SearchText = "All indexed";
        Assert.Equal("needle", Assert.Single(history.SavedSearchList.Items).QueryText);
        history.SavedSearchList.SearchText = "offline";
        Assert.Equal("other", Assert.Single(history.SavedSearchList.Items).QueryText);
        history.WorkspaceList.SearchText = "offline";
        Assert.Single(history.WorkspaceList.Items);
    }

    private static HistoryViewModel CreateHistory(FakeSettingsService settings, string? openPath = null, string? savePath = null)
    {
        var status = new StatusBarViewModel();
        return new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status,
            fileOpenPicker: openPath is null ? null : new FakeFileOpenPicker { PathToReturn = openPath },
            fileSavePicker: savePath is null ? null : new FakeFileSavePicker { PathToReturn = savePath });
    }
}
