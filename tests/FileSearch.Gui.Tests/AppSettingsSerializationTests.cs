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
            SidebarPageSize = 15,
            IndexerResourceProfile = IndexerResourceProfile.Low,
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
        Assert.NotNull(loaded);
        Assert.Equal(15, loaded.SidebarPageSize);
        Assert.Equal(IndexerResourceProfile.Low, loaded.IndexerResourceProfile);

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
}
