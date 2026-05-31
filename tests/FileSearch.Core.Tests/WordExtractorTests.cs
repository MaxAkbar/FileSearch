using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WordExtractorTests : IDisposable
{
    private readonly string _path;

    public WordExtractorTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".docx");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void SupportedExtensions_IncludesDocx()
    {
        var extractor = new WordExtractor();
        Assert.Contains(".docx", extractor.SupportedExtensions);
    }

    [Fact]
    public async Task Extracts_ParagraphTexts()
    {
        CreateDocx(_path, "First paragraph.", "Second paragraph.", "Third has needle in it.");

        var extractor = new WordExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);

        Assert.Equal(3, lines.Count);
        Assert.Equal("First paragraph.", lines[0].Content);
        Assert.Contains("needle", lines[2].Content);
    }

    [Fact]
    public async Task EmptyParagraphs_AreSkipped()
    {
        CreateDocx(_path, "First.", "", "Third.");

        var extractor = new WordExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(_path, CancellationToken.None))
            lines.Add(line);

        Assert.Equal(2, lines.Count);
    }

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        mainPart.Document.Save();
    }
}
