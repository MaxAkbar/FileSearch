using System.Text.Json;
using System.Text.Json.Serialization;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Tests;

public sealed class AppSettingsSerializationTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void IndexedLocations_RoundTripPersistentFields()
    {
        var settings = new AppSettings
        {
            UseIndex = true,
            CustomThemeFileName = "nord-dark.json",
            SidebarPageSize = 15,
            IndexerResourceProfile = IndexerResourceProfile.Low,
            KeepIndexUpdatedAfterClose = true,
            StartBackgroundIndexerAtSignIn = true,
            PauseIndexingOnBattery = true,
            IndexOnlyWhenIdle = true,
            IndexerCpuLimitPercent = 25,
            IndexerDiskPauseMilliseconds = 100,
            QuickSearchIncludeContent = false,
            QuickSearchFolderPath = @"C:\Quick",
            Shortcuts = new AppShortcutSettings
            {
                FocusQuery = AppShortcutGesture.CtrlL,
                RenameSelectedResult = AppShortcutGesture.Disabled,
            },
            SavedSearches =
            [
                new()
                {
                    QueryText = "needle",
                    SearchPath = @"C:\Work",
                    FileNamePattern = "*.cs",
                    ExcludeFileNamePattern = "*.g.cs",
                    IncludeSubfolders = false,
                    SearchMode = QueryMode.Regex,
                    MatchCase = true,
                    EnableDocumentExtraction = false,
                    SkipUnknownFileTypes = true,
                    UseIndex = true,
                    MinSizeKB = 4,
                    MaxSizeKB = 128,
                    ModifiedAfterEnabled = true,
                    ModifiedAfter = new DateTime(2026, 1, 2),
                    ModifiedBeforeEnabled = true,
                    ModifiedBefore = new DateTime(2026, 2, 3),
                    AdditionalPlainTextExtensions = ".tmpl",
                },
            ],
            FavoriteResults =
            [
                new()
                {
                    Path = @"C:\Work\notes.md",
                    AddedUtc = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
            Workspaces =
            [
                new()
                {
                    Name = "Daily",
                    UpdatedUtc = new DateTime(2026, 6, 19, 13, 0, 0, DateTimeKind.Utc),
                    Search = new SavedSearchSettings
                    {
                        QueryText = "daily",
                        SearchPath = @"C:\Daily",
                        FileNamePattern = "*.md",
                    },
                    CustomScopes =
                    [
                        new SearchScope { Name = "Markdown", FileNamePattern = "*.md" },
                    ],
                    FavoriteResults =
                    [
                        new FavoriteResultSettings { Path = @"C:\Daily\plan.md" },
                    ],
                    PinnedPaths = [@"C:\Daily\pin.md"],
                    QuickSearchSelectedIndexedRoots = [@"C:\Daily"],
                    ResultSort = "HitCount",
                    ResultGroup = "Folder",
                    RefinementQuery = "open",
                },
            ],
            IndexedLocations =
            [
                new IndexedLocationSettings
                {
                    Root = @"C:\Work",
                    Recursive = false,
                    IncludeHidden = true,
                    EnableDocumentExtraction = false,
                    SkipUnknownFileTypes = true,
                    IncludedExtensions = ".cs; md",
                    IncludedFolders = "src; tests",
                    ExcludedExtensions = ".dll; exe",
                    ExcludedFolders = "bin; obj",
                    WatchEnabled = true,
                    LastIndexedUtcTicks = 638851392000000000,
                    FileCount = 42,
                    LineCount = 1_234,
                    IsIndexing = true,
                    IsQueued = true,
                    IsIndexingPaused = true,
                    QueuedWorkCount = 2,
                    RuntimeStatusDetail = "Scanning 42",
                },
            ],
            IndexInclusionLists =
            [
                new()
                {
                    Name = "Source",
                    Extensions = ".cs; .xaml",
                    Folders = "src",
                },
            ],
            IndexExclusionLists =
            [
                new()
                {
                    Name = "Build output",
                    Extensions = ".dll; .exe",
                    Folders = "bin; obj",
                },
            ],
        };

        var json = JsonSerializer.Serialize(settings, s_options);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, s_options);

        Assert.DoesNotContain(nameof(IndexedLocationSettings.DisplayName), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.Summary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.WatchSummary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.StrategySummary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.RecursionSummary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.TypeSummary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.LastIndexedSummary), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.IsIndexing), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.IsQueued), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.IsIndexingPaused), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.QueuedWorkCount), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.RuntimeStatusDetail), json);
        Assert.DoesNotContain(nameof(IndexedLocationSettings.RuntimeStatusSummary), json);
        Assert.DoesNotContain(nameof(IndexFilterListSettings.Summary), json);
        Assert.DoesNotContain(nameof(SavedSearchSettings.DisplayName), json);
        Assert.DoesNotContain(nameof(SavedSearchSettings.Summary), json);
        Assert.DoesNotContain(nameof(FavoriteResultSettings.DisplayName), json);
        Assert.DoesNotContain($"\"{nameof(FavoriteResultSettings.Folder)}\":", json);
        Assert.DoesNotContain(nameof(WorkspaceSettings.DisplayName), json);
        Assert.DoesNotContain(nameof(WorkspaceSettings.Summary), json);
        Assert.NotNull(loaded);
        Assert.Equal("nord-dark.json", loaded.CustomThemeFileName);
        Assert.Equal(15, loaded.SidebarPageSize);
        Assert.Equal(IndexerResourceProfile.Low, loaded.IndexerResourceProfile);
        Assert.True(loaded.KeepIndexUpdatedAfterClose);
        Assert.True(loaded.StartBackgroundIndexerAtSignIn);
        Assert.True(loaded.PauseIndexingOnBattery);
        Assert.True(loaded.IndexOnlyWhenIdle);
        Assert.Equal(25, loaded.IndexerCpuLimitPercent);
        Assert.Equal(100, loaded.IndexerDiskPauseMilliseconds);
        Assert.False(loaded.QuickSearchIncludeContent);
        Assert.Equal(@"C:\Quick", loaded.QuickSearchFolderPath);
        Assert.Equal(AppShortcutGesture.CtrlL, loaded.Shortcuts.FocusQuery);
        Assert.Equal(AppShortcutGesture.Disabled, loaded.Shortcuts.RenameSelectedResult);
        Assert.Null(loaded.RunInBackground);

        var savedSearch = Assert.Single(loaded.SavedSearches);
        Assert.Equal("needle", savedSearch.QueryText);
        Assert.Equal(@"C:\Work", savedSearch.SearchPath);
        Assert.Equal("*.cs", savedSearch.FileNamePattern);
        Assert.Equal("*.g.cs", savedSearch.ExcludeFileNamePattern);
        Assert.False(savedSearch.IncludeSubfolders);
        Assert.Equal(QueryMode.Regex, savedSearch.SearchMode);
        Assert.True(savedSearch.MatchCase);
        Assert.False(savedSearch.EnableDocumentExtraction);
        Assert.True(savedSearch.SkipUnknownFileTypes);
        Assert.True(savedSearch.UseIndex);
        Assert.Equal(4, savedSearch.MinSizeKB);
        Assert.Equal(128, savedSearch.MaxSizeKB);
        Assert.True(savedSearch.ModifiedAfterEnabled);
        Assert.Equal(new DateTime(2026, 1, 2), savedSearch.ModifiedAfter);
        Assert.True(savedSearch.ModifiedBeforeEnabled);
        Assert.Equal(new DateTime(2026, 2, 3), savedSearch.ModifiedBefore);
        Assert.Equal(".tmpl", savedSearch.AdditionalPlainTextExtensions);

        var favorite = Assert.Single(loaded.FavoriteResults);
        Assert.Equal(@"C:\Work\notes.md", favorite.Path);
        Assert.Equal(new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc), favorite.AddedUtc);

        var workspace = Assert.Single(loaded.Workspaces);
        Assert.Equal("Daily", workspace.Name);
        Assert.Equal("daily", workspace.Search.QueryText);
        Assert.Equal(@"C:\Daily", workspace.Search.SearchPath);
        Assert.Equal("*.md", workspace.Search.FileNamePattern);
        Assert.Equal("Markdown", Assert.Single(workspace.CustomScopes).Name);
        Assert.Equal(@"C:\Daily\plan.md", Assert.Single(workspace.FavoriteResults).Path);
        Assert.Equal(@"C:\Daily\pin.md", Assert.Single(workspace.PinnedPaths));
        Assert.Equal(@"C:\Daily", Assert.Single(workspace.QuickSearchSelectedIndexedRoots));
        Assert.Equal("HitCount", workspace.ResultSort);
        Assert.Equal("Folder", workspace.ResultGroup);
        Assert.Equal("open", workspace.RefinementQuery);

        var location = Assert.Single(loaded.IndexedLocations);
        Assert.Equal(@"C:\Work", location.Root);
        Assert.False(location.Recursive);
        Assert.True(location.IncludeHidden);
        Assert.False(location.EnableDocumentExtraction);
        Assert.True(location.SkipUnknownFileTypes);
        Assert.Equal(".cs; md", location.IncludedExtensions);
        Assert.Equal("src; tests", location.IncludedFolders);
        Assert.Equal(".dll; exe", location.ExcludedExtensions);
        Assert.Equal("bin; obj", location.ExcludedFolders);
        Assert.True(location.WatchEnabled);
        Assert.Equal(638851392000000000, location.LastIndexedUtcTicks);
        Assert.Equal(42, location.FileCount);
        Assert.Equal(1_234, location.LineCount);

        var includeList = Assert.Single(loaded.IndexInclusionLists);
        Assert.Equal("Source", includeList.Name);
        Assert.Equal(".cs; .xaml", includeList.Extensions);
        Assert.Equal("src", includeList.Folders);

        var excludeList = Assert.Single(loaded.IndexExclusionLists);
        Assert.Equal("Build output", excludeList.Name);
        Assert.Equal(".dll; .exe", excludeList.Extensions);
        Assert.Equal("bin; obj", excludeList.Folders);
    }

    [Fact]
    public void LegacyRunInBackgroundMigratesToSplitBackgroundSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """
            {
              "RunInBackground": true
            }
            """,
            s_options);

        Assert.NotNull(settings);
        JsonSettingsStore.MigrateLegacyFields(settings);

        Assert.True(settings.KeepIndexUpdatedAfterClose);
        Assert.True(settings.StartBackgroundIndexerAtSignIn);
        Assert.Null(settings.RunInBackground);
    }
}
