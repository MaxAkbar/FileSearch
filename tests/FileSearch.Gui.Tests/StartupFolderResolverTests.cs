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

    [Fact]
    public void AppStartupOptionsParse_ReturnsDefaultsForNoArgs()
    {
        var options = AppStartupOptions.Parse(Array.Empty<string>(), _ => false);

        Assert.False(options.StartInBackground);
        Assert.Null(options.StartupFolder);
    }

    [Fact]
    public void AppStartupOptionsParse_KeepsExplorerFolderArgument()
    {
        const string folder = @"C:\Work";

        var options = AppStartupOptions.Parse(new[] { $"\"{folder}\"" }, path => path == folder);

        Assert.False(options.StartInBackground);
        Assert.Equal(folder, options.StartupFolder);
    }

    [Fact]
    public void AppStartupOptionsParse_SupportsBackgroundArgument()
    {
        var options = AppStartupOptions.Parse(new[] { "--background" }, _ => false);

        Assert.True(options.StartInBackground);
        Assert.Null(options.StartupFolder);
    }

    [Fact]
    public void AppStartupOptionsParse_SupportsBackgroundPlusFolder()
    {
        const string folder = @"C:\Work";

        var options = AppStartupOptions.Parse(new[] { "--background", folder }, path => path == folder);

        Assert.True(options.StartInBackground);
        Assert.Equal(folder, options.StartupFolder);
    }

    [Fact]
    public void AppStartupOptionsParse_IgnoresUnknownFlags()
    {
        const string folder = @"C:\Work";

        var options = AppStartupOptions.Parse(new[] { "--unknown", folder }, path => path == folder);

        Assert.False(options.StartInBackground);
        Assert.Equal(folder, options.StartupFolder);
    }
}
