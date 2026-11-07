using FileSearch.Core.Databases;
using FileSearch.Core.Extractors;
using FileSearch.Core.Tests;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class DatabasePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseGuiTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "sample.sqlite3")]
    [InlineData(DatabaseKind.CSharpDb, "sample.csdb")]
    public async Task FilenameOnlyDatabaseResultsSelectNativeTableViewer(DatabaseKind kind, string name)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, name, kind, 1, TestContext.Current.CancellationToken);
        var preview = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Database, preview.Kind);
        Assert.Contains("Read-only", preview.Summary);
    }

    [Fact]
    public async Task MatchesPreviewShowsDatabaseLocationAndCacheInvalidatesWhenSourceChanges()
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "sample.sqlite", DatabaseKind.Sqlite, 2, TestContext.Current.CancellationToken);
        var extractor = new DatabaseExtractor();
        var lines = new List<TextLine>();
        await foreach (var extracted in extractor.ExtractAsync(path, TestContext.Current.CancellationToken)) lines.Add(extracted);
        var line = Assert.Single(lines, line => line.Content.Contains("needle", StringComparison.Ordinal));
        var service = new FilePreviewService(new ExtractorRegistry([extractor]));
        string text = await service.LoadHitsPreviewAsync(path, [line.Number], 0, TestContext.Current.CancellationToken);
        Assert.Contains(DatabaseTestFiles.TableName, text);
        Assert.Contains("row 2", text);
        Assert.Contains("Notes", text);
        await File.WriteAllTextAsync(path + "-journal", "unfinished journal", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadHitsPreviewAsync(path, [line.Number], 0, TestContext.Current.CancellationToken));
    }
}
