using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSearch.Core.Extractors;
using OpenMcdf;

namespace FileSearch.Core.Tests;

public sealed class MailSentMetadataTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MsgSentDateUsesSubmitPropertyWithoutFallingBackToDelivery(bool includeSubmit, bool includeDelivery)
    {
        using var files = new OutlookTestFiles();
        string path = files.PathFor("sent-metadata.msg");
        var submitted = new DateTime(2026, 1, 2, 23, 59, 59, DateTimeKind.Utc);
        var delivered = new DateTime(2026, 1, 3, 0, 0, 3, DateTimeKind.Utc);
        CreateMsg(path, includeSubmit ? submitted.ToFileTimeUtc() : null, includeDelivery ? delivered.ToFileTimeUtc() : null);

        var message = OutlookMailReader.ReadMsg(path);
        Assert.Equal(includeSubmit ? submitted : null, message.Metadata.SentUtc);
        Assert.Equal(includeSubmit ? submitted : includeDelivery ? delivered : null, message.Metadata.DateUtc);
        await foreach (var line in new MsgExtractor().ExtractAsync(path, TestContext.Current.CancellationToken))
            Assert.Equal(message.Metadata.SentUtc, line.Anchor?.MailMessage?.SentUtc);
    }

    [Fact]
    public void InvalidSubmitPropertyDoesNotTurnDeliveryIntoSentDate()
    {
        using var files = new OutlookTestFiles();
        string path = files.PathFor("invalid-submit.msg");
        var delivered = new DateTime(2026, 1, 3, 0, 0, 3, DateTimeKind.Utc);
        CreateMsg(path, long.MaxValue, delivered.ToFileTimeUtc());
        var message = OutlookMailReader.ReadMsg(path);
        Assert.Null(message.Metadata.SentUtc);
        Assert.Equal(delivered, message.Metadata.DateUtc);
    }

    [Theory]
    [InlineData("bodies.pst", "0.1.1.0:0", "original email", "2017-08-30T19:26:03Z", "2017-08-30T19:26:04.2894527Z")]
    [InlineData("sample.ost", "0.0.4.3:0", "xx", "2011-09-19T20:54:09Z", "2011-09-19T20:54:10.71875Z")]
    public async Task NativeStoreFixtureKeepsSubmittedTimeSeparateFromReceivedDisplayDate(
        string fileName, string messageId, string subject, string sent, string received)
    {
        using var files = new OutlookTestFiles();
        var selected = new List<TextLine>();
        await foreach (var line in new OutlookStoreExtractor().ExtractAsync(files.PathFor(fileName),
            TestContext.Current.CancellationToken))
        {
            if (line.Anchor?.MailMessage?.Id == messageId)
                selected.Add(line);
        }
        Assert.NotEmpty(selected);
        var expectedSent = ParseUtc(sent);
        var expectedReceived = ParseUtc(received);
        Assert.NotEqual(expectedSent, expectedReceived);
        Assert.All(selected, line =>
        {
            var metadata = line.Anchor!.MailMessage!;
            Assert.Equal(subject, metadata.Subject);
            Assert.Equal(expectedSent, metadata.SentUtc);
            Assert.Equal(expectedReceived, metadata.DateUtc);
            Assert.Equal(DateTimeKind.Utc, metadata.SentUtc!.Value.Kind);
            Assert.NotNull(metadata.StoreFingerprint);
        });
        var first = selected[0].Anchor!.MailMessage!;
        var reopened = OutlookMailReader.ReadMessage(files.PathFor(fileName), first, TestContext.Current.CancellationToken);
        Assert.Equal(expectedSent, reopened.Metadata.SentUtc);
        Assert.Equal(expectedReceived, reopened.Metadata.DateUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StorePreviewCopyUsesOnlyGenuineSentDateInEmlHeader(bool hasSentDate)
    {
        using var files = new OutlookTestFiles();
        string path = files.PathFor("bodies.pst");
        var metadata = OutlookMailReader.ReadStore(path, NullExtractionIssueSink.Instance,
            TestContext.Current.CancellationToken).First().Metadata;
        Assert.NotEqual(metadata.SentUtc, metadata.DateUtc);
        if (!hasSentDate)
            metadata = metadata with { SentUtc = null };
        string output = files.PathFor("sent-preview.eml");

        await OutlookMailReader.WriteMessagePreviewAsync(path, metadata, output, TestContext.Current.CancellationToken);
        var lines = new List<TextLine>();
        await foreach (var line in new EmlExtractor().ExtractAsync(output, TestContext.Current.CancellationToken))
            lines.Add(line);

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Equal(metadata.SentUtc, line.Anchor?.MailMessage?.SentUtc));
        Assert.Contains(lines, line => line.Content.StartsWith("Date: ", StringComparison.Ordinal));
    }

    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void CreateMsg(string path, long? submit, long? delivery)
    {
        using var root = RootStorage.Create(path);
        using (var subject = root.CreateStream("__substg1.0_0037001F"))
            subject.Write(Encoding.Unicode.GetBytes("Sent metadata\0"));
        using (var body = root.CreateStream("__substg1.0_1000001F"))
            body.Write(Encoding.Unicode.GetBytes("Searchable message\0"));

        var entries = new List<(ushort Id, long Value)>();
        if (submit is { } submitted) entries.Add((0x0039, submitted));
        if (delivery is { } delivered) entries.Add((0x0E06, delivered));
        byte[] properties = new byte[32 + entries.Count * 16];
        for (int i = 0; i < entries.Count; i++)
        {
            var slot = properties.AsSpan(32 + i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, ((uint)entries[i].Id << 16) | 0x0040);
            BinaryPrimitives.WriteInt64LittleEndian(slot[8..], entries[i].Value);
        }
        using var propertyStream = root.CreateStream("__properties_version1.0");
        propertyStream.Write(properties);
    }
}
