using System.ComponentModel;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class FileLauncherTests : IDisposable
{
    [Fact]
    public async Task ArchiveHitOpensTheReadOnlyMemberViewerWithoutLaunchingExtractedContent()
    {
        string path = Path.Combine(_root, "documents.zip");
        File.WriteAllText(path, "fixture");
        var hit = new Hit(path, 901, "needle", [], Locator: new SourceLocator(ArchiveMember: "docs/report.pdf", Page: 2));
        string? opened = null;
        Hit? preferred = null;
        var launcher = new FileLauncher(_ => Assert.Fail("Archive hit must not launch an external app."),
            (file, source) => { opened = file; preferred = source; }, _ => Assert.Fail("Unexpected open error."));
        Assert.True(await launcher.OpenAtLocationAsync(path, hit, TestContext.Current.CancellationToken));
        Assert.Equal(path, opened);
        Assert.Same(hit, preferred);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.OpenTests", Guid.NewGuid().ToString("N"));
    public FileLauncherTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("sample.db")]
    [InlineData("sample.sqlite3")]
    [InlineData("sample.csdb")]
    public async Task FilenameSearchOpenUsesBuiltInDatabasePreviewWithoutAFileAssociation(string name)
    {
        string path = Path.Combine(_root, name); File.WriteAllText(path, "fixture");
        string? opened = null; Hit? preferred = null;
        var launcher = new FileLauncher(_ => throw new Win32Exception(1155), (file, hit) => { opened = file; preferred = hit; }, _ => Assert.Fail("Database open must not require a Windows file association."));
        var result = new FileResultViewModel(path, launcher);
        result.AddHit(new Hit(path, 0, name, [], Kind: HitKind.Metadata));
        await result.OpenCommand.ExecuteAsync(null);
        Assert.Equal(path, opened);
        Assert.Null(preferred);
    }

    [Fact]
    public async Task DatabasePreviewLineOpenRetainsTheExactRecordLocation()
    {
        string path = Path.Combine(_root, "sample.db"); File.WriteAllText(path, "fixture");
        var location = new DatabaseLocation(DatabaseKind.Sqlite, "Records", 275, "Notes", "rowid=275", "fingerprint");
        var hit = new Hit(path, 825, "needle", [], Anchor: new SourceAnchor(SourceAnchorKind.Database, "Records row 275", Database: location));
        Hit? preferred = null;
        var launcher = new FileLauncher(_ => throw new Win32Exception(1155), (_, selected) => preferred = selected, _ => Assert.Fail("Database opening should use its built-in viewer."));
        var result = new FileResultViewModel(path, launcher); result.AddHit(hit);
        await result.OpenAtLineAsync(825);
        Assert.Same(hit, preferred);
        Assert.Equal(location, preferred!.Database);
    }

    [Fact]
    public void DatabaseWithNoHitsAlsoOpensItsBuiltInViewer()
    {
        string path = Path.Combine(_root, "sample.db"); File.WriteAllText(path, "fixture");
        bool opened = false;
        var launcher = new FileLauncher(_ => throw new Win32Exception(1155), (_, hit) => { opened = true; Assert.Null(hit); }, _ => Assert.Fail("Unexpected error."));
        launcher.Open(path);
        Assert.True(opened);
    }

    [Theory]
    [InlineData(1155)]
    [InlineData(5)]
    public async Task ExternalOpenFailureShowsAnActionableMessageWithoutFaultingTheCommand(int error)
    {
        string path = Path.Combine(_root, "file.filesearchunassociated"); File.WriteAllText(path, "fixture");
        string? message = null;
        var launcher = new FileLauncher(_ => throw new Win32Exception(error), (_, _) => Assert.Fail("This is not a database."), text => message = text);
        var result = new FileResultViewModel(path, launcher);
        result.AddHit(new Hit(path, 0, "fixture", [], Kind: HitKind.Metadata));
        await result.OpenCommand.ExecuteAsync(null);
        Assert.NotEmpty(message!);
        Assert.Contains(error == 1155 ? "default app" : "could not open", message, StringComparison.Ordinal);
    }
}
