using FileSearch.Mcp;

namespace FileSearch.Mcp.Tests;

public sealed class McpServerRailsTests
{
    [Fact]
    public void Parse_NoArgsNoEnvironment_UsesDefaults()
    {
        var rails = McpServerRails.Parse([], null);

        Assert.Empty(rails.ExplicitRoots);
        Assert.False(rails.AllowAnyRoot);
    }

    [Fact]
    public void Parse_RepeatedRootArguments_CollectsCanonicalRoots()
    {
        var rails = McpServerRails.Parse(
            ["--root", @"C:\One", "--root", @"C:\Two\"], null);

        Assert.Equal([@"C:\One", @"C:\Two"], rails.ExplicitRoots);
    }

    [Fact]
    public void Parse_DuplicateRootsDifferingByCase_AreDeduplicated()
    {
        var rails = McpServerRails.Parse(
            ["--root", @"C:\One", "--root", @"c:\one\"], null);

        Assert.Single(rails.ExplicitRoots);
    }

    [Fact]
    public void Parse_RelativeRoot_IsResolvedAgainstWorkingDirectory()
    {
        var rails = McpServerRails.Parse(["--root", "."], null);

        Assert.Equal(
            McpServerRails.CanonicalizeRoot(Environment.CurrentDirectory),
            Assert.Single(rails.ExplicitRoots));
    }

    [Fact]
    public void Parse_EnvironmentRoots_UsedOnlyWithoutArguments()
    {
        var environment = @"C:\EnvOne" + Path.PathSeparator + @"C:\EnvTwo";

        var fromEnvironment = McpServerRails.Parse([], environment);
        var fromArguments = McpServerRails.Parse(["--root", @"C:\Arg"], environment);

        Assert.Equal([@"C:\EnvOne", @"C:\EnvTwo"], fromEnvironment.ExplicitRoots);
        Assert.Equal([@"C:\Arg"], fromArguments.ExplicitRoots);
    }

    [Fact]
    public void Parse_AllowAnyRootFlag_IsRecognized()
    {
        var rails = McpServerRails.Parse(["--allow-any-root"], null);

        Assert.True(rails.AllowAnyRoot);
    }

    [Fact]
    public void Parse_RootWithoutValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpServerRails.Parse(["--root"], null));
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpServerRails.Parse(["--frobnicate"], null));

        Assert.Contains("--frobnicate", ex.Message, StringComparison.Ordinal);
    }
}
