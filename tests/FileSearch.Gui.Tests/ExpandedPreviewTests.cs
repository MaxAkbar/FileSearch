using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Tests;
using FileSearch.Gui.Services;
using ImagingDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace FileSearch.Gui.Tests;

public sealed class ExpandedPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchExpandedPreview-" + Guid.NewGuid().ToString("N"));
    public ExpandedPreviewTests() => PreviewFixtures.Create(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private Task<DocumentPreview> Load(string name, Hit? hit = null, string? part = null, int page = 1) =>
        DocumentPreviewService.LoadAsync(Path.Combine(_root, name), TestContext.Current.CancellationToken, hit, part, page);

    [Fact]
    public async Task QuotedMultilineCsvMapsPhysicalHitToLogicalRowAndPreservesLastRow()
    {
        var document = await Load("table.csv", new Hit("table.csv", 3, "Matching second line", []));
        var workbook = Assert.IsType<SpreadsheetPreview>(document.Workbook);
        Assert.Equal(2, workbook.InitialRow);
        Assert.Equal("First line\nMatching second line", workbook.Sheets[0].Cell(2, 2));
        Assert.Equal("Last row", workbook.Sheets[0].Cell(3, 2));
    }

    [Fact]
    public async Task SparseWorkbookTargetsMatchingSheetAndCellBeyondFirstViewportWithoutExecutingFormulas()
    {
        var hit = new Hit("workbook.xlsx", 2, "[Data] First\tMatching cell\t3", [new MatchSpan(13, 8)],
            Anchor: new SourceAnchor(SourceAnchorKind.Excel, "Data!B501:BB501", Sheet: "Data", Cell: "B501:BB501"));
        var workbook = (await Load("workbook.xlsx", hit)).Workbook!;
        Assert.Equal("Data", workbook.InitialSheet);
        Assert.Equal(501, workbook.InitialRow);
        Assert.Equal(52, workbook.InitialColumn);
        var sheet = workbook.Sheets.Single(sheet => sheet.Name == "Data");
        Assert.Equal("3", sheet.Cell(501, 53));
        Assert.StartsWith("=HYPERLINK", sheet.Cell(501, 54));
        Assert.Equal(501, sheet.RowCount);
    }

    [Fact]
    public async Task ArchiveTargetsMatchingMemberExcludesTraversalAndCleansOnlyItsOwnedFile()
    {
        var hit = new Hit("members.zip", 4, "Matching archive member", [], Anchor: new SourceAnchor(SourceAnchorKind.Archive, "folder/match.txt", MemberPath: "folder/match.txt"));
        var document = await Load("members.zip", hit);
        Assert.Equal("folder/match.txt", document.SelectedPart);
        Assert.Equal(2, document.Parts!.Count);
        string owned;
        using (var lease = await ArchivePreviewService.OpenMemberAsync(Path.Combine(_root, "members.zip"), document.SelectedPart!, TestContext.Current.CancellationToken))
        {
            owned = lease.Path;
            var member = await DocumentPreviewService.LoadAsync(owned, TestContext.Current.CancellationToken);
            Assert.Contains("End of whole member", member.Text);
            Assert.NotEqual("match.txt", Path.GetFileName(owned));
        }
        Assert.False(File.Exists(owned));
        Assert.False(Directory.Exists(Path.GetDirectoryName(owned)));
        Assert.True(File.Exists(Path.Combine(_root, "members.zip")));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchivePreviewService.OpenMemberAsync(Path.Combine(_root, "members.zip"), "../unsafe.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArchivePdfMemberRetainsWholeMatchingPage()
    {
        string pdf = Path.Combine(_root, "page.pdf");
        DocumentPreviewServiceTests.WritePdf(pdf);
        string path = Path.Combine(_root, "pdf.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntryFromFile(pdf, "folder/page.pdf");
        using var lease = await ArchivePreviewService.OpenMemberAsync(path, "folder/page.pdf", TestContext.Current.CancellationToken);
        var document = await DocumentPreviewService.LoadAsync(lease.Path, TestContext.Current.CancellationToken, page: 2);
        Assert.Equal(2, document.Page);
        Assert.Equal(2, document.PageCount);
        Assert.NotNull(document.Image);
    }

    [Fact]
    public async Task PackageReadRejectsExpansionBombAndExternalXmlEntities()
    {
        string path = PreviewFixtures.Package(_root, "bomb.zip", ("huge.txt", new string('x', 2 * 1024 * 1024)));
        using var zip = PreviewPackage.Open(path);
        Assert.Throws<InvalidDataException>(() => PreviewPackage.Read(zip.Entries[0], token: TestContext.Current.CancellationToken));
        Assert.Throws<System.Xml.XmlException>(() => PreviewPackage.ParseXml(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///private'>]><a>&e;</a>")));
        Assert.Throws<InvalidDataException>(() => PreviewPackage.ResolveMember("chapter.xhtml", "../escape"));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchivePreviewService.OpenMemberAsync(path, "huge.txt", cancelled.Token));
    }

    [Fact]
    public async Task EmlLoadsWholeFormattedMessageAndStripsActiveAndRemoteContent()
    {
        var document = await Load("message.eml");
        Assert.Contains("Beginning of message", document.Text);
        Assert.Contains("<strong>End of whole message</strong>", document.Text);
        Assert.Contains("sender@example.invalid", document.Text);
        Assert.DoesNotContain("<script", document.Text);
        Assert.DoesNotContain("tracker.png", document.Text);
        Assert.DoesNotContain("1/1/0001", document.Text);
        Assert.False(document.IsTruncated);
    }

    [Fact]
    public async Task SelectedStoreMessageUsesFullBodyAndChecksStoreFingerprint()
    {
        using var files = new OutlookTestFiles();
        string path = files.PathFor("bodies.pst");
        var lines = new List<TextLine>();
        await foreach (var line in new OutlookStoreExtractor().ExtractAsync(path, TestContext.Current.CancellationToken)) lines.Add(line);
        var metadata = lines[0].Anchor!.MailMessage!;
        var document = await MailPreviewService.LoadAsync(path, metadata, TestContext.Current.CancellationToken);
        Assert.Contains("original email", document.Text);
        Assert.DoesNotContain("FW: original email", document.Text);
        Assert.Contains("Complete selected message", document.Summary);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => MailPreviewService.LoadAsync(path, metadata, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("multipage.tiff")]
    [InlineData("animated.gif")]
    public async Task ImageFramesNavigateAndReleaseTheOriginalFile(string name)
    {
        var first = await Load(name, page: 1);
        var second = await Load(name, page: 2);
        Assert.Equal(2, second.PageCount);
        Assert.Equal(2, second.Page);
        byte[] a = new byte[4], b = new byte[4];
        first.Image!.CopyPixels(a, 4, 0); second.Image!.CopyPixels(b, 4, 0);
        Assert.NotEqual(a, b);
        Assert.Equal(name.EndsWith(".gif", StringComparison.Ordinal), second.Animation is not null);
        File.Move(Path.Combine(_root, name), Path.Combine(_root, name + ".moved"));
    }

    [Fact]
    public async Task WebpUsesTheWindowsDecoderOrExplainsWhyPreviewIsUnavailable()
    {
        bool decoderAvailable = ImagePreviewService.HasWebpDecoder();
        var document = await Load("image.webp");
        if (document.Kind == DocumentPreviewKind.Details)
        {
            if (decoderAvailable)
            {
                Assert.Contains("Windows WebP decoder could not open", document.Text);
                Assert.Matches("0x[0-9A-F]{8}", document.Text);
            }
            else Assert.Contains("Windows WebP decoder is not available", document.Text);
            Assert.Contains("Use Open", document.Text);
            Assert.Null(document.Image);
            return;
        }
        Assert.Equal(DocumentPreviewKind.Image, document.Kind);
        Assert.True(document.Image!.IsFrozen);
        Assert.Equal(1, document.Image.PixelWidth);
    }

    [Fact]
    public async Task MissingWebpDecoderKeepsTheFileAvailableWithoutDecodingOrChangingIt()
    {
        string path = Path.Combine(_root, "image.webp");
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var document = await ImagePreviewService.LoadAsync(path, 1, static () => false, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Details, document.Kind);
        Assert.Contains("Windows WebP decoder is not available", document.Text);
        Assert.Contains("Use Open", document.Text);
        Assert.Null(document.Image);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        File.Move(path, path + ".moved");
    }

    [Fact]
    public async Task RegisteredWebpDecoderFailureShowsTheNativeErrorWithoutChangingOrLockingTheSource()
    {
        string path = Path.Combine(_root, "image.webp");
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var error = new COMException("", unchecked((int)0x88982F50));
        var document = await ImagePreviewService.LoadAsync(path, 1, static () => true,
            (_, _) => Task.FromException<ImagingDecoder>(error), TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Details, document.Kind);
        Assert.Contains("Windows WebP decoder could not open", document.Text);
        Assert.Contains("0x88982F50", document.Text);
        Assert.Contains("unusable", document.Text);
        Assert.Contains("damaged", document.Text);
        Assert.Contains("Use Open", document.Text);
        Assert.Null(document.Image);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        File.Move(path, path + ".moved");
    }

    [Theory]
    [InlineData("animated.gif", unchecked((int)0x88982F50))]
    [InlineData("image.webp", unchecked((int)0x80004004))]
    [InlineData("image.webp", unchecked((int)0x8007000E))]
    [InlineData("image.webp", unchecked((int)0x800704C7))]
    public async Task DecoderComFailuresForOtherFormatsCancellationAndMemoryRemainErrors(string name, int hresult)
    {
        var error = new COMException("decoder error", hresult);
        var actual = await Assert.ThrowsAsync<COMException>(() => ImagePreviewService.LoadAsync(Path.Combine(_root, name), 1,
            static () => true, (_, _) => Task.FromException<ImagingDecoder>(error), TestContext.Current.CancellationToken));
        Assert.Same(error, actual);
    }

    [Fact]
    public async Task NonComDecoderFailuresRemainErrors()
    {
        foreach (var error in new Exception[] { new InvalidDataException("invalid image"), new OutOfMemoryException("decoder memory") })
        {
            var actual = await Record.ExceptionAsync(() => ImagePreviewService.LoadAsync(Path.Combine(_root, "image.webp"), 1,
                static () => true, (_, _) => Task.FromException<ImagingDecoder>(error), TestContext.Current.CancellationToken));
            Assert.Same(error, actual);
        }
    }

    [Fact]
    public async Task DecoderCancellationDoesNotBecomeAWebpDetailsFallback()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImagePreviewService.LoadAsync(Path.Combine(_root, "image.webp"), 1,
            static () => true, static (_, _) => Task.FromCanceled<ImagingDecoder>(new CancellationToken(canceled: true)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDuringWebpCapabilityProbeDoesNotBecomeADetailsFallback()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImagePreviewService.LoadAsync(Path.Combine(_root, "image.webp"), 1,
            () => { cancel.Cancel(); return false; }, cancel.Token));
    }

    [Fact]
    public async Task RequestCancellationWinsOverWebpDecoderComFailure()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImagePreviewService.LoadAsync(Path.Combine(_root, "image.webp"), 1,
            static () => true, (_, _) =>
            {
                cancel.Cancel();
                return Task.FromException<ImagingDecoder>(new COMException("decoder error", unchecked((int)0x88982F50)));
            }, cancel.Token));
    }

    [Fact]
    public async Task EpubUsesSpineOrderAndWholeMatchingChapter()
    {
        var hit = new Hit("book.epub", 2, "[Book/two.xhtml] Matching chapter", []);
        var document = await Load("book.epub", hit);
        Assert.Equal("Book/one.xhtml", document.Parts![0].Id);
        Assert.Equal("Book/two.xhtml", document.SelectedPart);
        Assert.Contains("End of whole chapter", document.Text);
        Assert.DoesNotContain("<script", document.Text);
        Assert.DoesNotContain("tracker.png", document.Text);
        Assert.Contains("First chapter", (await Load("book.epub", hit, "Book/one.xhtml")).Text);
    }

    [Fact]
    public async Task OpenDocumentAndRtfRenderWithoutOffice()
    {
        var odt = await Load("document.odt");
        Assert.Contains("Document title", odt.Text);
        Assert.Contains("<table>", odt.Text);
        Assert.Contains("Whole paragraph", odt.Text);
        var odp = await Load("slides.odp", part: "2");
        Assert.Equal(2, odp.Parts!.Count);
        Assert.Contains("Whole second slide", odp.Text);
        Assert.DoesNotContain("Whole first slide", odp.Text);
        var rtf = await Load("formatted.rtf");
        Assert.Contains("Final paragraph", rtf.Text);
        Assert.Contains("formatted", rtf.Text);
        Assert.Equal(DocumentPreviewKind.Html, rtf.Kind);
    }

    [Fact]
    public async Task OdsSkipsBlankRepeatRunsAndPreservesSparseCellCoordinates()
    {
        var hit = new Hit("spreadsheet.ods", 1, "Matching ODS cell", [], Anchor: new SourceAnchor(SourceAnchorKind.OpenDocument, "Data!AZ501", Sheet: "Data", Cell: "AZ501"));
        var workbook = (await Load("spreadsheet.ods", hit)).Workbook!;
        Assert.Equal(501, workbook.InitialRow);
        Assert.Equal(52, workbook.InitialColumn);
        Assert.Single(workbook.Sheets[0].Rows);
        Assert.Equal("Matching ODS cell", workbook.Sheets[0].Cell(501, 52));
        var legacy = hit with { Anchor = null, Highlights = [new MatchSpan(0, 8)] };
        var located = (await Load("spreadsheet.ods", legacy)).Workbook!;
        Assert.Equal(501, located.InitialRow);
        Assert.Equal(52, located.InitialColumn);
    }

    [Fact]
    public async Task TsvPreservesUnicodeAndTargetsMatchingColumn()
    {
        File.WriteAllText(Path.Combine(_root, "data.tsv"), "Name\tValue\n東京\tMatching value", System.Text.Encoding.Unicode);
        var hit = new Hit("data.tsv", 2, "東京\tMatching value", [new MatchSpan(3, 8)]);
        var document = await Load("data.tsv", hit);
        Assert.Equal("東京", document.Workbook!.Sheets[0].Cell(2, 1));
        Assert.Equal(2, document.Workbook.InitialColumn);
    }

    [Fact]
    public async Task EmlInlineImagesAndAttachmentNamesAreLocalAndEscaped()
    {
        var message = new MimeKit.MimeMessage();
        message.Subject = "Inline image";
        var builder = new MimeKit.BodyBuilder { HtmlBody = "<p>Body</p><img src='cid:one'><img src='cid:one'>" };
        var resource = builder.LinkedResources.Add("inline.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        resource.ContentId = "one";
        builder.Attachments.Add("<contract>.txt", new byte[] { 1, 2, 3 });
        message.Body = builder.ToMessageBody();
        await message.WriteToAsync(Path.Combine(_root, "inline.eml"), TestContext.Current.CancellationToken);
        var document = await Load("inline.eml");
        Assert.Single(document.Resources!);
        Assert.Contains("&lt;contract&gt;.txt", document.Text);
        Assert.Contains(document.Resources!.Keys.Single(), document.Text);
    }

    [Fact]
    public async Task MatchingSlideCanBeLocatedFromLegacyText()
    {
        var document = await Load("slides.odp", new Hit("slides.odp", 1, "Whole second slide", [new MatchSpan(6, 6)]));
        Assert.Equal("2", document.SelectedPart);
        Assert.Contains("Whole second slide", document.Text);
    }

    [Fact]
    public void SanitizerKeepsApprovedEmbeddedImagesButRemovesActiveCssAndLinks()
    {
        var content = new HtmlPreviewContent();
        string safe = content.Sanitize("<p style='font-weight:bold;background-image:url(https://example.invalid)'>Text</p><img src='cid:one' onerror='bad()'><iframe src='file:///private'></iframe><a href='https://example.invalid'>Link text</a>",
            source => source == "cid:one" ? new PreviewResource([1, 2, 3], "image/png") : null);
        Assert.Single(content.Resources);
        Assert.Contains(content.Resources.Keys.Single(), safe);
        Assert.Contains("font-weight", safe);
        Assert.DoesNotContain("background-image", safe);
        Assert.DoesNotContain("onerror", safe);
        Assert.DoesNotContain("iframe", safe);
        Assert.DoesNotContain("href", safe);
        Assert.Contains("Link text", safe);
    }
}
