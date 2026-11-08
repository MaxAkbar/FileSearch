using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileSearch.Core.Tests;

public sealed class ZipDocumentExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.ZipDocuments", Guid.NewGuid().ToString("N"));
    private readonly string _scratch;
    private string ArchivePath => Path.Combine(_root, "documents.zip");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ZipDocumentExtractionTests()
    {
        Directory.CreateDirectory(_root);
        _scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task FourNativeDocumentFormatsKeepLocationsAcrossLiveAndIndexedSearch()
    {
        var documents = CreateNativeDocuments(_root);
        WriteArchive(documents.Select(pair => (pair.Key, pair.Value)).ToArray());
        byte[] source = await File.ReadAllBytesAsync(ArchivePath, Token);
        var native = NativeReaders();
        var zip = new ZipExtractor(ZipArchivePolicy.Default, () => native, _scratch);
        var registry = new ExtractorRegistry([zip]);
        var walker = new FileWalker();
        var live = new Searcher(walker, registry);
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(_root, "index.db") }, walker, registry);
        var walk = new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip" } };
        await index.BuildOrRefreshAsync(new IndexRequest(_root, walk), Token);
        var request = new SearchRequest(new TermQuery("needle"), [_root], walk, RawQuery: "needle", Mode: QueryMode.PlainText);
        var liveHits = await Hits(live.SearchAsync(request, Token));
        var indexedHits = await Hits(index.SearchAsync(request with { UseIndex = true }, Token));
        Assert.Equal(4, liveHits.Count);
        Assert.Equal(4, indexedHits.Count);
        Assert.Equal(liveHits.Select(Identity).Order(), indexedHits.Select(Identity).Order());
        Assert.Equal(4, liveHits.Select(hit => hit.LineNumber).Distinct().Count());
        string fingerprint = FormattableString.Invariant($"{source.Length}:{File.GetLastWriteTimeUtc(ArchivePath).Ticks}");
        foreach (var hit in indexedHits)
        {
            Assert.Equal(ArchivePath, hit.Path);
            Assert.Equal(SourceAnchorKind.Archive, hit.Anchor!.Kind);
            Assert.Equal(fingerprint, hit.Anchor.ArchiveFingerprint);
            Assert.Equal(hit.Anchor.MemberPath, hit.Locator!.ArchiveMember);
            Assert.NotNull(hit.Anchor.ArchiveContentAnchor);
            Assert.True(hit.Anchor.ArchiveContentAnchor.Line > 0);
            Assert.Equal(hit.Anchor.ArchiveContentAnchor.Line, hit.Anchor.Line);
            var persisted = JsonSerializer.Deserialize<SourceAnchor>(JsonSerializer.Serialize(hit.Anchor));
            Assert.Equal(hit.Anchor, persisted);
        }
        var pdf = Assert.Single(indexedHits, hit => hit.Anchor!.MemberPath == "reports/report.pdf");
        Assert.Equal(1, pdf.Anchor!.ArchiveContentAnchor!.Page);
        var workbook = Assert.Single(indexedHits, hit => hit.Anchor!.MemberPath == "reports/workbook.xlsx");
        Assert.Equal("Data", workbook.Anchor!.ArchiveContentAnchor!.Sheet);
        Assert.Equal("A1:A1", workbook.Anchor.ArchiveContentAnchor.Cell);
        var slide = Assert.Single(indexedHits, hit => hit.Anchor!.MemberPath == "reports/slides.pptx");
        Assert.Equal(1, slide.Anchor!.ArchiveContentAnchor!.Page);
        Assert.Equal(SHA256.HashData(source), SHA256.HashData(await File.ReadAllBytesAsync(ArchivePath, Token)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
    }

    [Fact]
    public async Task ResolverIsDeferredAndRegisteredReaderUsesIsolatedHost()
    {
        WriteArchive(("file.pdf", Encoding.ASCII.GetBytes("pdf bytes")));
        var reader = new StubReader();
        var registry = new ExtractorRegistry([reader]);
        var host = new StubHost();
        int resolutions = 0;
        var zip = new ZipExtractor(null, () => { resolutions++; return registry; }, host);
        Assert.Equal(0, resolutions);
        var (lines, issues) = await Read(zip);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, host.Calls);
        Assert.False(reader.Invoked);
        Assert.Contains(lines, line => line.Content == "hosted needle");
        Assert.Empty(issues.Issues);
        Assert.NotNull(host.LastPath);
        Assert.False(File.Exists(host.LastPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(host.LastPath)));
    }

    [Fact]
    public async Task MissingHostSkipsDocumentsWithoutLosingTextMembers()
    {
        WriteArchive(("file.pdf", [1, 2]), ("note.txt", Encoding.UTF8.GetBytes("ordinary needle")));
        var host = new StubHost { Available = false };
        var zip = new ZipExtractor(null, () => new ExtractorRegistry([new StubReader()]), host);
        var (lines, issues) = await Read(zip);
        Assert.Contains(lines, line => line.Content == "ordinary needle");
        Assert.DoesNotContain(lines, line => line.Content.Contains("hosted", StringComparison.Ordinal));
        Assert.Contains(issues.Issues, issue => issue.MemberPath == "file.pdf" && issue.Code == "archive_document_host_unavailable");
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task MemberHostTimeoutContinuesToLaterMembersAndCleansTemporaryFile()
    {
        WriteArchive(("file.pdf", [1, 2]), ("note.txt", Encoding.UTF8.GetBytes("later needle")));
        var host = new StubHost { Failure = new ExtractorHostException("extractor_host_timeout", "deadline") };
        var zip = new ZipExtractor(null, () => new ExtractorRegistry([new StubReader()]), host);
        var (lines, issues) = await Read(zip);
        Assert.Contains(lines, line => line.Content == "later needle");
        Assert.Contains(issues.Issues, issue => issue.Code == "archive_document_timeout");
        Assert.False(File.Exists(host.LastPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(host.LastPath)));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("/absolute.pdf")]
    [InlineData("C:/absolute.pdf")]
    [InlineData("folder\\escape.pdf")]
    public async Task UnsafeMembersAreNeverParsed(string name)
    {
        WriteArchive((name, [1]), ("note.txt", Encoding.UTF8.GetBytes("safe needle")));
        var reader = new StubReader();
        var (lines, issues) = await Read(Direct(reader));
        Assert.False(reader.Invoked);
        Assert.Contains(lines, line => line.Content == "safe needle");
        Assert.Contains(issues.Issues, issue => issue.Code == "archive_member_unsafe_path");
    }

    [Fact]
    public async Task DuplicateExactNamesAreAllSkippedButCaseDistinctNamesRemainDistinct()
    {
        WriteArchive(("same.pdf", [1]), ("same.pdf", [2]), ("SAME.pdf", [3]));
        var reader = new StubReader();
        var (lines, issues) = await Read(Direct(reader));
        Assert.Equal(2, issues.Issues.Count(issue => issue.Code == "archive_member_duplicate_name"));
        Assert.DoesNotContain(lines, line => line.Anchor?.MemberPath == "same.pdf");
        Assert.Contains(lines, line => line.Anchor?.MemberPath == "SAME.pdf");
    }

    [Fact]
    public async Task ExactMemberByteCapIsNotTruncatedAndIncompleteDocumentIsNeverParsed()
    {
        WriteArchive(("exact.pdf", new byte[1024]));
        var reader = new StubReader();
        var (lines, issues) = await Read(Direct(reader, new ZipArchivePolicy(maxEntryBytes: 1024, maxTotalBytes: 1024)));
        Assert.True(reader.Invoked);
        Assert.Contains(lines, line => line.Content == "reader needle");
        Assert.DoesNotContain(issues.Issues, issue => issue.Code == "archive_member_truncated");

        File.Delete(ArchivePath);
        WriteArchive(("partial.pdf", new byte[1024]));
        var partialReader = new StubReader();
        var (partial, partialIssues) = await Read(Direct(partialReader,
            new ZipArchivePolicy(maxEntryBytes: 1024, maxTotalBytes: 512)));
        Assert.False(partialReader.Invoked);
        Assert.Empty(partial);
        Assert.Contains(partialIssues.Issues, issue => issue.Code == "archive_member_truncated");
    }

    [Theory]
    [InlineData("<a><b><c><d>needle</d></c></b></a>", "archive_document_xml_depth")]
    [InlineData("<!DOCTYPE a [<!ENTITY x 'needle'>]><a>&x;</a>", "archive_document_package_unreadable")]
    [InlineData("<a>", "archive_document_package_unreadable")]
    public async Task UnsafeOrInvalidOfficeXmlIsRejectedBeforeReader(string xml, string code)
    {
        WriteArchive(("file.docx", Package(("word/document.xml", Encoding.UTF8.GetBytes(xml)))));
        var reader = new StubReader(".docx");
        var (_, issues) = await Read(Direct(reader, new ZipArchivePolicy(maxXmlDepth: 2)));
        Assert.False(reader.Invoked);
        Assert.Contains(issues.Issues, issue => issue.Code == code);
    }

    [Fact]
    public async Task InternalOfficeExpansionIsChargedToArchiveBudgetBeforeParser()
    {
        byte[] document = Package(("word/document.xml", Encoding.UTF8.GetBytes("<a>" + new string('x', 12000) + "</a>")));
        Assert.True(document.Length < 1024);
        WriteArchive(("small.docx", document), ("later.txt", Encoding.UTF8.GetBytes("later needle")));
        var reader = new StubReader(".docx");
        var (lines, issues) = await Read(Direct(reader,
            new ZipArchivePolicy(maxEntryBytes: 20000, maxTotalBytes: 1000)));
        Assert.False(reader.Invoked);
        Assert.Contains(issues.Issues, issue => issue.Code == "archive_document_package_limit");
        Assert.Contains(lines, line => line.Content == "later needle");
    }

    [Fact]
    public async Task OutputAndAnchorMetadataConsumeBoundedTextBudget()
    {
        WriteArchive(("file.pdf", [1]));
        var reader = new StubReader { Text = new string('x', 10000) };
        var (lines, issues) = await Read(Direct(reader,
            new ZipArchivePolicy(maxEntryTextCharacters: 2000, maxTotalTextCharacters: 2000, maxEntryTextLines: 3)));
        Assert.True(lines.Sum(line => line.Content.Length + line.Anchor!.DisplayText.Length) < 2000);
        Assert.Contains(issues.Issues, issue => issue.Code == "archive_member_text_limit");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
    }

    [Fact]
    public async Task CancellationAndArchiveDeadlineCleanDirectParserWorkspace()
    {
        WriteArchive(("file.pdf", [1]));
        var reader = new StubReader { WaitForCancellation = true };
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(Direct(reader), canceled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
        var (_, issues) = await Read(Direct(new StubReader { WaitForCancellation = true },
            new ZipArchivePolicy(maxDuration: TimeSpan.FromMilliseconds(100))));
        Assert.Contains(issues.Issues, issue => issue.Code == "archive_time_limit");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
    }

    [Fact]
    public async Task EmbeddedDocumentOcrIsDisabledAndEarlyStopLeavesNoTemporaryFiles()
    {
        WriteArchive(("file.pdf", [1]));
        var reader = new StubReader();
        await foreach (var _ in Direct(reader).ExtractAsync(ArchivePath, new TextExtractionContext(EnableOcr: true), Token))
            break;
        Assert.False(reader.OcrEnabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
    }

    private ZipExtractor Direct(ITextExtractor reader, ZipArchivePolicy? policy = null) =>
        new(policy ?? ZipArchivePolicy.Default, () => new ExtractorRegistry([reader]), _scratch);

    private async Task<(List<TextLine> Lines, ListExtractionIssueSink Issues)> Read(ZipExtractor extractor, CancellationToken? token = null)
    {
        var lines = new List<TextLine>();
        var issues = new ListExtractionIssueSink();
        await foreach (var line in extractor.ExtractAsync(ArchivePath, new TextExtractionContext(), issues, token ?? Token)) lines.Add(line);
        return (lines, issues);
    }

    private static async Task<List<Hit>> Hits(IAsyncEnumerable<Hit> input)
    {
        var hits = new List<Hit>();
        await foreach (var hit in input) hits.Add(hit);
        return hits;
    }

    private static string Identity(Hit hit) =>
        $"{hit.Path}|{hit.LineNumber}|{JsonSerializer.Serialize(hit.Anchor)}|{hit.LineContent}";

    private void WriteArchive(params (string Name, byte[] Bytes)[] entries) => File.WriteAllBytes(ArchivePath, Package(entries));

    private static byte[] Package(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var stream = package.CreateEntry(entry.Name, CompressionLevel.SmallestSize).Open();
                stream.Write(entry.Bytes);
            }
        return output.ToArray();
    }

    private static IExtractorRegistry NativeReaders() =>
        new ExtractorRegistry([new PdfExtractor(), new WordExtractor(), new ExcelExtractor(), new PowerPointExtractor()]);

    private static Dictionary<string, byte[]> CreateNativeDocuments(string root)
    {
        string word = Path.Combine(root, "source.docx");
        using (var document = WordprocessingDocument.Create(word, WordprocessingDocumentType.Document))
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Word needle")))));
        string excel = Path.Combine(root, "source.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Data").Cell("A1").Value = "Workbook needle";
            workbook.SaveAs(excel);
        }
        string powerpoint = Path.Combine(root, "source.pptx");
        using (var presentation = PresentationDocument.Create(powerpoint, PresentationDocumentType.Presentation))
        {
            var main = presentation.AddPresentationPart();
            var slide = main.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(
                new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = "Content" },
                    new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(), new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("PowerPoint needle"))))))));
            main.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = main.GetIdOfPart(slide) }));
        }
        return new()
        {
            ["reports/report.docx"] = File.ReadAllBytes(word),
            ["reports/workbook.xlsx"] = File.ReadAllBytes(excel),
            ["reports/slides.pptx"] = File.ReadAllBytes(powerpoint),
            ["reports/report.pdf"] = CreatePdf(),
        };
    }

    private static byte[] CreatePdf()
    {
        const string content = "BT /F1 12 Tf 40 700 Td (PDF needle) Tj ET\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private sealed class StubReader(string extension = ".pdf") : IContextualTextExtractor
    {
        public string ExtractorId => "test.archive-document";
        public IReadOnlyCollection<string> SupportedExtensions => [extension];
        public bool Invoked { get; private set; }
        public bool OcrEnabled { get; private set; }
        public bool WaitForCancellation { get; init; }
        public string Text { get; init; } = "reader needle";
        public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken token) => ExtractAsync(path, new(), token);
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, TextExtractionContext context,
            [EnumeratorCancellation] CancellationToken token)
        {
            Invoked = true;
            OcrEnabled = context.EnableOcr;
            Assert.True(File.Exists(path));
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield return new TextLine(1, Text, SourceAnchor.PdfPage(7));
        }
    }

    private sealed class StubHost : IOutOfProcessExtractionService
    {
        public bool Available { get; init; } = true;
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public bool ShouldUse(ITextExtractor reader) => Available;
        public Task<OutOfProcessExtractionResult> ExtractAsync(string path, ITextExtractor reader, CancellationToken token)
        {
            Calls++;
            LastPath = path;
            Assert.True(File.Exists(path));
            if (Failure is not null) return Task.FromException<OutOfProcessExtractionResult>(Failure);
            return Task.FromResult(new OutOfProcessExtractionResult([new TextLine(4, "hosted needle", SourceAnchor.PdfPage(4))], []));
        }
    }
}
