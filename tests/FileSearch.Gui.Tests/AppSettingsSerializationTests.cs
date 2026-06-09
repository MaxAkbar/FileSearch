using System.Text.Json;
using System.Text.Json.Serialization;
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
            IndexedLocations =
            [
                new IndexedLocationSettings
                {
                    Root = @"C:\Work",
                    Recursive = false,
                    IncludeHidden = true,
                    EnableDocumentExtraction = false,
                    SkipUnknownFileTypes = true,
                    WatchEnabled = true,
                    LastIndexedUtcTicks = 638851392000000000,
                    FileCount = 42,
                    LineCount = 1_234,
                    IsIndexing = true,
                    IsQueued = true,
                    IsIndexingPaused = true,
                    QueuedWorkCount = 2,
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
        Assert.DoesNotContain(nameof(IndexedLocationSettings.RuntimeStatusSummary), json);
        Assert.NotNull(loaded);
        var location = Assert.Single(loaded.IndexedLocations);
        Assert.Equal(@"C:\Work", location.Root);
        Assert.False(location.Recursive);
        Assert.True(location.IncludeHidden);
        Assert.False(location.EnableDocumentExtraction);
        Assert.True(location.SkipUnknownFileTypes);
        Assert.True(location.WatchEnabled);
        Assert.Equal(638851392000000000, location.LastIndexedUtcTicks);
        Assert.Equal(42, location.FileCount);
        Assert.Equal(1_234, location.LineCount);
    }
}
