using System.IO;
using System.Linq;
using FileSearch.Core.Workflows;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class JsonWorkflowStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonWorkflowStore _store;

    public JsonWorkflowStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "filesearch-workflows-" + Guid.NewGuid().ToString("N"));
        _store = new JsonWorkflowStore(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void SaveListLoad_RoundTrips()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "Find TODOs",
            Description = "Looks for TODO markers.",
            Steps = new WorkflowStep[]
            {
                new SearchStep { Id = "s1", Query = "TODO", Roots = new[] { @"C:\src" } },
            },
        };

        var fileName = _store.Save(workflow);

        Assert.Equal("find-todos.json", fileName);
        Assert.True(File.Exists(_store.GetFullPath(fileName)));

        var summary = Assert.Single(_store.List());
        Assert.Equal("find-todos.json", summary.FileName);
        Assert.Equal("Find TODOs", summary.Name);
        Assert.Equal("Looks for TODO markers.", summary.Description);
        Assert.Null(summary.Error);

        var loaded = _store.TryLoad(fileName, out var error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal("Find TODOs", loaded.Name);
        Assert.Equal("Looks for TODO markers.", loaded.Description);
        var step = Assert.IsType<SearchStep>(Assert.Single(loaded.Steps));
        Assert.Equal("s1", step.Id);
        Assert.Equal("TODO", step.Query);
        Assert.Equal(new[] { @"C:\src" }, step.Roots);
    }

    [Theory]
    [InlineData("Find TODOs", "find-todos.json")]
    [InlineData("UPPER case", "upper-case.json")]
    [InlineData("a/b:c*d?", "a-b-c-d.json")]
    [InlineData("  spaced  ", "spaced.json")]
    [InlineData("", "workflow.json")]
    [InlineData("   ", "workflow.json")]
    [InlineData("---", "workflow.json")]
    public void ToFileName_SlugsWorkflowName(string workflowName, string expected)
    {
        Assert.Equal(expected, JsonWorkflowStore.ToFileName(workflowName));
    }

    [Fact]
    public void Save_WithExplicitFileName_UsesIt()
    {
        var workflow = new WorkflowDefinition { Name = "Anything" };

        var fileName = _store.Save(workflow, "custom-name.json");

        Assert.Equal("custom-name.json", fileName);
        Assert.True(File.Exists(Path.Combine(_directory, "custom-name.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "anything.json")));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        _store.Save(new WorkflowDefinition { Name = "Mine", Description = "first" });
        _store.Save(new WorkflowDefinition { Name = "Mine", Description = "second" });

        var loaded = _store.TryLoad("mine.json", out var error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal("second", loaded.Description);
        Assert.Single(_store.List());
    }

    [Fact]
    public void Save_WhenMoveFails_LeavesNoTempFileBehind()
    {
        var workflow = new WorkflowDefinition { Name = "Locked" };
        var fileName = _store.Save(workflow);
        var path = _store.GetFullPath(fileName);

        // Holding the target with no sharing makes the final move fail; the
        // store must clean up its .tmp before rethrowing.
        Exception? exception;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            exception = Record.Exception(() => _store.Save(workflow));
        }

        Assert.NotNull(exception);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Save_OnSuccess_LeavesNoTempFileBehind()
    {
        _store.Save(new WorkflowDefinition { Name = "Tidy" });

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var fileName = _store.Save(new WorkflowDefinition { Name = "Doomed" });
        Assert.True(File.Exists(_store.GetFullPath(fileName)));

        _store.Delete(fileName);

        Assert.False(File.Exists(_store.GetFullPath(fileName)));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Delete_MissingFile_DoesNotThrow()
    {
        _store.Delete("never-existed.json");
    }

    [Fact]
    public void List_IncludesCorruptFileWithError()
    {
        _store.Save(new WorkflowDefinition { Name = "Good" });
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ this is not json");

        var summaries = _store.List();

        Assert.Equal(2, summaries.Count);

        var broken = summaries.Single(s => s.FileName == "broken.json");
        Assert.Equal("broken", broken.Name);
        Assert.NotNull(broken.Error);

        var good = summaries.Single(s => s.FileName == "good.json");
        Assert.Equal("Good", good.Name);
        Assert.Null(good.Error);
    }

    [Fact]
    public void List_WhenDirectoryMissing_ReturnsEmpty()
    {
        Assert.Empty(_store.List());
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNullAndError()
    {
        var loaded = _store.TryLoad("missing.json", out var error);

        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void GetFullPath_PlainName_CombinesWithDirectory()
    {
        Assert.Equal(Path.Combine(_directory, "ok.json"), _store.GetFullPath("ok.json"));
    }

    [Theory]
    [InlineData(@"..\evil.json")]
    [InlineData(@"C:\evil.json")]
    [InlineData(@"sub\file.json")]
    [InlineData("sub/file.json")]
    [InlineData(@"\evil.json")]
    public void GetFullPath_RejectsNamesThatEscapeTheLibrary(string fileName)
    {
        Assert.Throws<ArgumentException>(() => _store.GetFullPath(fileName));
    }

    [Fact]
    public void GetFullPath_BlankName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.GetFullPath("   "));
    }
}
