using System.Windows;
using FileSearch.Core.Engine;
using FileSearch.Gui.Controls;

namespace FileSearch.Gui.Tests;

public sealed class PdfOcrRegionTests
{
    [Theory]
    [InlineData(1000, 750, 400, 300)]
    [InlineData(750, 1000, 300, 400)]
    public void ExistingRenderedCoordinatesNormalizeWithoutAnotherPageRotation(int sourceWidth, int sourceHeight,
        double pageWidth, double pageHeight)
    {
        var location = new SourceLocator(Page: 2, X: sourceWidth / 5, Y: sourceHeight / 5,
            Width: sourceWidth / 10, Height: sourceHeight / 10, SourceWidth: sourceWidth, SourceHeight: sourceHeight);
        var region = FileDocumentPreview.PdfOcrRegion(location, new Size(pageWidth, pageHeight));
        Assert.Equal(new Rect(.2, .2, .1, .1), region);
    }

    [Fact]
    public void MissingNegativeEmptyAndOverflowingSourceRectanglesAreRejected()
    {
        var valid = new SourceLocator(Page: 1, X: 20, Y: 30, Width: 100, Height: 60, SourceWidth: 1000, SourceHeight: 750);
        SourceLocator[] invalid =
        [
            valid with { X = null }, valid with { Y = null }, valid with { Width = null }, valid with { Height = null },
            valid with { SourceWidth = null }, valid with { SourceHeight = null },
            valid with { X = -1 }, valid with { Y = -1 }, valid with { Width = -1 }, valid with { Height = -1 },
            valid with { Width = 0 }, valid with { Height = 0 },
            valid with { SourceWidth = 0 }, valid with { SourceHeight = 0 },
            valid with { X = 999, Width = 2 }, valid with { Y = 749, Height = 2 },
            valid with { X = int.MaxValue, Width = int.MaxValue, SourceWidth = int.MaxValue },
            valid with { Y = int.MaxValue, Height = int.MaxValue, SourceHeight = int.MaxValue },
        ];
        Assert.All(invalid, location => Assert.Null(FileDocumentPreview.PdfOcrRegion(location, new Size(400, 300))));
    }

    [Fact]
    public void NonfiniteEmptyAndMismatchedPageDimensionsAreRejected()
    {
        var location = new SourceLocator(Page: 1, X: 20, Y: 30, Width: 100, Height: 60, SourceWidth: 1000, SourceHeight: 750);
        Size[] invalid =
        [
            new(0, 300), new(400, 0), Size.Empty,
            new(double.NaN, 300), new(400, double.NaN),
            new(double.PositiveInfinity, 300), new(400, double.PositiveInfinity),
            new(400, 400), new(300, 400),
        ];
        Assert.All(invalid, size => Assert.Null(FileDocumentPreview.PdfOcrRegion(location, size)));
    }

    [Fact]
    public void OrdinaryBitmapRoundingAndBoundaryAlignedRectanglesRemainValid()
    {
        var location = new SourceLocator(Page: 1, X: 1501, Y: 1900, Width: 100, Height: 100, SourceWidth: 1601, SourceHeight: 2000);
        var region = FileDocumentPreview.PdfOcrRegion(location, new Size(400, 500));
        Assert.NotNull(region);
        Assert.Equal(1, region.Value.Right, precision: 12);
        Assert.Equal(1, region.Value.Bottom, precision: 12);
        Assert.Equal(100d / 1601, region.Value.Width);
        Assert.Equal(.05, region.Value.Height);
    }
}
