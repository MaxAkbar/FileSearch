using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class WindowsIFilterExtractionOptionsTests
{
    [Fact]
    public void AllowsPathBlocksRiskyShellExtensions()
    {
        var options = new WindowsIFilterExtractionOptions();

        Assert.False(options.AllowsPath(@"C:\docs\shortcut.lnk"));
        Assert.False(options.AllowsPath(@"C:\docs\saved-search.search-ms"));
        Assert.True(options.AllowsPath(@"C:\docs\report.pdf"));
    }

    [Fact]
    public void AllowsPathHonorsOptionalAllowList()
    {
        var options = new WindowsIFilterExtractionOptions();
        options.AllowedExtensions.Add(".docx");

        Assert.True(options.AllowsPath(@"C:\docs\report.docx"));
        Assert.False(options.AllowsPath(@"C:\docs\report.pdf"));
    }
}
