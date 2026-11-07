using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class DocumentPreviewServiceTests : IDisposable
{
    [Fact]
    public void PdfPageSelectionUsesSnippetLocatorThenLocatorThenAnchor()
    {
        var hit = new Hit("file.pdf", 8, "partial", [], Anchor: SourceAnchor.PdfPage(1),
            Locator: new SourceLocator(Page: 2), Snippet: new SearchSnippet("partial", locator: new SourceLocator(Page: 3)));
        Assert.Equal(3, DocumentPreviewService.FindInitialPdfPage([hit]));
        Assert.Equal(2, DocumentPreviewService.FindInitialPdfPage([hit with { Snippet = null }]));
        Assert.Equal(1, DocumentPreviewService.FindInitialPdfPage([hit with { Snippet = null, Locator = null }]));
        var other = hit with { Snippet = null, Locator = new SourceLocator(Page: 7) };
        Assert.Equal(7, DocumentPreviewService.FindInitialPdfPage([hit, other], other));
        Assert.Equal(1, DocumentPreviewService.FindInitialPdfPage([]));
        Assert.Equal(1, DocumentPreviewService.FindInitialPdfPage([hit with { Kind = HitKind.Metadata }]));
    }

    [Fact]
    public async Task APartialOcrHitStillRendersEveryCornerOfTheOriginalPage()
    {
        var path = Path.Combine(_root, "whole-page.pdf");
        WritePdf(path);
        var hit = new Hit(path, 9, "small partial hit", [], Anchor: SourceAnchor.PdfOcrRegion(2, 80, 90, 5, 3, 200, 200));
        var result = await DocumentPreviewService.LoadPdfPageAsync(path,
            DocumentPreviewService.FindInitialPdfPage([hit]), TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Page);
        Assert.NotNull(result.Image);
        var image = new FormatConvertedBitmap(result.Image, PixelFormats.Bgra32, null, 0);
        int width = image.PixelWidth, height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        foreach (var (x, y) in new[] { (width / 40, height / 40), (width * 39 / 40, height / 40),
                     (width / 40, height * 39 / 40), (width * 39 / 40, height * 39 / 40) })
        {
            int offset = (y * width + x) * 4;
            Assert.True(pixels[offset + 2] > 200 && pixels[offset] < 50 && pixels[offset + 1] < 50,
                $"The complete page corner at {x},{y} was not rendered.");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchPreviewTests-" + Guid.NewGuid().ToString("N"));
    public DocumentPreviewServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ImagePreviewNeedsNoSearchHitAndReleasesSourceFile()
    {
        var path = Path.Combine(_root, "image.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Image, result.Kind);
        Assert.NotNull(result.Image);
        Assert.True(result.Image.IsFrozen);
        Assert.Equal(1, result.Image.PixelWidth);
        File.Move(path, path + ".moved");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path + ".moved", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PdfPreviewRendersAndNavigatesActualPages()
    {
        var path = Path.Combine(_root, "pages.pdf");
        WritePdf(path);
        var first = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        var last = await DocumentPreviewService.LoadPdfPageAsync(path, 99, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Pdf, first.Kind);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, last.Page);
        Assert.NotNull(first.Image);
        Assert.NotNull(last.Image);
        Assert.InRange(first.Image.PixelWidth, 1, 1600);
        Assert.True(last.Image.IsFrozen);
        File.Move(path, path + ".moved");
    }

    [Fact]
    public async Task SourcePreviewPreservesOriginalAndDoesNotExecuteHtml()
    {
        const string text = "<script>window.bad=true</script>\n<h1>Example</h1>";
        var path = Write("source.html", text);
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Text, result.Kind);
        Assert.Equal(text, result.Text);
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task MarkdownFormatsTablesAndEscapesRawHtmlWithNetworkBlockingPolicy()
    {
        var path = Write("guide.md", "# Title\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n<script>alert(1)</script>\n\n![remote](https://example.invalid/private.png)");
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Html, result.Kind);
        Assert.Contains("<h1>Title</h1>", result.Text);
        Assert.Contains("<table>", result.Text);
        Assert.DoesNotContain("<script>", result.Text);
        Assert.Contains("default-src 'none'", result.Text);
        Assert.Contains("img-src data:", result.Text);
    }

    [Fact]
    public void SvgRemovesScriptsEventsAndExternalReferences()
    {
        var html = DocumentPreviewService.CreateSvgHtml("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'><script>bad()</script><foreignObject>bad</foreignObject><image href='file:///private.png'/><rect width='10' height='20'/></svg>");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("foreignObject", html);
        Assert.DoesNotContain("private.png", html);
        Assert.Contains("<rect", html);
    }

    [Fact]
    public void SvgRefusesExternalEntities() => Assert.Throws<XmlException>(() => DocumentPreviewService.CreateSvgHtml(
        "<!DOCTYPE svg [<!ENTITY secret SYSTEM 'file:///private.txt'>]><svg xmlns='http://www.w3.org/2000/svg'>&secret;</svg>"));

    [Fact]
    public async Task UnknownBinaryUsesFileDetails()
    {
        var path = Path.Combine(_root, "binary.filesearchunknown");
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3, 0, 255], TestContext.Current.CancellationToken);
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Details, result.Kind);
        Assert.NotEmpty(result.Text);
    }

    [Fact]
    public async Task MissingFilesAndFoldersHaveUsefulFallbacks()
    {
        Assert.Equal(DocumentPreviewKind.Details, (await DocumentPreviewService.LoadAsync(_root, TestContext.Current.CancellationToken)).Kind);
        Assert.Contains("moved", (await DocumentPreviewService.LoadAsync(Path.Combine(_root, "missing"), TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task LargeFilesAreBoundedBeforeReading()
    {
        var path = Path.Combine(_root, "large.txt");
        using (var stream = File.Create(path)) stream.SetLength(DocumentPreviewService.MaxTextBytes + 1);
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Details, result.Kind);
        Assert.Contains("limited", result.Text);
    }

    [Fact]
    public async Task LongTextReportsTruncation()
    {
        var path = Write("long.cs", new string('a', DocumentPreviewService.MaxTextCharacters + 100));
        var result = await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.True(result.IsTruncated);
        Assert.Equal(DocumentPreviewService.MaxTextCharacters, result.Text.Length);
    }

    [Fact]
    public async Task Utf16TextIsDecodedBeforeBinaryDetection()
    {
        var path = Path.Combine(_root, "unicode.txt");
        File.WriteAllText(path, "Hello 世界", Encoding.Unicode);
        Assert.Equal("Hello 世界", (await DocumentPreviewService.LoadAsync(path, TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task CancelledPreviewDoesNotReadTheFile()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentPreviewService.LoadAsync(Write("cancel.txt", "text"), cancel.Token));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    internal static void WritePdf(string path)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 24 Tf 20 100 Td (First page) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            Stream("q 1 0 0 rg 0 0 12 12 re f 188 0 12 12 re f 0 188 12 12 re f 188 188 12 12 re f Q BT /F1 24 Tf 20 100 Td (Second page) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
    }
}
