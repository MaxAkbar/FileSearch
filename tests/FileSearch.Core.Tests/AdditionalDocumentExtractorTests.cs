using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using FileSearch.Core.Extractors;
using A = DocumentFormat.OpenXml.Drawing;

namespace FileSearch.Core.Tests;

public sealed class AdditionalDocumentExtractorTests : IDisposable
{
    private readonly string _directory;

    public AdditionalDocumentExtractorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task PowerPointExtractor_ExtractsSlideText()
    {
        var path = Path.Combine(_directory, "deck.pptx");
        CreatePptx(path, "Slide title", "Slide body has needle");

        var lines = await ReadAllAsync(new PowerPointExtractor(), path);

        Assert.Contains(".pptx", new PowerPointExtractor().SupportedExtensions);
        Assert.Contains(lines, line => line.Content.Contains("Slide title"));
        Assert.Contains(lines, line => line.Content.Contains("needle"));
    }

    [Fact]
    public async Task OpenDocumentExtractor_ExtractsContentXmlText()
    {
        var path = Path.Combine(_directory, "document.odt");
        CreateZip(path, ("content.xml", "<office:document><text:p>Open document needle text</text:p></office:document>"));

        var lines = await ReadAllAsync(new OpenDocumentExtractor(), path);

        Assert.Contains(".odt", new OpenDocumentExtractor().SupportedExtensions);
        Assert.Contains("Open document needle text", lines.Single().Content);
    }

    [Fact]
    public async Task OpenDocumentExtractor_ExtractsImageOcrWhenEnabled()
    {
        var path = Path.Combine(_directory, "image-document.odt");
        CreateZipBytes(
            path,
            ("content.xml", System.Text.Encoding.UTF8.GetBytes("<office:document><text:p>body</text:p></office:document>")),
            ("Pictures/scan.png", new byte[] { 1, 2, 3, 4 }));

        var ocr = new FakeEmbeddedImageOcrService("open document image ocr needle");
        var lines = await ReadAllAsync(new OpenDocumentExtractor(ocr), path, new TextExtractionContext(EnableOcr: true));

        var hit = Assert.Single(lines, line => line.Content == "open document image ocr needle");
        Assert.NotNull(hit.Anchor);
        Assert.Equal(SourceAnchorKind.OpenDocument, hit.Anchor.Kind);
        Assert.Equal("Pictures/scan.png", hit.Anchor.MemberPath);
        Assert.True(ocr.SawImageBytes);
    }

    [Fact]
    public async Task EpubExtractor_ExtractsVisibleChapterText()
    {
        var path = Path.Combine(_directory, "book.epub");
        CreateZip(
            path,
            ("META-INF/container.xml", "<container />"),
            ("OEBPS/chapter1.xhtml", "<html><body><h1>Chapter</h1><p>EPUB needle text</p></body></html>"));

        var lines = await ReadAllAsync(new EpubExtractor(), path);

        Assert.Contains(".epub", new EpubExtractor().SupportedExtensions);
        Assert.Contains("EPUB needle text", lines.Single().Content);
        Assert.DoesNotContain(lines, line => line.Content.Contains("container"));
    }

    [Fact]
    public async Task EpubExtractor_ExtractsImageOcrWhenEnabled()
    {
        var path = Path.Combine(_directory, "image-book.epub");
        CreateZipBytes(
            path,
            ("OEBPS/chapter1.xhtml", System.Text.Encoding.UTF8.GetBytes("<html><body><p>body</p></body></html>")),
            ("OEBPS/images/figure.png", new byte[] { 5, 6, 7, 8 }));

        var ocr = new FakeEmbeddedImageOcrService("epub image ocr needle");
        var lines = await ReadAllAsync(new EpubExtractor(ocr), path, new TextExtractionContext(EnableOcr: true));

        var hit = Assert.Single(lines, line => line.Content == "epub image ocr needle");
        Assert.NotNull(hit.Anchor);
        Assert.Equal(SourceAnchorKind.Epub, hit.Anchor.Kind);
        Assert.Equal("OEBPS/images/figure.png", hit.Anchor.MemberPath);
        Assert.True(ocr.SawImageBytes);
    }

    [Fact]
    public async Task RtfExtractor_StripsControlWords()
    {
        var path = Path.Combine(_directory, "note.rtf");
        await File.WriteAllTextAsync(path, @"{\rtf1\ansi This is \b bold\b0  needle text.}", TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new RtfExtractor(), path);

        Assert.Contains(".rtf", new RtfExtractor().SupportedExtensions);
        Assert.Equal("This is bold needle text.", lines.Single().Content);
    }

    [Fact]
    public async Task HtmlExtractor_ExtractsVisibleTextOnly()
    {
        var path = Path.Combine(_directory, "page.html");
        await File.WriteAllTextAsync(path, "<html><head><style>.x{}</style><script>hidden()</script></head><body><h1>Visible</h1><p>needle &amp; text</p></body></html>", TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new HtmlExtractor(), path);

        Assert.Contains(".html", new HtmlExtractor().SupportedExtensions);
        Assert.Equal("Visible needle & text", lines.Single().Content);
    }

