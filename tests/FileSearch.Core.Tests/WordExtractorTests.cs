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

    [Fact]
    public async Task ExtractsEmbeddedImageOcrWhenEnabled()
    {
        CreateDocxWithImage(_path, "Visible text.");
        var ocr = new FakeEmbeddedImageOcrService();
        var extractor = new WordExtractor(ocr);

        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(
            _path,
            new TextExtractionContext(EnableOcr: true),
            CancellationToken.None))
        {
            lines.Add(line);
        }

        var hit = Assert.Single(lines, line => line.Content == "embedded ocr needle");
        Assert.NotNull(hit.Anchor);
        Assert.Equal(SourceAnchorKind.Word, hit.Anchor.Kind);
        Assert.EndsWith("media/image.png", hit.Anchor.MemberPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OCR region", hit.Anchor.DisplayText);
        Assert.True(ocr.SawImageBytes);
    }

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        mainPart.Document.Save();
    }

    private static void CreateDocxWithImage(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = imagePart.GetStream(FileMode.Create, FileAccess.Write))
            stream.Write([1, 2, 3, 4]);
        mainPart.Document.Save();
    }

    private sealed class FakeEmbeddedImageOcrService : IEmbeddedImageOcrService
    {
        public bool SawImageBytes { get; private set; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            byte[] imageBytes,
            EmbeddedImageOcrRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            SawImageBytes = imageBytes.Length > 0;
            yield return new TextLine(
                1,
                "embedded ocr needle",
                SourceAnchor.EmbeddedOcrRegion(
                    request.AnchorKind,
                    request.Label,
                    request.MemberPath,
                    1,
                    2,
                    3,
                    4,
                    10,
                    20,
                    request.Page,
                    request.Section,
                    request.Sheet));
        }
    }
}
