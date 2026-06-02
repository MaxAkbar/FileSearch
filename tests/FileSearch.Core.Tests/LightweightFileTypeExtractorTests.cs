using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class LightweightFileTypeExtractorTests : IDisposable
{
    private readonly string _directory;

    public LightweightFileTypeExtractorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task XmlTextExtractor_ExtractsSvgXamlAndResxText()
    {
        var path = Path.Combine(_directory, "image.svg");
        await File.WriteAllTextAsync(path, "<svg><title>Diagram title</title><text>needle &amp; label</text></svg>");

        var lines = await ReadAllAsync(new XmlTextExtractor(), path);
        var extractor = new XmlTextExtractor();

        Assert.Contains(".svg", extractor.SupportedExtensions);
        Assert.Contains(".xaml", extractor.SupportedExtensions);
        Assert.Contains(".resx", extractor.SupportedExtensions);
        Assert.Equal("Diagram title needle & label", lines.Single().Content);
    }

    [Fact]
    public async Task CalendarContactExtractor_ExtractsIcsValuesAndUnfoldsLines()
    {
        var path = Path.Combine(_directory, "event.ics");
        await File.WriteAllTextAsync(
            path,
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nSUMMARY:Planning needle\r\nDESCRIPTION:First line\\nsecond line with\r\n continuation\r\nEND:VCALENDAR\r\n");

        var lines = await ReadAllAsync(new CalendarContactExtractor(), path);
        var extractor = new CalendarContactExtractor();

        Assert.Contains(".ics", extractor.SupportedExtensions);
        Assert.Contains(lines, line => line.Content == "Planning needle");
        Assert.Contains(lines, line => line.Content == "First line second line withcontinuation");
    }

    [Fact]
    public async Task CalendarContactExtractor_ExtractsVcfValues()
    {
        var path = Path.Combine(_directory, "contact.vcf");
        await File.WriteAllTextAsync(
            path,
            "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Jane Needle\r\nEMAIL:jane@example.com\r\nNOTE:Uses escaped\\, comma\r\nEND:VCARD\r\n");

        var lines = await ReadAllAsync(new CalendarContactExtractor(), path);

        Assert.Contains(lines, line => line.Content == "Jane Needle");
        Assert.Contains(lines, line => line.Content == "jane@example.com");
        Assert.Contains(lines, line => line.Content == "Uses escaped, comma");
    }

    [Fact]
    public void PlainTextExtractor_IncludesAdditionalDeveloperAndDataFormats()
    {
        var extensions = new PlainTextExtractor().SupportedExtensions;

        Assert.Contains(".jsonl", extensions);
        Assert.Contains(".ndjson", extensions);
        Assert.Contains(".slnx", extensions);
        Assert.Contains(".csproj", extensions);
        Assert.Contains(".http", extensions);
        Assert.Contains(".tf", extensions);
        Assert.Contains(".bicep", extensions);
        Assert.Contains(".dockerfile", extensions);
        Assert.Contains(".cshtml", extensions);
        Assert.Contains(".asp", extensions);
        Assert.Contains(".aspx", extensions);
        Assert.Contains(".razor", extensions);
        Assert.Contains(".vue", extensions);
        Assert.Contains(".svelte", extensions);
        Assert.Contains(".lua", extensions);
        Assert.Contains(".dart", extensions);
    }

    private static async Task<List<TextLine>> ReadAllAsync(ITextExtractor extractor, string path)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, CancellationToken.None))
            lines.Add(line);
        return lines;
    }
}
