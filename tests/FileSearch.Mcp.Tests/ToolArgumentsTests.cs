using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Mcp;
using FileSearch.WindowsOcr;
using ModelContextProtocol;

namespace FileSearch.Mcp.Tests;

public sealed class ToolArgumentsTests
{
    [Theory]
    [InlineData(null, QueryMode.PlainText)]
    [InlineData("plain", QueryMode.PlainText)]
    [InlineData("Regex", QueryMode.Regex)]
    [InlineData("boolean", QueryMode.Boolean)]
    [InlineData("unified", QueryMode.Unified)]
    public void ParseMode_AcceptsCliSpellings(string? value, QueryMode expected)
    {
        Assert.Equal(expected, ToolArguments.ParseMode(value));
    }

    [Fact]
    public void ParseMode_Unknown_ThrowsMcpException()
    {
        var ex = Assert.Throws<McpException>(() => ToolArguments.ParseMode("fancy"));

        Assert.Contains("plain, regex, boolean, or unified", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, SearchTarget.Content)]
    [InlineData("files", SearchTarget.FileNames)]
    [InlineData("folders", SearchTarget.FolderNames)]
    [InlineData("names", SearchTarget.FileAndFolderNames)]
    public void ParseTarget_AcceptsCliSpellings(string? value, SearchTarget expected)
    {
        Assert.Equal(expected, ToolArguments.ParseTarget(value));
    }

    [Fact]
    public void ParseUtc_ParsesIsoDatesAsUtc()
    {
        var parsed = ToolArguments.ParseUtc("2026-06-09T13:00:00Z", "modifiedAfter");

        Assert.Equal(new DateTime(2026, 6, 9, 13, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void ParseUtc_Invalid_ThrowsMcpException()
    {
        Assert.Throws<McpException>(() => ToolArguments.ParseUtc("last tuesday", "modifiedAfter"));
    }

    [Fact]
    public void BuildWalkerOptions_NormalizesExtensions()
    {
        var options = ToolArguments.BuildWalkerOptions(
            null, null, ["cs", "*.MD"], null, includeHidden: false,
            modifiedAfter: null, modifiedBefore: null, excludeImageFiles: false);

        Assert.Contains(".cs", options.IncludeExtensions);
        Assert.Contains(".md", options.IncludeExtensions);
    }

    [Fact]
    public void BuildWalkerOptions_ExcludeImageFiles_AddsOcrExtensions()
    {
        var live = ToolArguments.BuildWalkerOptions(
            null, null, null, null, includeHidden: false,
            modifiedAfter: null, modifiedBefore: null, excludeImageFiles: true);
        var indexed = ToolArguments.BuildWalkerOptions(
            null, null, null, null, includeHidden: false,
            modifiedAfter: null, modifiedBefore: null, excludeImageFiles: false);

        foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
        {
            Assert.Contains(extension, live.ExcludeExtensions);
            Assert.DoesNotContain(extension, indexed.ExcludeExtensions);
        }
    }

    [Fact]
    public void BuildWalkerOptions_KeepsDefaultDirectoryExclusions()
    {
        var options = ToolArguments.BuildWalkerOptions(
            null, null, null, null, includeHidden: false,
            modifiedAfter: null, modifiedBefore: null, excludeImageFiles: false);

        Assert.Equal(WalkerOptions.DefaultExcludeDirectories, options.ExcludeDirectories);
    }

    [Fact]
    public void MergeBuildTimeExcludes_UnionsProfileExclusionsIntoRequest()
    {
        var options = new WalkerOptions
        {
            ExcludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".log" },
        };
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = new IndexProfile(
            Recursive: true,
            IncludeHidden: false,
            IncludeExtensions: empty,
            ExcludeExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg" },
            IncludeDirectories: empty,
            ExcludeDirectories: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" },
            EnableOcr: false);

        var merged = ToolArguments.MergeBuildTimeExcludes(options, profile);

        Assert.Contains(".log", merged.ExcludeExtensions);
        Assert.Contains(".png", merged.ExcludeExtensions);
        Assert.Contains(".jpg", merged.ExcludeExtensions);
        Assert.Contains("bin", merged.ExcludeDirectories);
        Assert.Contains(".git", merged.ExcludeDirectories);
        Assert.True(profile.Covers(merged), "merged request must satisfy the stored profile");
    }

    [Fact]
    public void BuildQuery_Invalid_ThrowsMcpExceptionWithReason()
    {
        var factory = new QueryFactory();

        var ex = Assert.Throws<McpException>(() =>
            ToolArguments.BuildQuery(factory, "[unclosed", QueryMode.Regex, caseSensitive: false));

        Assert.StartsWith("Invalid query:", ex.Message, StringComparison.Ordinal);
    }
}
