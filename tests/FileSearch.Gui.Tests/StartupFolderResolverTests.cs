using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class StartupFolderResolverTests
{
    [Fact]
    public void ResolveFolderPath_ReturnsFirstExistingFolder()
    {
        const string firstInvalid = @"C:\Missing";
        const string valid = @"C:\Work\Project";

        var result = StartupFolderResolver.ResolveFolderPath(
            new[] { firstInvalid, valid },
            path => path == valid);

        Assert.Equal(valid, result);
    }

    [Fact]
    public void ResolveFolderPath_TrimsExplorerQuotes()
    {
        const string valid = @"C:\Work\Project";

        var result = StartupFolderResolver.ResolveFolderPath(
            new[] { $"\"{valid}\"" },
            path => path == valid);

        Assert.Equal(valid, result);
    }

    [Fact]
    public void ResolveFolderPath_ReturnsNullForMissingOrInvalidFolder()
    {
        var result = StartupFolderResolver.ResolveFolderPath(
            new[] { "", "   ", @"C:\Missing" },
            _ => false);

        Assert.Null(result);
    }
}