    [Fact]
    public async Task EmlExtractor_ExtractsHeadersAndPlainTextBody()
    {
        var path = Path.Combine(_directory, "message.eml");
        await File.WriteAllTextAsync(
            path,
            "Subject: Test needle\r\nFrom: sender@example.com\r\nContent-Type: text/plain\r\n\r\nBody has needle text.",
            TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new EmlExtractor(), path);

        Assert.Contains(".eml", new EmlExtractor().SupportedExtensions);
        Assert.Contains(lines, line => line.Content == "Subject: Test needle");
        Assert.Contains(lines, line => line.Content == "Body has needle text.");
    }

    [Fact]
    public async Task EmlExtractor_DecodesQuotedPrintableUtf8Body()
    {
        var path = Path.Combine(_directory, "qp.eml");
        await File.WriteAllTextAsync(
            path,
            "Subject: QP test\r\nContent-Type: text/plain; charset=\"utf-8\"\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nna=C3=AFve needle caf=C3=A9",
            TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new EmlExtractor(), path);

        // The old char-per-byte decoder produced "naÃ¯ve" mojibake.
        Assert.Contains(lines, line => line.Content == "naïve needle café");
    }

    [Fact]
    public async Task EmlExtractor_SplitsBodyIntoSeparateLines()
    {
        var path = Path.Combine(_directory, "multiline.eml");
        await File.WriteAllTextAsync(
            path,
            "Subject: Lines\r\n\r\nfirst body line\r\nsecond body line",
            TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new EmlExtractor(), path);

        Assert.Contains(lines, line => line.Content == "first body line");
        Assert.Contains(lines, line => line.Content == "second body line");
    }

    [Fact]
    public async Task EmlExtractor_ExtractsImageAttachmentOcrWhenEnabled()
    {
        var path = Path.Combine(_directory, "image-message.eml");
        var imageBytes = Convert.ToBase64String([1, 2, 3, 4]);
        var content = string.Join(
            "\r\n",
            "Subject: Image",
            "Content-Type: multipart/mixed; boundary=\"abc\"",
            "",
            "--abc",
            "Content-Type: text/plain",
            "",
            "body",
            "--abc",
            "Content-Type: image/png; name=\"scan.png\"",
            "Content-Disposition: attachment; filename=\"scan.png\"",
            "Content-Transfer-Encoding: base64",
            "",
            imageBytes,
            "--abc--",
            "");
        await File.WriteAllTextAsync(
            path,
            content,
            TestContext.Current.CancellationToken);

        var ocr = new FakeEmbeddedImageOcrService("email image ocr needle");
        var extractor = new EmlExtractor(ocr);
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(
            path,
            new TextExtractionContext(EnableOcr: true),
            TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        var hit = Assert.Single(lines, line => line.Content == "email image ocr needle");
        Assert.NotNull(hit.Anchor);
        Assert.Equal(SourceAnchorKind.Email, hit.Anchor.Kind);
        Assert.Equal("scan.png", hit.Anchor.MemberPath);
        Assert.True(ocr.SawImageBytes);
    }

    [Fact]
    public async Task RtfExtractor_SplitsParagraphsIntoLines()
    {
        var path = Path.Combine(_directory, "paragraphs.rtf");
        await File.WriteAllTextAsync(
            path,
            @"{\rtf1\ansi First paragraph needle.\par Second paragraph text.}",
            TestContext.Current.CancellationToken);

        var lines = await ReadAllAsync(new RtfExtractor(), path);

        Assert.Equal(2, lines.Count);
        Assert.Equal("First paragraph needle.", lines[0].Content);
        Assert.Equal(1, lines[0].Number);
        Assert.Equal("Second paragraph text.", lines[1].Content);
        Assert.Equal(2, lines[1].Number);
    }

    private static async Task<List<TextLine>> ReadAllAsync(ITextExtractor extractor, string path)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, TestContext.Current.CancellationToken))
            lines.Add(line);
        return lines;
    }

    private static async Task<List<TextLine>> ReadAllAsync(IContextualTextExtractor extractor, string path, TextExtractionContext context)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, context, TestContext.Current.CancellationToken))
            lines.Add(line);
        return lines;
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static void CreateZipBytes(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
    }

    private static void CreatePptx(string path, params string[] texts)
    {
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = doc.AddPresentationPart();
        presentationPart.Presentation = new Presentation(new SlideIdList());

        var slidePart = presentationPart.AddNewPart<SlidePart>();
        var shapeTree = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = "" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));

        foreach (var shape in texts.Select((text, index) => CreateShape((uint)index + 2U, text)))
            shapeTree.Append(shape);

        slidePart.Slide = new Slide(new CommonSlideData(shapeTree));
        slidePart.Slide.Save();

        var relationshipId = presentationPart.GetIdOfPart(slidePart);
        presentationPart.Presentation.SlideIdList!.Append(new SlideId { Id = 256U, RelationshipId = relationshipId });
        presentationPart.Presentation.Save();
    }

    private static Shape CreateShape(uint id, string text)
    {
        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = $"Text {id}" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(),
            new TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(text)))));
    }

    private sealed class FakeEmbeddedImageOcrService(string text) : IEmbeddedImageOcrService
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
                text,
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
