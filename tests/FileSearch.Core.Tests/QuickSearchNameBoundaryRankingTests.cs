using FileSearch.Core.Queries;

namespace FileSearch.Core.Tests;

public sealed class QuickSearchNameBoundaryRankingTests
{
    [Theory]
    [InlineData("cat", "cat-care-notes.txt", "catalogue.txt")]
    [InlineData("git", "notes-git-reference.md", "digital.docx")]
    [InlineData("manager", "TaskQueueManager.cs", "taskqueuemanager.cs")]
    [InlineData("reader", "XMLReaderOptions.cs", "xmlreaderoptions.cs")]
    [InlineData("12", "report12-final.md", "report12345.md")]
    [InlineData("config", "v2config-long-notes.md", "reconfiguration.md")]
    [InlineData("état", "NotesÉtatGuide.md", "notesétatguide.md")]
    [InlineData("guide", "Cafe\u0301GuideNotes.md", "cafe\u0301guidenotes.md")]
    [InlineData("C++", "notes-C++-guide.md", "logic++manual.md")]
    [InlineData("cafe", "cafe-care-notes.md", "cafe\u0301ine.md")]
    public void CompleteNameComponentsPrecedeEmbeddedMatchesWithinTheSameTier(string query, string complete, string embedded)
    {
        var literal = new QuickSearchNameQuery(query);
        Assert.True(literal.IsMatch(complete));
        Assert.True(literal.IsMatch(embedded));
        var ranking = QuickSearchNameRanking.Compile(query);
        Assert.True(ranking.Score(@"C:\Work\" + complete) > ranking.Score(@"C:\Work\" + embedded));
    }

    [Fact]
    public void MultipleLiteralTermsRecognizeSeparateCamelCaseComponents()
    {
        var ranking = QuickSearchNameRanking.Compile("cache entry");
        Assert.True(ranking.Score(@"C:\CacheEntryPolicy.cs") > ranking.Score(@"C:\cacheentrypolicy.cs"));
    }

    [Fact]
    public void ACompleteLaterOccurrenceSuppliesTheSignalWhenTheFirstIsEmbedded()
    {
        var ranking = QuickSearchNameRanking.Compile("git");
        Assert.True(ranking.Score(@"C:\digital-git-guide.md") > ranking.Score(@"C:\digitalgitguide.md"));
    }

    [Fact]
    public void BoundarySignalsCannotOvertakeExactOrExistingPhrasePrefixTiers()
    {
        string deep = @"C:\" + string.Join("\\", Enumerable.Repeat("nested", 100));
        var exact = QuickSearchNameRanking.Compile("config");
        Assert.True(exact.Score(deep + @"\config") > exact.Score(@"C:\config.txt"));
        Assert.True(exact.Score(deep + @"\config.txt") > exact.Score(@"C:\notes-config-guide.md"));
        var phrase = QuickSearchNameRanking.Compile("project plan");
        Assert.True(phrase.Score(@"C:\project planning notes.md") > phrase.Score(@"C:\PlanForProject.md"));
    }

    [Theory]
    [InlineData("[draft]", "draft.txt")]
    [InlineData("a+b", "aaab.txt")]
    [InlineData("\"report\"", "report.txt")]
    [InlineData("café", "cafe\u0301.txt")]
    public void BoundaryAnalysisDoesNotExpandOrNormalizeLiteralEligibility(string query, string unrelated)
    {
        Assert.False(new QuickSearchNameQuery(query).IsMatch(unrelated));
        Assert.Equal(0, QuickSearchNameRanking.Compile(query).Score(@"C:\Work\" + unrelated));
    }

    [Fact]
    public void CompiledScoringAllocatesNoMemoryAfterWarmup()
    {
        var manager = QuickSearchNameRanking.Compile("manager");
        var unicode = QuickSearchNameRanking.Compile("état");
        var later = QuickSearchNameRanking.Compile("git");
        var multiple = QuickSearchNameRanking.Compile("cache entry");
        var symbols = QuickSearchNameRanking.Compile("C++");
        var samples = new (QuickSearchNameRanking Ranking, string Path, bool Directory)[]
        {
            (manager, @"C:\Work\TaskQueueManager.cs", false),
            (manager, @"C:\Work\taskqueuemanager.cs", false),
            (manager, "C:\\Work\\Cafe\u0301ManagerGuide.cs", false),
            (manager, @"\\server\share\notes-manager", true),
            (manager, @"C:\Work\unrelated.cs", false),
            (unicode, @"C:\Work\NotesÉtatGuide.md", false),
            (unicode, @"C:\Work\notesétatguide.md", false),
            (later, @"C:\Work\digital-git-guide.md", false),
            (multiple, @"C:\Work\CacheEntryPolicy.cs", false),
            (symbols, @"C:\Work\notes-C++-guide.md", false),
        };
        double checksum = ScoreBatch(samples, 512);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += ScoreBatch(samples, 4096);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(double.IsFinite(checksum) && checksum > 0);
        Assert.Equal(0, allocated);

        static double ScoreBatch((QuickSearchNameRanking Ranking, string Path, bool Directory)[] samples, int iterations)
        {
            double total = 0;
            for (int iteration = 0; iteration < iterations; iteration++)
                foreach (var sample in samples) total += sample.Ranking.Score(sample.Path, sample.Directory);
            return total;
        }
    }
}
