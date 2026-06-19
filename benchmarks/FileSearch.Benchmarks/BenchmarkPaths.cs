namespace FileSearch.Benchmarks;

internal sealed class BenchmarkPaths
{
    public BenchmarkPaths(string rootDirectory, BenchmarkProfile profile)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Profile = profile;
        CorpusDirectory = Path.Combine(RootDirectory, profile.Name);
        ContentRoot = Path.Combine(CorpusDirectory, "content");
        MetadataRoot = Path.Combine(CorpusDirectory, "metadata-root");
        DatabasePath = Path.Combine(CorpusDirectory, "index", "filesearch-benchmark.db");
        ReportsDirectory = Path.Combine(CorpusDirectory, "reports");
        ManifestPath = Path.Combine(CorpusDirectory, "manifest.json");
        MarkerPath = Path.Combine(CorpusDirectory, ".filesearch-benchmark-corpus");
    }

    public BenchmarkProfile Profile { get; }

    public string RootDirectory { get; }

    public string CorpusDirectory { get; }

    public string ContentRoot { get; }

    public string MetadataRoot { get; }

    public string DatabasePath { get; }

    public string ReportsDirectory { get; }

    public string ManifestPath { get; }

    public string MarkerPath { get; }

    public static BenchmarkPaths FromEnvironment()
    {
        var profile = BenchmarkProfile.Resolve(Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_PROFILE"));
        return Resolve(profile, Environment.GetEnvironmentVariable("FILESEARCH_BENCHMARK_ROOT"));
    }

    public static BenchmarkPaths Resolve(BenchmarkProfile profile, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks");

        return new BenchmarkPaths(root, profile);
    }
}
