using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;

namespace FileSearch.Core.Tests;

public sealed class QuickSearchNameRankingTests
{
    [Fact]
    public void ExactFullNameAndStemStayAheadOfWeakerMatchesAtAnyDepth()
    {
        var ranking = QuickSearchNameRanking.Compile("invoice.pdf");
        string deep = @"C:\Work\" + string.Join("\\", Enumerable.Repeat("nested", 100));
        Assert.True(ranking.Score(deep + @"\invoice.pdf") > ranking.Score(@"C:\invoice.pdf.backup"));
        Assert.True(ranking.Score(deep + @"\invoice.pdf.backup") > ranking.Score(@"C:\invoice.pdf.notes.backup"));

        var stem = QuickSearchNameRanking.Compile("report");
        Assert.True(stem.Score(deep + @"\report.docx") > stem.Score(@"C:\report-final.docx"));
        Assert.True(stem.Score(deep + @"\report") > stem.Score(@"C:\report.docx"));
    }

    [Fact]
    public void WholePhrasePrefixPrecedesScatteredOwnNameTermsThenParentOnlyTerms()
    {
        var ranking = QuickSearchNameRanking.Compile("project plan");
        double prefix = ranking.Score(@"C:\project planning notes.md");
        double scattered = ranking.Score(@"C:\plan for project.md");
        double parent = ranking.Score(@"C:\project\plan.md");
        double pathOnly = ranking.Score(@"C:\project\plan\notes.md");
        Assert.True(prefix > scattered);
        Assert.True(scattered > parent);
        Assert.True(parent > pathOnly);
    }

