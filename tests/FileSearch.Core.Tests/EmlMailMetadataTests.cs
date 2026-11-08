using System.Runtime.CompilerServices;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class EmlMailMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FileSearchEmlMetadata-" + Guid.NewGuid().ToString("N"));

    public EmlMailMetadataTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task EveryLineRetainsTopLevelMessageMetadataWithoutReadingBodyHeaders()
    {
        var lines = await ReadAsync("""
            Subject: Quarterly report
            From: Alice <alice@example.test>
            To: Bob <bob@example.test>
            Date: Fri, 21 Nov 1997 09:55:06 -0600
            Content-Type: text/plain; charset=utf-8

            A body paragraph
            From: impostor@example.test
            Subject: This is body text
            Date: Fri, 21 Nov 2025 09:55:06 +0000
            """);

        Assert.Contains(lines, line => line.Content == "From: impostor@example.test");
        var metadata = Assert.IsType<MailMessageMetadata>(lines[0].Anchor?.MailMessage);
        Assert.Equal("Quarterly report", metadata.Subject);
        Assert.Equal("Alice <alice@example.test>", metadata.From);
        Assert.Equal("Bob <bob@example.test>", metadata.To);
        Assert.Equal(new DateTime(1997, 11, 21, 15, 55, 6, DateTimeKind.Utc), metadata.DateUtc);
        Assert.Equal(metadata.DateUtc, metadata.SentUtc);
        Assert.Equal("eml", metadata.Id);
        Assert.Null(metadata.StoreFingerprint);
        Assert.Empty(metadata.Folder);
        Assert.All(lines, line =>
        {
            Assert.Equal(SourceAnchorKind.Email, line.Anchor?.Kind);
            Assert.Equal(line.Number, line.Anchor?.Line);
            Assert.Same(metadata, line.Anchor?.MailMessage);
        });
    }

    [Fact]
    public async Task FoldedEncodedHeadersAndRepeatedRecipientsAreDecoded()
    {
        var lines = await ReadAsync("""
            Subject: =?UTF-8?Q?Quarterly_?=
             =?UTF-8?B?4pyT?=
            From: =?UTF-8?Q?Jos=C3=A9?= <jose@example.test>
            To: =?UTF-8?Q?Zo=C3=AB?= <zoe@example.test>,
             Bob <bob@example.test>
            To: Third <third@example.test>
            Cc: First <first@example.test>
             , Second <second@example.test>
            Cc: Last <last@example.test>

            body
            """);

        var metadata = lines[0].Anchor!.MailMessage!;
        Assert.Equal("Quarterly ✓", metadata.Subject);
        Assert.Equal("José <jose@example.test>", metadata.From);
        Assert.Equal("Zoë <zoe@example.test>, Bob <bob@example.test>; Third <third@example.test>", metadata.To);
        Assert.Equal("First <first@example.test> , Second <second@example.test>; Last <last@example.test>", metadata.Cc);
        Assert.Contains(lines, line => line.Content == "Subject: Quarterly ✓");
    }

    [Theory]
    [InlineData("Fri, 21 Nov 1997 09:55:06 +0530", "1997-11-21T04:25:06Z")]
    [InlineData("Fri, 21 Nov 1997 09:55:06 +05:30", "1997-11-21T04:25:06Z")]
    [InlineData("Fri, 21 Nov 1997 09:55:06 GMT", "1997-11-21T09:55:06Z")]
    [InlineData("1997-11-21T09:55:06-06:00", "1997-11-21T15:55:06Z")]
    [InlineData("not a date", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task MessageDatesNormalizeToUtcAndInvalidDatesRemainUnknown(string? header, string? expected)
    {
        var lines = await ReadAsync("Subject: Date test\r\n" +
            (header is null ? string.Empty : "Date: " + header + "\r\n") + "\r\nbody");
        var date = lines[0].Anchor!.MailMessage!.DateUtc;
        Assert.Equal(date, lines[0].Anchor!.MailMessage!.SentUtc);
        Assert.Equal(expected is null ? null : DateTime.Parse(expected, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), date);
        if (date is not null)
            Assert.Equal(DateTimeKind.Utc, date.Value.Kind);
    }

    [Fact]
    public async Task MissingAndMalformedHeadersPreserveSearchableBody()
    {
        var lines = await ReadAsync("Subject: =?UTF-8?B?invalid-base64?=\nDate: invalid\n\nfirst body line\nsecond body line");
        var metadata = lines[0].Anchor!.MailMessage!;
        Assert.Equal("=?UTF-8?B?invalid-base64?=", metadata.Subject);
        Assert.Empty(metadata.From);
        Assert.Empty(metadata.To);
        Assert.Empty(metadata.Cc);
        Assert.Null(metadata.DateUtc);
        Assert.Contains(lines, line => line.Content == "first body line");
        Assert.Contains(lines, line => line.Content == "second body line");
    }

    [Fact]
    public async Task MetadataValuesAndHeaderScanningAreBounded()
    {
        var lines = await ReadAsync("Subject: " + new string('s', 20_000) + "\nTo: " + new string('t', 20_000) +
            "\nTo: more@example.test\nX-Padding: " + new string('p', 30_000) + "\nFrom: beyond-limit@example.test\n\nbody");
        var metadata = lines[0].Anchor!.MailMessage!;
        Assert.InRange(metadata.Subject.Length, 1, 4096);
        Assert.InRange(metadata.To.Length, 1, 4096);
        Assert.Empty(metadata.From);
        Assert.Contains(lines, line => line.Content == "body");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmbeddedOcrRetainsMessageIdentityAndExistingGeometry(bool hasAnchor)
    {
        var ocr = new GeometryOcr(hasAnchor);
        var lines = await ReadAsync("""
            Subject: Embedded image
            From: alice@example.test
            Content-Type: multipart/mixed; boundary="parts"

            --parts
            Content-Type: text/plain

            body
            --parts
            Content-Type: image/png; name="scan.png"
            Content-Transfer-Encoding: base64

            AQIDBA==
            --parts--
            """, new EmlExtractor(ocr), enableOcr: true);
        var line = Assert.Single(lines, line => line.Content == "recognized words");
        Assert.Equal(lines.Count, line.Number);
        Assert.Same(lines[0].Anchor!.MailMessage, line.Anchor?.MailMessage);
        Assert.Equal("scan.png", line.Anchor?.MemberPath);
        Assert.Equal("scan.png", line.Anchor?.Section);
        if (hasAnchor)
        {
            Assert.Equal(ocr.Anchor! with { MailMessage = lines[0].Anchor!.MailMessage }, line.Anchor);
        }
        else
        {
            Assert.Equal(SourceAnchorKind.Email, line.Anchor?.Kind);
        }
    }

    [Fact]
    public async Task StandaloneIdentityIsStableAcrossExtractionAndIndependentOfInternetMessageId()
    {
        const string content = "Subject: same message\nMessage-ID: <external@example.test>\n\nbody";
        var first = await ReadAsync(content);
        var second = await ReadAsync(content);
        Assert.Equal(first[0].Anchor!.MailMessage, second[0].Anchor!.MailMessage);
        Assert.Null(first[0].Anchor!.MailMessage!.StoreFingerprint);
    }

    private async Task<List<TextLine>> ReadAsync(string content, EmlExtractor? extractor = null, bool enableOcr = false)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".eml");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        var lines = new List<TextLine>();
        await foreach (var line in (extractor ?? new EmlExtractor()).ExtractAsync(path,
            new TextExtractionContext(EnableOcr: enableOcr), TestContext.Current.CancellationToken))
            lines.Add(line);
        return lines;
    }

    private sealed class GeometryOcr(bool hasAnchor) : IEmbeddedImageOcrService
    {
        public SourceAnchor? Anchor { get; private set; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(byte[] imageBytes, EmbeddedImageOcrRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Anchor = hasAnchor ? SourceAnchor.EmbeddedOcrRegion(request.AnchorKind, request.Label, request.MemberPath,
                10, 20, 30, 40, 500, 600, section: request.Section) : null;
            yield return new TextLine(1, "recognized words", Anchor);
        }
    }
}
