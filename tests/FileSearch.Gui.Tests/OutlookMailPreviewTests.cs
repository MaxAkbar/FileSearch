using FileSearch.Core.Extractors;
using FileSearch.Core.Tests;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class OutlookMailPreviewTests
{
    [Fact]
    public async Task StandaloneEmlWithMessageMetadataUsesEmlTextAndRichPreviewRoutes()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("standalone.eml");
        await File.WriteAllTextAsync(path, """
            Subject: Standalone preview
            From: Alice <alice@example.test>
            To: Bob <bob@example.test>
            Date: Fri, 21 Nov 1997 09:55:06 -0600
            Content-Type: text/plain; charset=utf-8

            first body line
            matching body line
            """, TestContext.Current.CancellationToken);
        var extractor = new EmlExtractor();
        var lines = new List<TextLine>();
        await foreach (var line in extractor.ExtractAsync(path, TestContext.Current.CancellationToken))
            lines.Add(line);
        var selected = Assert.Single(lines, line => line.Content == "matching body line");
        var metadata = Assert.IsType<MailMessageMetadata>(selected.Anchor?.MailMessage);
        var service = new FilePreviewService(new ExtractorRegistry([extractor]));

        var text = await service.LoadMailMessagePreviewAsync(path, metadata, [selected.Number], 100,
            TestContext.Current.CancellationToken);
        Assert.Contains("Subject: Standalone preview", text, StringComparison.Ordinal);
        Assert.Contains($"► {selected.Number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)}", text,
            StringComparison.Ordinal);
        Assert.Contains("matching body line", text, StringComparison.Ordinal);

        var rich = await MailPreviewService.LoadAsync(path, metadata, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentPreviewKind.Html, rich.Kind);
        Assert.Contains("Standalone preview", rich.Text, StringComparison.Ordinal);
        Assert.Contains("matching body line", rich.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailPreviewReadsOnlyTheSelectedMessageAndUsesOriginalHitNumbers()
    {
        using var files = new OutlookTestFiles();
        var path = files.PathFor("bodies.pst");
        var lines = new List<TextLine>();
        await foreach (var line in new OutlookStoreExtractor().ExtractAsync(path, TestContext.Current.CancellationToken))
            lines.Add(line);
        var first = lines[0].Anchor!.MailMessage!;
        var service = new FilePreviewService(new ExtractorRegistry([new OutlookStoreExtractor()]));
        var preview = await service.LoadMailMessagePreviewAsync(path, first, [6], 1000, TestContext.Current.CancellationToken);
        Assert.Contains("Subject: original email", preview, StringComparison.Ordinal);
        Assert.Contains("►      6", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("FW: original email", preview, StringComparison.Ordinal);
        var last = lines[^1].Anchor!.MailMessage!;
        var secondPreview = await service.LoadMailMessagePreviewAsync(path, last, [lines[^1].Number], 1000, TestContext.Current.CancellationToken);
        Assert.Contains("Subject: FW: original email", secondPreview, StringComparison.Ordinal);
        Assert.Contains($"► {lines[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)}", secondPreview, StringComparison.Ordinal);
    }
}
