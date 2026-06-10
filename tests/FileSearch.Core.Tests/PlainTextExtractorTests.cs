using System.IO;
using System.Text;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class PlainTextExtractorTests : IDisposable
{
    private readonly string _dir;

    public PlainTextExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "filesearch-plaintext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ReadsUtf16LittleEndianFilesWithBom()
    {
        // PowerShell 5's Out-File default ("Unicode") — full of NUL bytes,
        // which the binary sniffer must not mistake for a binary file.
        var path = Path.Combine(_dir, "utf16le.txt");
        File.WriteAllText(path, "utf16 needle\nsecond line\n", Encoding.Unicode);

        var lines = await ExtractAsync(path);

        Assert.Equal(2, lines.Count);
        Assert.Equal("utf16 needle", lines[0].Content);
        Assert.Equal("second line", lines[1].Content);
    }

    [Fact]
    public async Task ReadsUtf16BigEndianFilesWithBom()
    {
        var path = Path.Combine(_dir, "utf16be.txt");
        File.WriteAllText(path, "utf16 needle\n", Encoding.BigEndianUnicode);

        var lines = await ExtractAsync(path);

        var line = Assert.Single(lines);
        Assert.Equal("utf16 needle", line.Content);
    }

    [Fact]
    public async Task SkipsFilesWithNulBytesAndNoTextBom()
    {
        var path = Path.Combine(_dir, "binary.txt");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x04, 0x00 });

        var lines = await ExtractAsync(path);

        Assert.Empty(lines);
    }

    private static async Task<List<TextLine>> ExtractAsync(string path)
    {
        var extractor = new PlainTextExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, TestContext.Current.CancellationToken))
            lines.Add(line);
        return lines;
    }
}
