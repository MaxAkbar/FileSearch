using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class UsnStartupCatchUpSmokeTests
{
    [Fact]
    public async Task RealVolumeStartupCatchUpReplaysChanges()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FILESEARCH_RUN_USN_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var requestedRoot = Environment.GetEnvironmentVariable("FILESEARCH_USN_SMOKE_ROOT");
        var parent = string.IsNullOrWhiteSpace(requestedRoot)
            ? Path.GetTempPath()
            : requestedRoot;
        var basePath = Path.Combine(parent, "filesearch-usn-smoke-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(basePath, "root");
        var dbPath = Path.Combine(basePath, "index", "filesearch.db");

        Directory.CreateDirectory(root);
        try
        {
            var resolver = new WindowsIndexVolumeResolver();
            Assert.True(
                resolver.TryResolveVolume(root, out var volume, out var volumeReason),
                $"Could not resolve volume: {volumeReason}");
            Assert.False(volume.IsRemote, "USN smoke test requires a local volume.");
            Assert.True(volume.UsnSupported, $"USN smoke test requires NTFS with journal support. Resolved filesystem: {volume.FileSystemName}");
            Assert.False(
                volume.VolumeDevicePath.EndsWith('\\'),
                $"USN journal handle path must name the volume, not the root directory: {volume.VolumeDevicePath}");
            Assert.True(
                volume.RootDirectoryPath.EndsWith('\\'),
                $"Root directory path must retain its trailing separator: {volume.RootDirectoryPath}");

            var journal = new WindowsUsnJournalReader();
            var directJournal = await journal.QueryAsync(volume, TestContext.Current.CancellationToken);
            Assert.True(directJournal.NextUsn > 0, "USN journal query returned an invalid cursor.");

            var plain = new PlainTextExtractor();
            var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
            using var index = new CSharpDbFileIndex(
                new FileIndexOptions { DatabasePath = dbPath },
                new FileWalker(),
                registry,
                searchOptions: null,
                logger: null,
                resolver,
                journal);

            var changed = Path.Combine(root, "changed.txt");
            var deleted = Path.Combine(root, "deleted.txt");
            File.WriteAllText(changed, "old needle\n");
            File.WriteAllText(deleted, "delete needle\n");

            await index.BuildOrRefreshAsync(
                new IndexRequest(root, new WalkerOptions()),
                TestContext.Current.CancellationToken);

            var checkpoint = await index.GetVolumeCheckpointCoreAsync(volume, TestContext.Current.CancellationToken);
            Assert.NotNull(checkpoint);
            Assert.NotNull(checkpoint.JournalId);
            Assert.True(checkpoint.LastCommittedUsn > 0);

            var added = Path.Combine(root, "added.txt");
            var nestedDir = Path.Combine(root, "nested");
            var nested = Path.Combine(nestedDir, "nested.txt");
            File.WriteAllText(changed, "updated needle\n");
            File.WriteAllText(added, "fresh needle\n");
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(nested, "nested needle\n");
            File.Delete(deleted);

            var catchUp = new IndexStartupCatchUpService(
                index,
                new CSharpDbIndexCatchUpStore(index),
                resolver,
                journal);

            var result = await catchUp.CatchUpAsync(
                new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
                TestContext.Current.CancellationToken);

            var normalizedRoot = IndexPath.NormalizeRoot(root);
            Assert.True(
                result.HandledRoots.Contains(normalizedRoot),
                result.FallbackReasons.TryGetValue(normalizedRoot, out var reason)
                    ? $"USN replay fell back: {reason}"
                    : "USN replay did not mark the smoke root handled.");

            Assert.Single(await SearchAsync(index, root, "updated"));
            Assert.Single(await SearchAsync(index, root, "fresh"));
            Assert.Single(await SearchAsync(index, root, "nested"));
            Assert.Empty(await SearchAsync(index, root, "old"));
            Assert.Empty(await SearchAsync(index, root, "delete"));
        }
        finally
        {
            TryDelete(basePath);
        }
    }

    private static async Task<List<Hit>> SearchAsync(CSharpDbFileIndex index, string root, string text)
    {
        var hits = new List<Hit>();
        var request = new SearchRequest(
            new TermQuery(text),
            new[] { root },
            new WalkerOptions(),
            UseIndex: true);

        await foreach (var hit in index.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        return hits;
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
