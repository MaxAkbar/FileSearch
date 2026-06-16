using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class IndexStartupCatchUpServiceTests : IDisposable
{
    private const uint UsnReasonDataOverwrite = 0x00000001;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameNewName = 0x00002000;

    private readonly string _root;

    public IndexStartupCatchUpServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filesearch-catchup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CatchUpAsyncMarksRootHandledWhenCheckpointIsValid()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null));
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Empty(result.FallbackReasons);
        Assert.Equal(20, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncFallsBackWhenVolumeDoesNotSupportUsn()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: false);
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            new FakeCatchUpStore(null),
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("does not expose", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatchUpAsyncFallsBackWhenJournalIdChanged()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null)),
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(8, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("changed", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatchUpAsyncAppliesResolvedFileUpsertAndAdvancesCheckpoint()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var path = Path.Combine(root, "changed.txt");
        File.WriteAllText(path, "changed needle\n");
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null));
        var writer = new FakeIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        resolver.PathsByFileId["55"] = path;
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("55", UsnReasonDataOverwrite, FileAttributes.Archive, "changed.txt")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Equal(new[] { "55" }, store.DeletedFileReferences);
        var upsert = Assert.Single(writer.Upserts);
        Assert.Equal(root, upsert.Root);
        Assert.Equal(path, upsert.Path);
        Assert.Equal(20, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncAppliesDeleteByIdentity()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null));
        var writer = new FakeIndexWriter();
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("77", UsnReasonFileDelete, FileAttributes.Archive, "deleted.txt")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Equal(new[] { "77" }, store.DeletedFileReferences);
        Assert.Empty(writer.Upserts);
        Assert.Equal(20, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncDeletesIdentityWhenResolvedPathIsOutsideIndexedRoots()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var outside = Path.Combine(Path.GetTempPath(), "filesearch-catchup-outside-" + Guid.NewGuid().ToString("N"), "moved.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        try
        {
            File.WriteAllText(outside, "outside\n");
            var volume = CreateVolume(usnSupported: true);
            var store = new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null));
            var writer = new FakeIndexWriter();
            var resolver = new FakeVolumeResolver(volume);
            resolver.PathsByFileId["88"] = outside;
            var service = new IndexStartupCatchUpService(
                writer,
                store,
                resolver,
                new FakeJournalReader(
                    new UsnJournalSnapshot(7, 1, 20),
                    Change("88", UsnReasonDataOverwrite, FileAttributes.Archive, "moved.txt")));

            var result = await service.CatchUpAsync(
                new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
                TestContext.Current.CancellationToken);

            Assert.Contains(root, result.HandledRoots);
            Assert.Equal(new[] { "88" }, store.DeletedFileReferences);
            Assert.Empty(writer.Upserts);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(outside)!, recursive: true);
        }
    }

    [Fact]
    public async Task CatchUpAsyncFallsBackForDirectoryRename()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null));
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("99", UsnReasonRenameNewName, FileAttributes.Directory, "renamed")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("Directory", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.LastCommittedUsn);
    }

    private static IndexVolumeInfo CreateVolume(bool usnSupported) =>
        new(
            "fake-volume",
            @"C:\",
            @"\\.\C:",
            "123",
            usnSupported ? "NTFS" : "exFAT",
            IsRemote: false,
            usnSupported);

    private static UsnChangeRecord Change(
        string fileReferenceNumber,
        uint reason,
        FileAttributes attributes,
        string name) =>
        new(
            fileReferenceNumber,
            "1",
            11,
            DateTime.UtcNow,
            reason,
            attributes,
            name);

    private sealed class FakeVolumeResolver : IIndexVolumeResolver
    {
        private readonly IndexVolumeInfo _volume;

        public FakeVolumeResolver(IndexVolumeInfo volume) => _volume = volume;

        public Dictionary<string, string> PathsByFileId { get; } = new(StringComparer.Ordinal);

        public bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason)
        {
            volume = _volume;
            fallbackReason = string.Empty;
            return true;
        }

        public bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity)
        {
            identity = default;
            return false;
        }

        public bool TryResolvePathFromFileId(
            IndexVolumeInfo volume,
            string fileReferenceNumber,
            out string path,
            out string fallbackReason)
        {
            if (PathsByFileId.TryGetValue(fileReferenceNumber, out path!))
            {
                fallbackReason = string.Empty;
                return true;
            }

            path = string.Empty;
            fallbackReason = "No path.";
            return false;
        }
    }

    private sealed class FakeJournalReader : IUsnJournalReader
    {
        private readonly UsnJournalSnapshot _snapshot;
        private readonly IReadOnlyList<UsnChangeRecord> _records;

        public FakeJournalReader(UsnJournalSnapshot snapshot, params UsnChangeRecord[] records)
        {
            _snapshot = snapshot;
            _records = records;
        }

        public Task<UsnJournalSnapshot> QueryAsync(
            IndexVolumeInfo volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);

        public async IAsyncEnumerable<UsnChangeRecord> ReadChangesAsync(
            IndexVolumeInfo volume,
            long startUsn,
            long stopAtUsn,
            ulong journalId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var record in _records)
                yield return record;
        }
    }

    private sealed class FakeCatchUpStore : IIndexCatchUpStore
    {
        private readonly IndexVolumeCheckpoint? _checkpoint;

        public FakeCatchUpStore(IndexVolumeCheckpoint? checkpoint) => _checkpoint = checkpoint;

        public long LastCommittedUsn { get; private set; }

        public List<string> DeletedFileReferences { get; } = new();

        public Task<IndexVolumeCheckpoint?> GetVolumeCheckpointAsync(
            IndexVolumeInfo volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(_checkpoint);

        public Task DeleteFileByIdentityAsync(
            string volumeKey,
            string fileReferenceNumber,
            CancellationToken cancellationToken)
        {
            DeletedFileReferences.Add(fileReferenceNumber);
            return Task.CompletedTask;
        }

        public Task UpdateVolumeCheckpointAsync(
            IndexVolumeInfo volume,
            ulong journalId,
            long lastCommittedUsn,
            string health,
            string? error,
            CancellationToken cancellationToken)
        {
            LastCommittedUsn = lastCommittedUsn;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIndexWriter : IIndexWriter
    {
        public List<(string Root, string Path)> Upserts { get; } = new();

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshRootAsync(
            IndexRequest request,
            IndexRefreshMode mode,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpsertFileAsync(
            string root,
            string path,
            WalkerOptions options,
            CancellationToken cancellationToken)
        {
            Upserts.Add((root, path));
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
