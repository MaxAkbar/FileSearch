using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Tests;

public sealed class JsonFileTypeOptionsStoreTests : IDisposable
{
    private readonly string _directory;

    public JsonFileTypeOptionsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "filesearch-filetypes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Load_CreatesDefaultFile_WhenMissing()
    {
        var path = Path.Combine(_directory, "file-types.json");
        var store = new JsonFileTypeOptionsStore(path);

        var options = store.Load();

        Assert.True(File.Exists(path));
        Assert.Contains(".pdf", options.DocumentExtensions);
        Assert.Empty(options.AdditionalPlainTextExtensions);
    }

    [Fact]
    public void Save_AndLoad_NormalizesExtensions()
    {
        var path = Path.Combine(_directory, "file-types.json");
        var store = new JsonFileTypeOptionsStore(path);

        store.Save(new FileTypeOptions
        {
            DocumentExtensions = new List<string> { "*.PDF", "docx", ".pdf" },
            AdditionalPlainTextExtensions = new List<string> { "liquid; *.tmpl", ".FOO", "foo" },
        });

        var options = store.Load();

        Assert.Equal(new[] { ".docx", ".pdf" }, options.DocumentExtensions);
        Assert.Equal(new[] { ".foo", ".liquid", ".tmpl" }, options.AdditionalPlainTextExtensions);
    }
}
