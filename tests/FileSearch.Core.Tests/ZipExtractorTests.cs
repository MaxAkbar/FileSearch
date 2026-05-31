using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ZipExtractorTests : IDisposable
{
    private readonly string _path;

    public ZipExtractorTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void SupportedExtensions_IncludesZip()
    {
        var extractor = new ZipExtractor();
        Assert.Contains(".zip", extractor.SupportedExtensions);
    }

    [Fact]
    public async Task Extracts_TextEntries_AndSkipsBinary()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "readme.txt", "hello world\nsecond line\n");
            AddEntry(archive, "data.bin", "binary-extension-skipped");
            AddEntry(archive, "code.cs", "public class Foo { }\n");
        }

        var extractor = new ZipExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);

        Assert.Contains(lines, l => l.Content.Contains("=== readme.txt ==="));
        Assert.Contains(lines, l => l.Content == "hello world");
        Assert.Contains(lines, l => l.Content.Contains("=== code.cs ==="));
        Assert.Contains(lines, l => l.Content.Contains("public class Foo"));
        Assert.DoesNotContain(lines, l => l.Content.Contains("=== data.bin"));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
