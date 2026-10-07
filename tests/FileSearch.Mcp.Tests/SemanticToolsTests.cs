using System.Runtime.CompilerServices;
using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Mcp;
using FileSearch.Mcp.Tools;
using ModelContextProtocol;

namespace FileSearch.Mcp.Tests;

public sealed class SemanticToolsTests
{
    [Theory]
    [InlineData("semantic", "database recovery", true)]
    [InlineData("unified", "semantic:\"database recovery\"", true)]
    [InlineData("plain", "database recovery", false)]
    public async Task IndexedSemanticSearchUsesHybridSearcherAndLiteralSearchKeepsItsRoute(string mode, string query, bool hybrid)
    {
        var index = new ReadOnlyIndex();
        var searcher = new RecordingSearcher();
        var policy = new RootPolicy(new McpServerRails { ExplicitRoots = [@"C:\Docs"] }, index);
        var tools = new SearchTools(searcher, index, index, new QueryFactory(), policy);
        await tools.SearchIndexAsync(query, mode: mode, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(hybrid ? 1 : 0, searcher.Requests.Count);
        Assert.Equal(hybrid ? 0 : 1, index.SearchCalls);
        if (hybrid)
        {
            var request = Assert.Single(searcher.Requests);
            Assert.True(request.UseIndex);
            Assert.Equal([@"C:\Docs"], request.Roots);
            if (mode == "semantic") Assert.NotNull(request.SemanticOptions);
        }
    }

    [Fact]
    public async Task LiveSemanticSearchExplainsThatAnExistingIndexIsRequired()
    {
        var index = new ReadOnlyIndex();
        var tools = new SearchTools(new RecordingSearcher(), index, index, new QueryFactory(), new RootPolicy(new McpServerRails(), index));
        var error = await Assert.ThrowsAsync<McpException>(() => tools.SearchContentAsync("database recovery", [@"C:\Docs"], mode: "semantic", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("search_index", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsSelectedInstalledAndIndexedStatesOnlyForAllowedRoots()
    {
        var index = new ReadOnlyIndex();
        var semantic = new RecordingStatus();
        var options = new EmbeddingModelPackOptions { SelectedModelPackId = "gemma-test" };
        var tools = new IndexTools(index, new ExtractorRegistry([]), new RootPolicy(new McpServerRails { ExplicitRoots = [@"C:\Docs"] }, index),
            new ModelStore(), semantic, options);
        using var json = JsonDocument.Parse(await tools.IndexStatusAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("documentModels").GetProperty("enabled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("documentModels").GetProperty("installed")[0].GetProperty("usable").GetBoolean());
        Assert.False(json.RootElement.GetProperty("locations")[0].GetProperty("smartSearch").GetProperty("isReady").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("hiddenLocationCount").GetInt32());
        Assert.Equal([@"C:\Docs"], semantic.Roots);
    }

    private sealed class RecordingStatus : ISemanticIndexStatusService
    {
        public List<string> Roots { get; } = [];
        public Task<SemanticIndexRootStatus> GetRootStatusAsync(string root, CancellationToken cancellationToken)
        {
            Roots.Add(root);
            return Task.FromResult(new SemanticIndexRootStatus(root, true, "gemma-test", "Gemma", 1, 1, 0, 0, "Vectors are not built for the selected model."));
        }
    }
    private sealed class ModelStore : IEmbeddingModelPackStore
    {
        private readonly InstalledEmbeddingModelPack _pack = new(new EmbeddingModelPackManifest { Id = "gemma-test", Version = "1", Dimension = 768 }, @"C:\Models", true, "Validated");
        public string ModelPacksDirectory => @"C:\Models";
        public Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledEmbeddingModelPack>>([_pack]);
        public Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken) => Task.FromResult<InstalledEmbeddingModelPack?>(_pack);
        public string GetPackDirectory(string modelId) => throw new InvalidOperationException("Read-only status must not install models.");
    }
    private sealed class RecordingSearcher : ISearcher
    {
        public List<SearchRequest> Requests { get; } = [];
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield break;
        }
    }
    private sealed class ReadOnlyIndex : IIndexSearch, IIndexMaintenance
    {
        public int SearchCalls { get; private set; }
        public string DatabasePath => @"C:\fake\filesearch.db";
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchCalls++;
            await Task.CompletedTask;
            yield break;
        }
        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult(new IndexCoverage(IndexCoverageStatus.Covered, "Covered"));
        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) => Task.FromResult(new IndexStats(root, 1, 1, null, true));
        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexedLocationInfo>>([
            new(@"C:\Docs", 1, 1, null, "", true), new(@"D:\Private", 1, 1, null, "", true)]);
        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) => Task.FromResult(new IndexDatabaseInfo(DatabasePath, false, false, "0", 0, 0, 0, 0, 0, 0, 0, null));
        public Task CompactAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only tools must not compact or write indexes.");
    }
}
