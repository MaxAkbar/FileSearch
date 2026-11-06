using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Tests;

public sealed class IndexedLauncherSearchTests
{
    [Fact]
    public async Task MissingIndexReturnsEmptyWithoutSearching()
    {
        var index = new FakeIndex();
        var response = await Create(index).SearchAsync("invoice", SearchTarget.FileNames, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(response.Results);
        Assert.Empty(index.Requests);
    }

    [Fact]
    public async Task EmptyQueryDoesNotEvenOpenTheIndex()
    {
        var index = new FakeIndex();
        await Create(index).SearchAsync("  ", SearchTarget.Content, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, index.LocationReads);
    }

    [Fact]
    public async Task SkipsIncompatibleLocationsWithoutLiveFallback()
    {
        var index = new FakeIndex { Locations = [Location("old"), Location("current")] };
        index.UncoveredRoots.Add("old");
        index.Hits = [Hit("current/report.txt")];

        var response = await Create(index).SearchAsync("report", SearchTarget.FileNames, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(response.Results);
        Assert.Equal(1, response.SkippedLocations);
        Assert.Equal("current", Assert.Single(index.Requests).Roots[0]);
    }

    [Fact]
    public async Task PreservesBuildTimeFiltersAndDeduplicatesFilesAcrossLocations()
    {
        var index = new FakeIndex { Locations = [Location("one"), Location("two")] };
        index.Hits = [Hit("same.txt"), Hit("SAME.txt"), Hit("other.txt")];

        var response = await Create(index).SearchAsync("type:txt needle", SearchTarget.Content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Results.Count);
        Assert.All(index.Requests, request =>
        {
            Assert.True(request.UseIndex);
            Assert.Equal(QueryMode.Unified, request.Mode);
            Assert.Contains(".txt", request.WalkerOptions.IncludeExtensions);
            Assert.Contains("private", request.WalkerOptions.ExcludeDirectories);
            Assert.True(request.WalkerOptions.EnableOcr);
            Assert.Single(request.Roots);
        });
    }

    [Fact]
    public async Task ResultLimitStopsEnumeration()
    {
        var index = new FakeIndex { Locations = [Location("root")], Hits = [Hit("a"), Hit("b"), Hit("c")] };
        var response = await Create(index).SearchAsync("x", SearchTarget.FileNames, maxResults: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Results.Count);
        Assert.Equal(2, index.YieldedHits);
        Assert.True(response.IsTruncated);
        Assert.Equal(QueryMode.PlainText, Assert.Single(index.Requests).Mode);
    }

    [Fact]
    public async Task DeadlineReturnsPartialResults()
    {
        var index = new FakeIndex { Locations = [Location("root")], Hits = [Hit("a")], WaitAfterHits = true };
        var response = await Create(index).SearchAsync("x", SearchTarget.Content, timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(response.Results);
        Assert.True(response.TimedOut);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAnEmptyResult()
    {
        var index = new FakeIndex { Locations = [Location("root")], WaitAfterHits = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(index).SearchAsync("x", SearchTarget.Content, cancellationToken: cancellation.Token));
    }

    private static IndexedLauncherSearch Create(FakeIndex index) => new(index, index, new QueryFactory());

    private static Hit Hit(string path) => new(path, 1, "needle", []);

    private static IndexedLocationInfo Location(string root) => new(root, 1, 1, DateTime.UtcNow,
        "v1|recursive=1|hidden=0|include=.txt|exclude=|includeDirs=|excludeDirs=private|ocr=1", true);

    private sealed class FakeIndex : IIndexSearch, IIndexMaintenance
    {
        public IReadOnlyList<IndexedLocationInfo> Locations { get; init; } = [];
        public IReadOnlyList<Hit> Hits { get; set; } = [];
        public HashSet<string> UncoveredRoots { get; } = [];
        public List<SearchRequest> Requests { get; } = [];
        public int LocationReads { get; private set; }
        public int YieldedHits { get; private set; }
        public bool WaitAfterHits { get; init; }
        public string DatabasePath => "unused";

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken)
        {
            LocationReads++;
            return Task.FromResult(Locations);
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexCoverage(UncoveredRoots.Contains(request.Roots[0])
                ? IndexCoverageStatus.Incompatible : IndexCoverageStatus.Covered, "test"));

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var hit in Hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                YieldedHits++;
                yield return hit;
            }
            if (WaitAfterHits)
                await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
