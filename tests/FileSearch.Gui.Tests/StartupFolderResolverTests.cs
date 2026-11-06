using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class StartupFolderResolverTests
{
    [Fact]
    public void AppStartupOptionsParse_MissingLauncherFolderDoesNotSearchAnotherFolder()
    {
        var options = AppStartupOptions.Parse(["--query", "invoice", "--path", @"C:\Gone"], _ => false);
        Assert.Null(options.QueryText);
        Assert.Null(options.StartupFolder);
    }

    [Fact]
    public void AppStartupOptionsParse_PreservesLauncherQueryAndSelectionAcrossIpc()
    {
        const string root = @"C:\Work Files";
        const string selected = @"C:\Work Files\report.txt";
        const string query = "type:txt \"annual report\" & $(literal)";
        var options = AppStartupOptions.Parse(
            ["--query", query, "--path", root, "--target", "files", "--select", selected], path => path == root);

        var forwarded = JsonSerializer.Deserialize<AppStartupOptions>(JsonSerializer.Serialize(options));
        Assert.Equal(options, forwarded);
        Assert.Equal(query, options.QueryText);
        Assert.Equal(root, options.StartupFolder);
        Assert.Equal(selected, options.SelectedPath);
        Assert.Equal(SearchTarget.FileNames, options.SearchTarget);
    }

    [Fact]
    public void AppStartupOptionsParse_OldActivationPayloadKeepsWorking()
    {
        var options = JsonSerializer.Deserialize<AppStartupOptions>("{\"StartInBackground\":false,\"StartupFolder\":null}");
        Assert.NotNull(options);
        Assert.Null(options.QueryText);
        Assert.Equal(SearchTarget.Content, options.SearchTarget);
    }

    [Theory]
    [InlineData("--query")]
    [InlineData("--path")]
    [InlineData("--target")]
    [InlineData("--select")]
    public void AppStartupOptionsParse_IncompleteLauncherArgumentDoesNotCrash(string argument)
    {
        var options = AppStartupOptions.Parse([argument], _ => false);
        Assert.Null(options.QueryText);
        Assert.Null(options.StartupFolder);
    }

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
