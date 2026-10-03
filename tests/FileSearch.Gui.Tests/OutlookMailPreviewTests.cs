using FileSearch.Core.Extractors;
using FileSearch.Core.Tests;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class OutlookMailPreviewTests
{
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
