using BenchmarkDotNet.Attributes;
using FileSearch.Core.Indexing;

namespace FileSearch.Benchmarks;

[MemoryDiagnoser]
public class MetadataSearchBenchmarks
{
    private BenchmarkPaths _paths = null!;
    private CSharpDbFileIndex _index = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _paths = BenchmarkPaths.Resolve(
            BenchmarkProfile.Resolve(Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_PROFILE")),
            Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_ROOT"));
        var manifest = new BenchmarkCorpusGenerator().EnsureCorpus(_paths, force: false);
        await new MetadataIndexSeeder().EnsureSeededAsync(_paths, manifest, CancellationToken.None)
            .ConfigureAwait(false);
        _index = BenchmarkIndexFactory.Create(_paths);
    }

    [GlobalCleanup]
    public void Cleanup() => _index.Dispose();

    [Benchmark]
    public async Task<int> ExactFilenameQuery()
    {
        var hits = await BenchmarkSearch.SearchAllAsync(
                _index,
                _paths.MetadataRoot,
                "metadata_target_000042",
                CancellationToken.None)
            .ConfigureAwait(false);
        return hits.Count;
    }
}

[MemoryDiagnoser]
public class IndexedContentSearchBenchmarks
{
    private BenchmarkPaths _paths = null!;
    private CorpusManifest _manifest = null!;
    private CSharpDbFileIndex _index = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _paths = BenchmarkPaths.Resolve(
            BenchmarkProfile.Resolve(Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_PROFILE")),
            Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_ROOT"));
        _manifest = new BenchmarkCorpusGenerator().EnsureCorpus(_paths, force: false);
        await new MetadataIndexSeeder().EnsureSeededAsync(_paths, _manifest, CancellationToken.None)
            .ConfigureAwait(false);
        _index = BenchmarkIndexFactory.Create(_paths);
        await _index.BuildOrRefreshAsync(
                new IndexRequest(_paths.ContentRoot, BenchmarkIndexFactory.IndexOptions),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup() => _index.Dispose();

    [Benchmark]
    public async Task<int> ContentQuery()
    {
        var query = _manifest.Queries.First(static query => query.RootKind == "content").Query;
        var hits = await BenchmarkSearch.SearchAllAsync(_index, _paths.ContentRoot, query, CancellationToken.None)
            .ConfigureAwait(false);
        return hits.Count;
    }
}
