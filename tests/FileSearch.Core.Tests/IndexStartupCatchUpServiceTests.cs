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
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "1" });
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
    public async Task CatchUpAsyncFallsBackWhenRootProfileIsStale()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "1" })
        {
            RootProfileCurrent = false,
        };
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("extractor versions", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.LastCommittedUsn);
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
    public async Task CatchUpAsyncFallsBackForCloudBackedRootEvenOnUsnVolume()
    {
        var cloudRoot = Path.Combine(Path.GetTempPath(), "OneDrive - FileSearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudRoot);
        try
        {
            var root = IndexPath.NormalizeRoot(cloudRoot);
            var volume = CreateVolume(usnSupported: true);
            var store = new FakeCatchUpStore(
                new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
                DirectoryReferences: new[] { "1" });
            var service = new IndexStartupCatchUpService(
                new FakeIndexWriter(),
                store,
                new FakeVolumeResolver(volume),
                new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

            var result = await service.CatchUpAsync(
                new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
                TestContext.Current.CancellationToken);

            Assert.Empty(result.HandledRoots);
            Assert.Contains(root, result.FallbackReasons.Keys);
            Assert.Contains("Cloud-backed", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, store.LastCommittedUsn);
        }
        finally
        {
            Directory.Delete(cloudRoot, recursive: true);
        }
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
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "55" },
            DirectoryReferences: new[] { "1" });
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
        Assert.Empty(store.DeletedFileReferences);
        var upsert = Assert.Single(writer.Upserts);
        Assert.Equal(root, upsert.Root);
        Assert.Equal(path, upsert.Path);
        Assert.Equal(20, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncAppliesNewFileWhenParentDirectoryIsKnown()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var path = Path.Combine(root, "new.txt");
        File.WriteAllText(path, "new needle\n");
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "parent-1" });
        var writer = new FakeIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        resolver.PathsByFileId["56"] = path;
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("56", UsnReasonDataOverwrite, FileAttributes.Archive, "new.txt", parent: "parent-1")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        var upsert = Assert.Single(writer.Upserts);
        Assert.Equal(path, upsert.Path);
    }

    [Fact]
    public async Task CatchUpAsyncCoalescesRepeatedFileRecords()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var path = Path.Combine(root, "changed.txt");
        File.WriteAllText(path, "changed needle\n");
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "57" },
            DirectoryReferences: new[] { "1" });
        var writer = new FakeIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        resolver.PathsByFileId["57"] = path;
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("57", UsnReasonDataOverwrite, FileAttributes.Archive, "changed.txt"),
                Change("57", UsnReasonDataOverwrite, FileAttributes.Archive, "changed.txt"),
                Change("57", UsnReasonDataOverwrite, FileAttributes.Archive, "changed.txt")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Single(writer.Upserts);
        Assert.Empty(store.DeletedFileReferences);
    }

    [Fact]
    public async Task CatchUpAsyncCommitsReplayInBoundedBatches()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "known-dir" });
        var writer = new FakeReplayIndexWriter();
        var records = Enumerable.Range(0, 513)
            .Select(i => Change(
                $"unrelated-{i}",
                UsnReasonDataOverwrite,
                FileAttributes.Archive,
                string.Empty,
                parent: "unrelated-dir",
                usn: 10 + i))
            .ToArray();
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 1000), records));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Equal(new long[] { 522, 1000 }, writer.CommittedUsns);
        Assert.All(writer.BatchChanges, Assert.Empty);
        Assert.Equal(0, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncSkipsUnrelatedFileRecordWithoutResolvingPath()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "known-file" },
            DirectoryReferences: new[] { "known-dir" });
        var writer = new FakeIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("unrelated-file", UsnReasonDataOverwrite, FileAttributes.Archive, string.Empty, parent: "unrelated-dir")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Empty(writer.Upserts);
        Assert.Empty(store.DeletedFileReferences);
        Assert.Equal(0, resolver.ResolvePathCallCount);
    }

    [Fact]
    public async Task CatchUpAsyncEnsuresNewDirectoryWhenParentDirectoryIsKnown()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var directory = Path.Combine(root, "new-dir");
        Directory.CreateDirectory(directory);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "root-dir" });
        var writer = new FakeReplayIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        resolver.PathsByFileId["dir-1"] = directory;
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("dir-1", UsnReasonDataOverwrite, FileAttributes.Directory, "new-dir", parent: "root-dir")));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Empty(result.FallbackReasons);
        var change = Assert.Single(writer.BatchChanges.SelectMany(static batch => batch));
        Assert.Equal(IndexReplayChangeKind.EnsureDirectory, change.Kind);
        Assert.Equal(root, change.Root);
        Assert.Equal(directory, change.Path);
        Assert.Equal("dir-1", change.FileReferenceNumber);
    }

    [Fact]
    public async Task CatchUpAsyncReplaysFileCreatedUnderNewDirectory()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var directory = Path.Combine(root, "new-dir");
        var path = Path.Combine(directory, "new.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "nested needle\n");
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "root-dir" });
        var writer = new FakeReplayIndexWriter();
        var resolver = new FakeVolumeResolver(volume);
        resolver.PathsByFileId["dir-1"] = directory;
        resolver.PathsByFileId["file-1"] = path;
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            resolver,
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("dir-1", UsnReasonDataOverwrite, FileAttributes.Directory, "new-dir", parent: "root-dir", usn: 11),
                Change("file-1", UsnReasonDataOverwrite, FileAttributes.Archive, "new.txt", parent: "dir-1", usn: 12)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Empty(result.FallbackReasons);
        var changes = writer.BatchChanges.SelectMany(static batch => batch).ToArray();
        Assert.Contains(changes, change => change.Kind == IndexReplayChangeKind.EnsureDirectory && change.Path == directory);
        Assert.Contains(changes, change => change.Kind == IndexReplayChangeKind.Upsert && change.Path == path);
    }

    [Fact]
    public async Task CatchUpAsyncFallsBackWhenDirectoryReferencesAreMissing()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "55" });
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("directory identity", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatchUpAsyncFallsBackWhenRootIdentityChanged()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "root-dir" },
            RootIdentity: new IndexRootIdentity(volume.VolumeKey, "old-root", null));
        var resolver = new FakeVolumeResolver(volume);
        resolver.IdentitiesByPath[root] = new ResolvedFileIdentity("new-root", null);
        var service = new IndexStartupCatchUpService(
            new FakeIndexWriter(),
            store,
            resolver,
            new FakeJournalReader(new UsnJournalSnapshot(7, 1, 20)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.HandledRoots);
        Assert.Contains(root, result.FallbackReasons.Keys);
        Assert.Contains("identity", result.FallbackReasons[root], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.LastCommittedUsn);
    }

    [Fact]
    public async Task CatchUpAsyncAppliesDeleteByIdentity()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "77" },
            DirectoryReferences: new[] { "1" });
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
    public async Task CatchUpAsyncDeletesIdentityForUnresolvedFileRecord()
    {
        var root = IndexPath.NormalizeRoot(_root);
        var volume = CreateVolume(usnSupported: true);
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            FileReferences: new[] { "79" },
            DirectoryReferences: new[] { "1" });
        var writer = new FakeIndexWriter();
        var service = new IndexStartupCatchUpService(
            writer,
            store,
            new FakeVolumeResolver(volume),
            new FakeJournalReader(
                new UsnJournalSnapshot(7, 1, 20),
                Change("79", UsnReasonDataOverwrite, FileAttributes.Archive, string.Empty)));

        var result = await service.CatchUpAsync(
            new[] { new IndexedLocation(root, new WalkerOptions(), WatchEnabled: false) },
            TestContext.Current.CancellationToken);

        Assert.Contains(root, result.HandledRoots);
        Assert.Empty(result.FallbackReasons);
        Assert.Equal(new[] { "79" }, store.DeletedFileReferences);
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
            var store = new FakeCatchUpStore(
                new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
                FileReferences: new[] { "88" },
                DirectoryReferences: new[] { "1" });
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
        var store = new FakeCatchUpStore(
            new IndexVolumeCheckpoint(1, volume.VolumeKey, 7, 10, "healthy", null),
            DirectoryReferences: new[] { "1" });
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

    private static IndexVolumeInfo CreateVolume(
        bool usnSupported,
        bool isRemote = false,
        IndexVolumeDriveKind driveKind = IndexVolumeDriveKind.Fixed) =>
        new(
            "fake-volume",
            @"C:\",
            @"\\.\C:",
            "123",
            usnSupported ? "NTFS" : "exFAT",
            isRemote,
            usnSupported,
            driveKind);

    private static UsnChangeRecord Change(
        string fileReferenceNumber,
        uint reason,
        FileAttributes attributes,
        string name,
        string parent = "1",
        long usn = 11) =>
        new(
            fileReferenceNumber,
            parent,
            usn,
            DateTime.UtcNow,
            reason,
            attributes,
            name);

    private sealed class FakeVolumeResolver : IIndexVolumeResolver
    {
        private readonly IndexVolumeInfo _volume;

        public FakeVolumeResolver(IndexVolumeInfo volume) => _volume = volume;

        public Dictionary<string, string> PathsByFileId { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ResolvedFileIdentity> IdentitiesByPath { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int ResolvePathCallCount { get; private set; }

        public bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason)
        {
            volume = _volume;
            fallbackReason = string.Empty;
            return true;
        }

        public bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity)
        {
            return IdentitiesByPath.TryGetValue(IndexPath.NormalizeRoot(path), out identity);
        }

        public bool TryResolvePathFromFileId(
            IndexVolumeInfo volume,
            string fileReferenceNumber,
            out string path,
            out string fallbackReason)
        {
            ResolvePathCallCount++;
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
        private readonly IndexReplayReferenceSet _references;
        private readonly IndexRootIdentity? _rootIdentity;

        public FakeCatchUpStore(
            IndexVolumeCheckpoint? checkpoint,
            IReadOnlyCollection<string>? FileReferences = null,
            IReadOnlyCollection<string>? DirectoryReferences = null,
            IndexRootIdentity? RootIdentity = null)
        {
            _checkpoint = checkpoint;
            _rootIdentity = RootIdentity;
            _references = new IndexReplayReferenceSet(
                new HashSet<string>(FileReferences ?? Array.Empty<string>(), StringComparer.Ordinal),
                new HashSet<string>(DirectoryReferences ?? Array.Empty<string>(), StringComparer.Ordinal));
        }

        public long LastCommittedUsn { get; private set; }

        public bool RootProfileCurrent { get; set; } = true;

        public List<string> DeletedFileReferences { get; } = new();

        public Task<IndexVolumeCheckpoint?> GetVolumeCheckpointAsync(
            IndexVolumeInfo volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(_checkpoint);

        public Task<IndexReplayReferenceSet> GetReplayReferencesAsync(
            IndexVolumeInfo volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(_references);

        public Task<IndexRootIdentity?> GetRootIdentityAsync(
            string root,
            CancellationToken cancellationToken) =>
            Task.FromResult(_rootIdentity);

        public Task<bool> IsRootProfileCurrentAsync(
            IndexedLocation location,
            CancellationToken cancellationToken) =>
            Task.FromResult(RootProfileCurrent);

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

    private sealed class FakeReplayIndexWriter : IIndexWriter, IIndexReplayWriter
    {
        public List<IReadOnlyList<IndexReplayChange>> BatchChanges { get; } = new();

        public List<long> CommittedUsns { get; } = new();

        public Task ApplyReplayBatchAsync(
            IndexVolumeInfo volume,
            IReadOnlyCollection<IndexedLocation> locations,
            IReadOnlyList<IndexReplayChange> changes,
            ulong journalId,
            long lastCommittedUsn,
            string health,
            string? error,
            CancellationToken cancellationToken)
        {
            BatchChanges.Add(changes.ToArray());
            CommittedUsns.Add(lastCommittedUsn);
            return Task.CompletedTask;
        }

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
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