    [Fact]
    public void DirectoryDotsAreNotTreatedAsAFileExtension()
    {
        var ranking = QuickSearchNameRanking.Compile("client");
        Assert.True(ranking.Score(@"C:\Work\client.txt") > ranking.Score(@"C:\Work\client.v2", isDirectory: true));
        Assert.Equal(ranking.Score(@"C:\Work\client.v2", isDirectory: true),
            ranking.Score(@"C:\Work\client.v2\", isDirectory: true));
        Assert.True(ranking.Score(@"C:\Work\client", isDirectory: true) > ranking.Score(@"C:\Work\client.txt"));
    }

    [Fact]
    public void PunctuationOperatorsAndQuotesRemainLiteralRankingText()
    {
        var punctuation = QuickSearchNameRanking.Compile("C++ [draft]");
        Assert.True(punctuation.Score(@"C:\C++ [draft].txt") > punctuation.Score(@"C:\C draft.txt"));
        var operation = QuickSearchNameRanking.Compile("OR");
        Assert.True(operation.Score(@"C:\OR.txt") > operation.Score(@"C:\Order.txt"));
        var quoted = QuickSearchNameRanking.Compile("\"report\"");
        Assert.True(quoted.Score("C:\\\"report\".txt") > quoted.Score(@"C:\report.txt"));
        Assert.Equal(QuickSearchNameRanking.Compile("report").Score(@"C:\report.txt"),
            QuickSearchNameRanking.Compile("REPORT").Score(@"C:\REPORT.TXT"));
    }

    [Fact]
    public void DriveAndNetworkRootsDoNotAddDifferentDepthPenalties()
    {
        var ranking = QuickSearchNameRanking.Compile("report");
        Assert.Equal(ranking.Score(@"C:\Work\report.txt"), ranking.Score(@"\\server\share\Work\report.txt"));
        Assert.Equal(ranking.Score(@"C:\Work\report.txt"), ranking.ScoreName("report.txt", isDirectory: false, depth: 2));
        string deep = string.Join("\\", Enumerable.Repeat("nested", 100));
        Assert.Equal(ranking.Score(@"C:\" + deep + @"\report.txt"), ranking.Score(@"C:\extra\" + deep + @"\report.txt"));
    }

    [Fact]
    public void AQueryWithoutLiteralTermsHasNoRankingSignal()
    {
        Assert.Equal(0, QuickSearchNameRanking.Compile("   ").Score(@"C:\report.txt"));
        Assert.Equal(0, QuickSearchNameRanking.Compile("report").Score(""));
    }

    [Fact]
    public void ContentOnlyCandidatesWithoutAllLiteralPathTermsGetNoNameSignal()
    {
        var ranking = QuickSearchNameRanking.Compile("project plan");
        Assert.Equal(0, ranking.Score(@"C:\Work\project-notes.md"));
        Assert.Equal(0, ranking.Score(@"C:\Work\unrelated.md"));
        Assert.True(ranking.Score(@"C:\project\plan.md") > 0);
    }

    [Fact]
    public void VolumeShortlistUsesFinalDepthScoreBeforeDiscardingCandidates()
    {
        var table = NewTable();
        int parent = 5;
        for (int index = 0; index < 70; index++)
        {
            int directory = 10 + index;
            table.Set(Ref(directory), Ref(parent), "d" + index, FileAttributes.Directory);
            parent = directory;
        }
        for (int index = 0; index < 8; index++)
        {
            int directory = 100 + index;
            table.Set(Ref(directory), Ref(parent), "copy" + index, FileAttributes.Directory);
            table.Set(Ref(200 + index), Ref(directory), "needle.txt", FileAttributes.Archive);
        }
        table.Set(Ref(500), Ref(5), "needle.txt", FileAttributes.Archive);

        var (matches, total) = Search(table, "needle", maxResults: 1);
        Assert.Equal(9, total);
        Assert.Equal(@"C:\needle.txt", Assert.Single(matches).Path);
        Assert.Equal(QuickSearchNameRanking.Compile("needle").Score(@"C:\needle.txt"), matches[0].Score);
    }

    [Fact]
    public void VolumeExactStemBeatsShallowPrefixAndDottedFolder()
    {
        var table = NewTable();
        table.Set(Ref(10), Ref(5), "budget-notes.xlsx", FileAttributes.Archive);
        table.Set(Ref(11), Ref(5), "budget.v2", FileAttributes.Directory);
        int parent = 5;
        for (int index = 0; index < 70; index++)
        {
            int directory = 100 + index;
            table.Set(Ref(directory), Ref(parent), "d" + index, FileAttributes.Directory);
            parent = directory;
        }
        table.Set(Ref(500), Ref(parent), "budget.xlsx", FileAttributes.Archive);
        var (matches, _) = Search(table, "budget");
        Assert.EndsWith(@"\budget.xlsx", matches[0].Path);
        Assert.True(matches[0].Score > matches[1].Score);
        Assert.True(matches[0].Score > matches[2].Score);
    }

    [Fact]
    public void VolumeCutoffAndFinalOrderUseTheSameStableRecordTieBreak()
    {
        var table = NewTable();
        for (int index = 9; index >= 0; index--)
        {
            table.Set(Ref(100 + index), Ref(5), "copy" + index, FileAttributes.Directory);
            table.Set(Ref(200 + index), Ref(100 + index), "report.txt", FileAttributes.Archive);
        }
        var (matches, total) = Search(table, "report", maxResults: 2);
        Assert.Equal(10, total);
        Assert.Equal(new[] { @"C:\copy0\report.txt", @"C:\copy1\report.txt" }, matches.Select(match => match.Path));
        Assert.Equal(matches[0].Score, matches[1].Score);
    }

    [Fact]
    public void RankingDoesNotChangeLiteralEligibilityOrSelectedRootExclusions()
    {
        var table = NewTable();
        table.Set(Ref(10), Ref(5), "Allowed", FileAttributes.Directory);
        table.Set(Ref(11), Ref(5), "Other", FileAttributes.Directory);
        table.Set(Ref(12), Ref(10), "node_modules", FileAttributes.Directory);
        table.Set(Ref(20), Ref(10), "[draft]-report.txt", FileAttributes.Archive);
        table.Set(Ref(21), Ref(11), "[draft].txt", FileAttributes.Archive);
        table.Set(Ref(22), Ref(12), "[draft].txt", FileAttributes.Archive);
        table.Set(Ref(23), Ref(10), "draft.txt", FileAttributes.Archive);
        table.Set(Ref(24), Ref(10), "FileSearchRanking.cs", FileAttributes.Archive);
        var request = new VolumeTermSearchRequest("[draft]", MaxResults: 10);
        var (matches, _) = VolumeTermSearch.Search(table, @"C:\", VolumeTermSearch.Compile(request.Text)!, request, [10], CancellationToken.None);
        Assert.Equal(@"C:\Allowed\[draft]-report.txt", Assert.Single(matches).Path);
        Assert.Empty(Search(table, "darft").Matches);
        Assert.Empty(Search(table, "fsr").Matches);
    }

    private static (List<VolumeTermMatch> Matches, long Total) Search(VolumeNameTable table, string text, int maxResults = 10) =>
        VolumeTermSearch.Search(table, @"C:\", VolumeTermSearch.Compile(text)!, new(text, MaxResults: maxResults), [5], CancellationToken.None);

    private static VolumeNameTable NewTable()
    {
        var table = new VolumeNameTable();
        table.Set(Ref(5), Ref(5), ".", FileAttributes.Directory);
        return table;
    }

    private static ulong Ref(int record) => VolumeNameTable.ComposeReference(record, 1);
}
