using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class RegexTesterViewModelTests
{
    [Fact]
    public void ValidPatternReportsMatchesGroupsAndReplacementPreview()
    {
        var vm = new RegexTesterViewModel(@"(?<word>[a-z]+)-(\d+)", matchCase: false, "abc-123\nDEF-7");

        Assert.True(vm.IsPatternValid);
        Assert.Equal(2, vm.Matches.Count);
        Assert.Equal("2 matches", vm.MatchSummaryText);

        var first = vm.Matches[0];
        Assert.Equal("abc-123", first.Value);
        Assert.Contains(first.Groups, group =>
            group.Name == "word" &&
            group.Success &&
            group.Value == "abc");

        vm.Replacement = "${word}";

        Assert.Equal("abc\nDEF", vm.ReplacementPreview);
    }

    [Fact]
    public void InvalidPatternReportsErrorAndClearsMatches()
    {
        var vm = new RegexTesterViewModel("needle", matchCase: false, "needle");

        Assert.Single(vm.Matches);

        vm.Pattern = "(";

        Assert.False(vm.IsPatternValid);
        Assert.Empty(vm.Matches);
        Assert.Contains("Invalid pattern", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("No matches", vm.MatchSummaryText);
    }

    [Fact]
    public void MultilineOptionUpdatesAnchoredMatches()
    {
        var vm = new RegexTesterViewModel("^two", matchCase: false, "one\ntwo");

        Assert.Empty(vm.Matches);

        vm.Multiline = true;

        var match = Assert.Single(vm.Matches);
        Assert.Equal("two", match.Value);
    }
}
