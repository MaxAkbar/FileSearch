using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class ContentUnitTests
{
    [Fact]
    public void FromTextLine_CreatesStableTextUnitWithLineLocator()
    {
        var line = new TextLine(12, "needle content");

        var unit = ContentUnit.FromTextLine(10, 20, line, "plain", "1");
        var repeated = ContentUnit.FromTextLine(11, 20, line, "plain", "1");

        Assert.Equal(10, unit.Id);
        Assert.Equal(20, unit.FileId);
        Assert.Equal(ContentUnitKind.Text, unit.Kind);
        Assert.Equal("needle content", unit.Text);
        Assert.Equal(64, unit.ContentHash.Length);
        Assert.Equal(unit.ContentHash, repeated.ContentHash);
        Assert.Equal("plain", unit.ExtractorId);
        Assert.Equal("1", unit.ExtractorVersion);
        Assert.Equal(12, unit.Locator.StartLine);
        Assert.Equal(12, unit.Locator.EndLine);
        Assert.Equal("line 12", unit.Locator.DisplayText);
    }

    [Fact]
    public void FromTextLine_MapsAnchoredOcrRegionToLocator()
    {
        var anchor = SourceAnchor.PdfOcrRegion(3, 10, 20, 30, 40, 100, 200);
        var line = new TextLine(7, "ocr needle", anchor);

        var unit = ContentUnit.FromTextLine(10, 20, line, "pdf", "1");

        Assert.Equal(ContentUnitKind.ImageOcrRegion, unit.Kind);
        Assert.Equal(3, unit.Locator.Page);
        Assert.Equal(7, unit.Locator.StartLine);
        Assert.Equal(10, unit.Locator.X);
        Assert.Equal(20, unit.Locator.Y);
        Assert.Equal(30, unit.Locator.Width);
        Assert.Equal(40, unit.Locator.Height);
        Assert.Equal(100, unit.Locator.SourceWidth);
        Assert.Equal(200, unit.Locator.SourceHeight);
        Assert.Contains("OCR region", unit.Locator.DisplayText, StringComparison.OrdinalIgnoreCase);
    }
}
