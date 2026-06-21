using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class SourceOpenTargetBuilderTests
{
    [Fact]
    public void TryCreate_BuildsVisualStudioCodeLineTarget()
    {
        var hit = new Hit(
            @"C:\docs\report.cs",
            12,
            "needle",
            Array.Empty<MatchSpan>(),
            Locator: new SourceLocator(StartLine: 12, EndLine: 12, Column: 4));

        var target = SourceOpenTargetBuilder.TryCreate(@"C:\docs\report.cs", hit, @"C:\Tools\Code.exe");

        Assert.NotNull(target);
        Assert.Equal(SourceOpenTargetKind.VisualStudioCode, target.Kind);
        Assert.Equal(@"C:\Tools\Code.exe", target.FileName);
        Assert.Equal(new[] { "-g", @"C:\docs\report.cs:12:4" }, target.Arguments);
        Assert.Null(target.Uri);
    }

    [Fact]
    public void TryCreate_BuildsPdfPageUriTarget()
    {
        var hit = new Hit(
            @"C:\docs\scan.pdf",
            0,
            "needle",
            Array.Empty<MatchSpan>(),
            Locator: new SourceLocator(Page: 5));

        var target = SourceOpenTargetBuilder.TryCreate(@"C:\docs\scan.pdf", hit, visualStudioCodePath: null);

        Assert.NotNull(target);
        Assert.Equal(SourceOpenTargetKind.PdfPageUri, target.Kind);
        Assert.Contains("#page=5", target.Uri, StringComparison.Ordinal);
    }
}
