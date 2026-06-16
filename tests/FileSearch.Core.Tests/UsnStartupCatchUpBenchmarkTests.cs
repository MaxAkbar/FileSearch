using System.Diagnostics;
using System.Text;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class UsnStartupCatchUpBenchmarkTests
{
    [Fact]
    public async Task MeasureUsnCatchUpAgainstRootRefresh()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FILESEARCH_RUN_USN_BENCH"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var requestedRoot = Environment.GetEnvironmentVariable("FILESEARCH_USN_BENCH_ROOT");
        var parent = string.IsNullOrWhiteSpace(requestedRoot)
            ? Path.GetTempPath()
            : requestedRoot;
        var fileCount = ReadPositiveInt("FILESEARCH_USN_BENCH_FILE_COUNT", 2_000);
        var changeCount = Math.Min(ReadPositiveInt("FILESEARCH_USN_BENCH_CHANGE_COUNT", 100), fileCount);
        var outputPath = Environment.GetEnvironmentVariable("FILESEARCH_USN_BENCH_OUTPUT");
        var basePath = Path.Combine(parent, "filesearch-usn-bench-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(basePath, "root");

        Directory.CreateDirectory(root);
        try
        {
            var resolver = new WindowsIndexVolumeResolver();
            Assert.True(
                resolver.TryResolveVolume(root, out var volume, out var volumeReason),
                $"Could not resolve volume: {volumeReason}");
            Assert.False(volume.IsRemote, "USN benchmark requires a local volume.");
            Assert.True(volume.UsnSupported, $"USN benchmark requires NTFS/ReFS with journal support. Resolved filesystem: {volume.FileSystemName}");

            var journal = new WindowsUsnJournalReader();
            _ = await journal.QueryAsync(volume, TestContext.Current.CancellationToken);

            CreateFiles(root, fileCount);

            var usnDbPath = Path.Combine(basePath, "usn-index", "filesearch.db");
            var refreshDbPath = Path.Combine(basePath, "refresh-index", "filesearch.db");
            using var usnIndex = CreateIndex(usnDbPath, resolver, journal);
            using var refreshIndex = CreateIndex(refreshDbPath, resolver, journal);

            var initialRefreshBuildMs = await MeasureAsync(
                () => refreshIndex.BuildOrRefreshAsync(new IndexRequest(root, new WalkerOptions()), TestContext.Current.CancellationToken));
            var initialUsnBuildMs = await MeasureAsync(
                () => usnIndex.BuildOrRefreshAsync(new IndexRequest(root, new WalkerOptions()), TestContext.Current.CancellationToken));

            ApplyChanges(root, changeCount);

            var catchUp = new IndexStartupCatchUpService(
                usnIndex,
                new CSharpDbIndexCatchUpStore(usnIndex),
                resolver,
                journal);
            var usnCatchUpMs = await MeasureAsync(async () =>
            {
                var result = await catchUp.CatchUpAsync(
                    new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
                    TestContext.Current.CancellationToken);
                var normalizedRoot = IndexPath.NormalizeRoot(root);
                Assert.True(
                    result.HandledRoots.Contains(normalizedRoot),
                    result.FallbackReasons.TryGetValue(normalizedRoot, out var reason)
                        ? $"USN replay fell back: {reason}"
                        : "USN replay did not mark the benchmark root handled.");
            });

            var rootRefreshMs = await MeasureAsync(
                () => refreshIndex.RefreshRootAsync(
                    new IndexRequest(root, new WalkerOptions()),
                    IndexRefreshMode.Incremental,
                    TestContext.Current.CancellationToken));

            var changedHits = await CountHitsAsync(usnIndex, root, "changed-benchmark");
            var addedHits = await CountHitsAsync(usnIndex, root, "added-benchmark");
            Assert.Equal(changeCount, changedHits);
            Assert.Equal(changeCount, addedHits);

            var report = new StringBuilder()
                .AppendLine("FileSearch USN startup catch-up benchmark")
                .AppendLine($"Root: {root}")
                .AppendLine($"Files: {fileCount:n0}")
                .AppendLine($"Changed files: {changeCount:n0}")
                .AppendLine($"Initial build for USN database: {initialUsnBuildMs:n0} ms")
                .AppendLine($"Initial build for refresh database: {initialRefreshBuildMs:n0} ms")
                .AppendLine($"USN catch-up: {usnCatchUpMs:n0} ms")
                .AppendLine($"Incremental root refresh: {rootRefreshMs:n0} ms");

            if (!string.IsNullOrWhiteSpace(outputPath))
                await File.WriteAllTextAsync(outputPath, report.ToString(), TestContext.Current.CancellationToken);

            TestContext.Current.SendDiagnosticMessage(report.ToString());
        }
        finally
        {
            TryDelete(basePath);
        }
    }

    private static CSharpDbFileIndex CreateIndex(
        string dbPath,
        IIndexVolumeResolver resolver,
        IUsnJournalReader journal)
    {
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        return new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = dbPath },
            new FileWalker(),
            registry,
            searchOptions: null,
            logger: null,
            resolver,
            journal);
    }

    private static void CreateFiles(string root, int fileCount)
    {
        for (var i = 0; i < fileCount; i++)
        {
            var folder = Path.Combine(root, (i % 20).ToString("D2"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, $"file-{i:D6}.txt"),
                $"benchmark seed {i:D6}\n");
        }
    }

    private static void ApplyChanges(string root, int changeCount)
    {
        for (var i = 0; i < changeCount; i++)
        {
            var folder = Path.Combine(root, (i % 20).ToString("D2"));
            File.WriteAllText(
                Path.Combine(folder, $"file-{i:D6}.txt"),
                $"changed-benchmark {i:D6}\n");
            File.WriteAllText(
                Path.Combine(folder, $"added-{i:D6}.txt"),
                $"added-benchmark {i:D6}\n");
        }
    }

    private static async Task<long> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private static async Task<int> CountHitsAsync(CSharpDbFileIndex index, string root, string text)
    {
        var count = 0;
        var request = new SearchRequest(
            new TermQuery(text),
            new[] { root },
            new WalkerOptions(),
            UseIndex: true);

        await foreach (var _ in index.SearchAsync(request, TestContext.Current.CancellationToken))
            count++;

        return count;
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
