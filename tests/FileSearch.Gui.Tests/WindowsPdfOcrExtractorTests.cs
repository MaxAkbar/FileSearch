using FileSearch.WindowsOcr;

namespace FileSearch.Gui.Tests;

public sealed class WindowsPdfOcrExtractorTests
{
    [Fact]
    public void GetOcrPageNumbersReturnsOnlyPagesWithoutNativeText()
    {
        var pages = WindowsPdfOcrExtractor.GetOcrPageNumbers(
            new HashSet<int> { 1, 3 },
            pageCount: 4,
            maxPdfPages: 0);

        Assert.Equal(new[] { 2, 4 }, pages);
    }

    [Fact]
    public void GetOcrPageNumbersRespectsConfiguredPageLimit()
    {
        var pages = WindowsPdfOcrExtractor.GetOcrPageNumbers(
            new HashSet<int> { 1 },
            pageCount: 5,
            maxPdfPages: 3);

        Assert.Equal(new[] { 2, 3 }, pages);
    }
}
