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

        var lines = await ReadAllAsync(new ZipExtractor());

        Assert.Contains(lines, l => l.Content.Contains("=== readme.txt ==="));
        Assert.Contains(lines, l => l.Content == "hello world");
        Assert.Contains(lines, l => l.Content.Contains("=== code.cs ==="));
        Assert.Contains(lines, l => l.Content.Contains("public class Foo"));
        Assert.DoesNotContain(lines, l => l.Content.Contains("=== data.bin"));
    }

    [Fact]
    public async Task SkipsEntriesWhoseHeaderExceedsPerEntryCap()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "big.txt", new string('a', 4096) + "\nbig needle\n");
            AddEntry(archive, "small.txt", "small needle\n");
        }

        var extractor = new ZipExtractor(maxEntryBytes: 1024, maxTotalBytes: 1024 * 1024, maxEntries: 100);
        var lines = await ReadAllAsync(extractor);

        Assert.DoesNotContain(lines, l => l.Content.Contains("big needle"));
        Assert.Contains(lines, l => l.Content == "small needle");
    }

    [Fact]
    public async Task StopsWhenArchiveDecompressionBudgetIsExhausted()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "first.txt", new string('a', 1100) + "\n");
            AddEntry(archive, "second.txt", "late needle\n");
        }

        // first.txt passes the per-entry cap but eats the whole archive
        // budget mid-entry; second.txt must never be scanned.
        var extractor = new ZipExtractor(maxEntryBytes: 2048, maxTotalBytes: 1000, maxEntries: 100);
        var lines = await ReadAllAsync(extractor);

        Assert.Contains(lines, l => l.Content.Contains("=== first.txt ==="));
        Assert.DoesNotContain(lines, l => l.Content.Contains("late needle"));
    }

    [Fact]
    public async Task StopsAfterMaxEntries()
    {
        using (var archive = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "one.txt", "first needle\n");
            AddEntry(archive, "two.txt", "second needle\n");
            AddEntry(archive, "three.txt", "third needle\n");
        }

        var extractor = new ZipExtractor(maxEntryBytes: 1024, maxTotalBytes: 1024 * 1024, maxEntries: 2);
        var lines = await ReadAllAsync(extractor);

        Assert.Contains(lines, l => l.Content == "first needle");
        Assert.Contains(lines, l => l.Content == "second needle");
        Assert.DoesNotContain(lines, l => l.Content.Contains("third"));
    }

    private async Task<List<TextLine>> ReadAllAsync(ZipExtractor extractor)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);
        return lines;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
