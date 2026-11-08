using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class QuickSearchPathMatchingTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "FileSearchQuickPaths-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "chosen");
    private readonly Searcher _live;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public QuickSearchPathMatchingTests()
    {
        Directory.CreateDirectory(Root);
        var plain = new PlainTextExtractor();
        _live = new Searcher(new FileWalker(), new ExtractorRegistry([plain], plain));
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public async Task FilePathsRequireExplicitOptInAndThenHighlightTermsAcrossParentAndLeaf()
    {
        string file = Write("ProjectAlpha", "budget.txt");
        var request = Request("projectalpha budget");
        Assert.Empty(await Collect(_live, request));
        var hit = Assert.Single(await Collect(_live, request with { MatchFilePaths = true }));
        Assert.Equal(file, hit.Path);
        Assert.Equal(HitKind.Metadata, hit.Kind);
        Assert.Contains(Path.Combine("ProjectAlpha", "budget.txt"), hit.LineContent);
        var highlighted = hit.Highlights.Select(span => hit.LineContent.Substring(span.Start, span.Length)).ToArray();
        Assert.Contains("ProjectAlpha", highlighted);
        Assert.Contains("budget", highlighted);
    }

    [Fact]
    public async Task FullPathFallbackIncludesTheSelectedRootNameWithoutSearchingOutsideIt()
    {
        string inside = Write("leaf", "budget.txt");
        Directory.CreateDirectory(Path.Combine(_base, "outside"));
        File.WriteAllText(Path.Combine(_base, "outside", "budget.txt"), "not selected");
        var hit = Assert.Single(await Collect(_live, Request("chosen budget") with { MatchFilePaths = true }));
        Assert.Equal(inside, hit.Path);
        Assert.Contains(inside, hit.LineContent);
    }

    [Fact]
    public async Task ExplicitPathMatchingStillHonorsRootFileAndDirectoryRestrictions()
    {
        string allowed = Write("ProjectAlpha", "budget.md");
        Write("ProjectAlpha", "budget.txt");
        Write("ProjectBeta", "budget.md");
        Write("bin", "ProjectAlpha-budget.md");
        Directory.CreateDirectory(Path.Combine(_base, "outside"));
        File.WriteAllText(Path.Combine(_base, "outside", "ProjectAlpha-budget.md"), "not selected");
        var request = Request("budget") with
        {
            MatchFilePaths = true,
            WalkerOptions = new WalkerOptions
            {
                IncludeGlobs = ["*.md"],
                IncludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ProjectAlpha" },
            },
        };
        Assert.Equal(allowed, Assert.Single(await Collect(_live, request)).Path);
    }

    [Fact]
    public async Task NonrecursiveSearchDoesNotGainNestedFilesFromPathMatching()
    {
        Write("ProjectAlpha", "budget.txt");
        string top = Path.Combine(Root, "projectalpha-budget.txt");
        File.WriteAllText(top, "top");
        var request = Request("projectalpha budget") with { MatchFilePaths = true, WalkerOptions = new WalkerOptions { Recursive = false } };
        Assert.Equal(top, Assert.Single(await Collect(_live, request)).Path);
    }

    [Fact]
    public async Task MetadataIndexFallsBackBeforeUsingLeafOnlySearchOrCoverage()
    {
        string file = Write("ProjectAlpha", "budget.txt");
        var index = new UnusedIndex();
        var searcher = new IndexedSearcher(_live, index, new IndexCoverageService(index));
        var status = new List<string>();
        var request = Request("projectalpha budget") with { UseIndex = true, MatchFilePaths = true, Status = status.Add };
        var hit = Assert.Single(await Collect(searcher, request));
        Assert.Equal(file, hit.Path);
        Assert.Equal(HitRoute.Live, hit.Route);
        Assert.Equal(0, index.CoverageCalls);
        Assert.Equal(0, index.SearchCalls);
        Assert.Contains(status, message => message.Contains("Filename/path matching", StringComparison.Ordinal) && message.Contains("using live scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DriveIndexFallsBackBeforeConsultingTheVolumeForPathMatching()
    {
        string file = Write("ProjectAlpha", "budget.txt");
        var resolver = new UnusedVolumeResolver();
        using var index = new VolumeNameIndexService(resolver, new WindowsUsnJournalReader(), new WindowsVolumeScanner(),
            new WindowsVolumeFileIdResolverFactory(), options: new VolumeNameIndexOptions { SnapshotDirectory = Path.Combine(_base, "snapshots") });
        var searcher = new VolumeNameSearcher(_live, index);
        var status = new List<string>();
        var request = Request("projectalpha budget") with { UseIndex = true, MatchFilePaths = true, Status = status.Add };
        Assert.Equal(file, Assert.Single(await Collect(searcher, request)).Path);
        Assert.Equal(0, resolver.Calls);
        if (OperatingSystem.IsWindows())
            Assert.Contains(status, message => message.Contains("drive name index", StringComparison.Ordinal) && message.Contains("using live scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptInPathMatchingDoesNotChangeLiteralContentSearch()
    {
        string content = Path.Combine(Root, "unrelated.txt");
        File.WriteAllText(content, "alpha budget\nalpha only");
        Write("alpha", "budget.txt");
        var request = new SearchRequest(new TermQuery("alpha budget"), [Root], new WalkerOptions(), MatchFilePaths: true);
        var hit = Assert.Single(await Collect(_live, request));
        Assert.Equal(content, hit.Path);
        Assert.Equal(HitKind.Content, hit.Kind);
        Assert.Equal("alpha budget", hit.LineContent);
    }

    private SearchRequest Request(string query) => new(new QuickSearchNameQuery(query), [Root], new WalkerOptions(), SearchTarget: SearchTarget.FileNames);

    private string Write(string folder, string name)
    {
        string directory = Path.Combine(Root, folder);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name);
        File.WriteAllText(file, "content");
        return file;
    }

    private static async Task<List<Hit>> Collect(ISearcher searcher, SearchRequest request)
    {
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, Token)) hits.Add(hit);
        return hits;
    }

    private sealed class UnusedIndex : IIndexSearch
    {
        public int SearchCalls { get; private set; }
        public int CoverageCalls { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchCalls++;
            await Task.CompletedTask;
            yield return new Hit("wrong-index-route", 0, "", []);
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken)
        {
            CoverageCalls++;
            return Task.FromResult(new IndexCoverage(IndexCoverageStatus.Covered, "covered"));
        }
    }

    private sealed class UnusedVolumeResolver : IIndexVolumeResolver
    {
        public int Calls { get; private set; }

        public bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason)
        { Calls++; throw new InvalidOperationException("Path-matching fallback must not consult the drive index."); }

        public bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity) => throw new InvalidOperationException();

        public FileIdResolutionResult ResolvePathFromFileId(IndexVolumeInfo volume, string fileReferenceNumber) => throw new InvalidOperationException();
    }
}
