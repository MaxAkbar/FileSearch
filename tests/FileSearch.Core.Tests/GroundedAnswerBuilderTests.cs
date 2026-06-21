using FileSearch.Core.Engine;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class GroundedAnswerBuilderTests
{
    [Fact]
    public void Build_CreatesExtractiveDraftWithCitations()
    {
        var builder = new GroundedAnswerBuilder();
        var locator = new SourceLocator(Page: 2, StartLine: 8, EndLine: 8);
        var evidence = new[]
        {
            new GroundedAnswerEvidence(
                @"C:\docs\policy.pdf",
                SearchRank: 2,
                HitRank: 1,
                "termination clause requires thirty days notice",
                LineNumber: 8,
                Score: 0.5,
                Locator: locator),
            new GroundedAnswerEvidence(
                @"C:\docs\summary.txt",
                SearchRank: 1,
                HitRank: 1,
                "notice terms overview",
                LineNumber: 4,
                Score: 0.8,
                Locator: new SourceLocator(StartLine: 4, EndLine: 4)),
        };

        var draft = builder.Build("termination notice", evidence);

        Assert.Equal("termination notice", draft.Query);
        Assert.Equal(2, draft.Citations.Count);
        Assert.Equal(@"C:\docs\summary.txt", draft.Citations[0].Path);
        Assert.Equal("line 4", draft.Citations[0].Location);
        Assert.Equal(@"C:\docs\policy.pdf", draft.Citations[1].Path);
        Assert.Equal("page 2, line 8", draft.Citations[1].Location);
        Assert.Contains("extractive, local evidence only", draft.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[1]", draft.Markdown, StringComparison.Ordinal);
        Assert.Contains("notice terms overview", draft.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LimitsAndTrimsCitations()
    {
        var builder = new GroundedAnswerBuilder();
        var evidence = Enumerable.Range(1, 5)
            .Select(index => new GroundedAnswerEvidence(
                $@"C:\docs\{index}.txt",
                index,
                1,
                new string((char)('a' + index), 500)))
            .ToArray();

        var draft = builder.Build(
            "needle",
            evidence,
            new GroundedAnswerOptions(MaxCitations: 2, MaxCitationCharacters: 120));

        Assert.Equal(2, draft.Citations.Count);
        Assert.All(draft.Citations, citation => Assert.True(citation.Text.Length <= 120));
        Assert.Equal(@"C:\docs\1.txt", draft.Citations[0].Path);
        Assert.Equal(@"C:\docs\2.txt", draft.Citations[1].Path);
    }
}
