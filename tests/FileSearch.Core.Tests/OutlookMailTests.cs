using System.Buffers.Binary;
using System.Text;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using OpenMcdf;

namespace FileSearch.Core.Tests;

public sealed class OutlookMailTests
{
    [Fact]
    public async Task MsgReadsRealHeadersAndBodyWithoutOutlook()
    {
        using var files = new OutlookTestFiles();
        var lines = await ReadAsync(new MsgExtractor(), files.PathFor("message.msg"));
        Assert.Contains(lines, line => line.Content == "Subject: MIME registry use cases");
        Assert.Contains(lines, line => line.Content.Contains("jukka.zitting@gmail.com", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Content.Contains("modular and extensible", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(SourceAnchorKind.Email, line.Anchor!.Kind));
        Assert.Equal(new DateTime(2009, 1, 29, 19, 16, 40, DateTimeKind.Utc), lines[0].Anchor!.MailMessage!.DateUtc);
    }

    [Theory]
    [InlineData("bodies.pst", 4)]
    [InlineData("sample.ost", 92)]
    public async Task RealStoresKeepDistinctMessageIdentitiesAndStableLineNumbers(string name, int expectedMessages)
    {
        using var files = new OutlookTestFiles();
        var lines = await ReadAsync(new OutlookStoreExtractor(), files.PathFor(name));
        Assert.Equal(expectedMessages, lines.Select(line => line.Anchor!.MailMessage!.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, lines.Count), lines.Select(line => line.Number));
        foreach (var group in lines.GroupBy(line => line.Anchor!.MailMessage!.Id))
        {
            var metadata = group.First().Anchor!.MailMessage!;
            Assert.Equal(group.First().Number, metadata.FirstLineNumber);
            Assert.NotEmpty(metadata.StoreFingerprint!);
            Assert.NotEmpty(metadata.Folder);
            var reread = OutlookMailReader.ReadMessageLines(files.PathFor(name), metadata, TestContext.Current.CancellationToken).ToArray();
            Assert.Equal(group.Select(line => line.Content), reread.Select(line => line.Content));
            Assert.Equal(group.Select(line => line.Number), reread.Select(line => line.Number));
        }
        using var exclusive = new FileStream(files.PathFor(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task IndexedAndLiveMailSearchPreserveMetadataAndApplyHitLimitsPerMessage()
    {
        using var files = new OutlookTestFiles();
        var walker = new FileWalker();
        var registry = new ExtractorRegistry([new MsgExtractor(), new OutlookStoreExtractor()]);
        var options = new SearchOptions { MaxHitsPerFile = 1 };
        var live = new Searcher(walker, registry, options);
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = files.PathFor("index.db") },
            walker, registry, options);
        var walk = new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pst" } };
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, walk), TestContext.Current.CancellationToken);
        var request = new SearchRequest(new TermQuery("original"), [files.Root], walk);
        var liveHits = await HitsAsync(live.SearchAsync(request, TestContext.Current.CancellationToken));
        var indexedHits = await HitsAsync(index.SearchAsync(request with { UseIndex = true }, TestContext.Current.CancellationToken));
        Assert.Equal(4, liveHits.Count);
        Assert.Equal(liveHits.OrderBy(hit => hit.ResultKey).Select(hit => (hit.LineNumber, hit.LineContent, hit.MailMessage)),
            indexedHits.OrderBy(hit => hit.ResultKey).Select(hit => (hit.LineNumber, hit.LineContent, hit.MailMessage)));
        Assert.All(indexedHits, hit => Assert.Equal(HitRoute.Indexed, hit.Route));
    }

    [Fact]
    public async Task ChangedStoreCannotOpenAnOldMessageIdentity()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("bodies.pst");
        var lines = await ReadAsync(new OutlookStoreExtractor(), path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        var exception = Assert.Throws<IOException>(() => OutlookMailReader.ReadMessage(path, lines[0].Anchor!.MailMessage!, TestContext.Current.CancellationToken));
        Assert.Contains("changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LimitsReportPartialExtractionAndCancellationStopsReading()
    {
        using var files = new OutlookTestFiles();
        var extractor = new OutlookStoreExtractor(new OutlookMailOptions { MaxMessages = 1 });
        var issues = new ListExtractionIssueSink();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(files.PathFor("bodies.pst"), issues, TestContext.Current.CancellationToken))
            lines.Add(line);
        Assert.Single(lines.Select(line => line.Anchor!.MailMessage!.Id).Distinct());
        Assert.Contains(issues.Issues, issue => issue.Code == "mail_store_limit");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in extractor.ExtractAsync(files.PathFor("bodies.pst"), cancelled.Token)) { }
        });
    }

    [Fact]
    public async Task MessagePreviewEmlRoundTripsThroughEmailExtractor()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("bodies.pst");
        var lines = await ReadAsync(new OutlookStoreExtractor(), path);
        var output = files.PathFor("preview.eml");
        await OutlookMailReader.WriteMessagePreviewAsync(path, lines[0].Anchor!.MailMessage!, output, TestContext.Current.CancellationToken);
        var preview = await ReadAsync(new EmlExtractor(), output);
        Assert.Contains(preview, line => line.Content == "Subject: original email");
        Assert.Contains(preview, line => line.Content == "This is the original email (html)");
        Assert.DoesNotContain(preview, line => line.Content.Contains("FW: original email", StringComparison.Ordinal));
        await Assert.ThrowsAsync<IOException>(() => OutlookMailReader.WriteMessagePreviewAsync(path, lines[0].Anchor!.MailMessage!, output, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".pst")]
    [InlineData(".ost")]
    [InlineData(".msg")]
    public void EmailTypeIncludesOutlookFormats(string extension)
    {
        var query = new UnifiedQueryParser().Parse("type:email original");
        Assert.Contains(extension, query.Filters.Extensions);
    }

    [Fact]
    public void DefaultSizeCapAllowsStoresAndExplicitSmallerCapStillApplies()
    {
        var options = new WalkerOptions();
        var size = WalkerOptions.DefaultMaxFileSizeBytes + 1;
        Assert.False(options.ExceedsSizeLimit("mail.pst", size));
        Assert.False(options.ExceedsSizeLimit("mail.OST", size));
        Assert.True(options.ExceedsSizeLimit("message.msg", size));
        Assert.True((options with { MaxFileSizeBytes = 100 }).ExceedsSizeLimit("mail.pst", size));
        Assert.True((options with { AllowLargeMailStores = false }).ExceedsSizeLimit("mail.pst", size));
        Assert.False((options with { MaxFileSizeBytes = 0 }).ExceedsSizeLimit("file.txt", size));
    }

    [Fact]
    public async Task MsgUnicodeHtmlAndAttachmentNamesAreExtractedWithoutPayloadReads()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("generated.msg");
        using (var root = RootStorage.Create(path))
        {
            WriteStream(root, "__substg1.0_0037001F", Encoding.Unicode.GetBytes("Résumé 東京\0"));
            WriteStream(root, "__substg1.0_10130102", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<p>Invoice 東京</p><script>\nunwanted\n</script><p>Second paragraph</p>")).ToArray());
            var attachment = root.CreateStorage("__attach_version1.0_#00000000");
            WriteStream(attachment, "__substg1.0_3707001F", Encoding.Unicode.GetBytes("contract.pdf\0"));
            // No attachment data stream exists: extracting names must not request it.
        }
        var lines = await ReadAsync(new MsgExtractor(), path);
        Assert.Contains(lines, line => line.Content == "Subject: Résumé 東京");
        Assert.Contains(lines, line => line.Content == "Invoice 東京");
        Assert.Contains(lines, line => line.Content == "Attachment: contract.pdf");
        Assert.DoesNotContain(lines, line => line.Content.Contains("unwanted", StringComparison.Ordinal));
    }

    [Fact]
    public void RtfDecompressionReadsLiteralAndDictionaryReferencesAndRejectsOversizedOutput()
    {
        // Dictionary offset 0 contains "{\\rtf1": a six-byte reference followed by a literal.
        var payload = new byte[] { 5, 0, 4, (byte)' ', 0x0d, 0x60 }; // terminator at dictionary offset 214
        var data = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)(data.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x75465A4C);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), OutlookRtfCompression.ComputeCrc(payload));
        payload.CopyTo(data, 16);
        Assert.Equal("{\\rtf1 ", Encoding.ASCII.GetString(OutlookRtfCompression.Decompress(data)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => OutlookRtfCompression.Decompress(data));
    }

    [Fact]
    public async Task MsgAnsiHeadersAndRtfOnlyBodyAreSearchable()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("rtf.msg");
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi RTF only invoice\par Second paragraph}");
        var data = new byte[16 + rtf.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)(data.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)rtf.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x414C454D);
        rtf.CopyTo(data, 16);
        using (var root = RootStorage.Create(path))
        {
            WriteStream(root, "__substg1.0_0037001E", Encoding.Latin1.GetBytes("Résumé\0"));
            WriteStream(root, "__substg1.0_10090102", data);
        }
        var lines = await ReadAsync(new MsgExtractor(), path);
        Assert.Contains(lines, line => line.Content == "Subject: Résumé");
        Assert.Contains(lines, line => line.Content == "RTF only invoice");
        Assert.Contains(lines, line => line.Content == "Second paragraph");
    }

    [Fact]
    public void RtfCorruptChecksumAndIncompleteInputAreRejected()
    {
        var data = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x75465A4C);
        data[16] = 1;
        Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => OutlookRtfCompression.Decompress(data)).Message);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), OutlookRtfCompression.ComputeCrc(data.AsSpan(16)));
        Assert.Contains("incomplete", Assert.Throws<InvalidDataException>(() => OutlookRtfCompression.Decompress(data)).Message);
    }

    [Fact]
    public async Task MidExtractionCancellationReleasesTheStoreHandle()
    {
        using var files = new OutlookTestFiles();
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new OutlookStoreExtractor().ExtractAsync(files.PathFor("bodies.pst"), cancelled.Token))
                cancelled.Cancel();
        });
        using var exclusive = new FileStream(files.PathFor("bodies.pst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void SemanticChunksCannotCombineTwoDifferentMessages()
    {
        var first = new MailMessageMetadata("1", "first", "from", "to", "", null, "Inbox", "stamp");
        var second = first with { Id = "2", Subject = "second" };
        var lines = new[]
        {
            new TextLine(1, "First message", new SourceAnchor(SourceAnchorKind.Email, "first", MailMessage: first)),
            new TextLine(2, "Second message", new SourceAnchor(SourceAnchorKind.Email, "second", MailMessage: second)),
        };
        var units = lines.Select((line, i) => ContentUnit.FromTextLine(i + 1, 1, line, "mail", "1")).ToArray();
        var chunks = new ContentUnitChunker().CreateChunks(units);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(first, chunks[0].Locator.MailMessage);
        Assert.Equal(second, chunks[1].Locator.MailMessage);
    }

    [Fact]
    public async Task StoppingStoreExtractionEarlyReleasesTheFileHandle()
    {
        using var files = new OutlookTestFiles();
        await foreach (var line in new OutlookStoreExtractor().ExtractAsync(files.PathFor("bodies.pst"), TestContext.Current.CancellationToken))
        {
            Assert.NotNull(line.Anchor!.MailMessage);
            break;
        }
        using var exclusive = new FileStream(files.PathFor("bodies.pst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    private static void WriteStream(Storage storage, string name, byte[] bytes)
    {
        using var stream = storage.CreateStream(name);
        stream.Write(bytes);
    }

    private static async Task<List<TextLine>> ReadAsync(ITextExtractor extractor, string path)
    {
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, TestContext.Current.CancellationToken)) lines.Add(line);
        return lines;
    }

    private static async Task<List<Hit>> HitsAsync(IAsyncEnumerable<Hit> source)
    {
        var hits = new List<Hit>();
        await foreach (var hit in source) hits.Add(hit);
        return hits;
    }
}
