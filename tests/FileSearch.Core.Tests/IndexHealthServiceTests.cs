using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class IndexHealthServiceTests
{
    [Fact]
    public async Task GetHealthAsyncReportsQueuedRootAsCatchingUp()
    {
        var root = CreateTempRoot();
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var now = DateTime.UtcNow;
        var index = new FakeHealthFileIndex
        {
            Locations =
            [
                new IndexedLocationInfo(
                    normalizedRoot,
                    FileCount: 12,
                    LineCount: 40,
                    IndexedUtc: now.AddMinutes(-10),
                    Profile: "profile",
                    Exists: true,
                    LastFullScanUtc: now.AddMinutes(-10),
                    VolumeKey: "volume-1"),
            ],
            DatabaseInfo = DatabaseInfo(
                rootStrategies:
                [
                    new IndexRootStrategyInfo(
                        normalizedRoot,
                        IndexLocationKind.LocalUsn,
                        IndexUpdateStrategy.UsnJournalAndWatcher,
                        "Local NTFS/ReFS: USN journal + watcher",
                        string.Empty,
                        UsnCatchUpEnabled: true,
                        WatcherRecommended: true),
                ],
                volumeHealth:
                [
                    new IndexVolumeHealthInfo(
                        "volume-1",
                        "NTFS",
                        IsRemote: false,
                        UsnSupported: true,
                        JournalId: 10,
                        LastCommittedUsn: 900,
                        Health: "healthy",
                        LastError: null,
                        LastCheckedUtc: now),
                ]),
            PendingChanges =
            [
                new PendingIndexChange(1, normalizedRoot, null, IndexChangeKind.RefreshRoot),
            ],
        };
        var runtimeStatus = new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: false,
            QueueLength: 3,
            Message: "Queued",
            QueuedRootCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedRoot] = 3,
            },
            WatcherDiagnostics: new Dictionary<string, IndexWatcherDiagnosticInfo>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedRoot] = new IndexWatcherDiagnosticInfo(normalizedRoot, true, now.AddSeconds(-5), null, null),
            });

        try
        {
            var service = new IndexHealthService(index);
            var health = await service.GetHealthAsync(
                [new IndexedLocation(normalizedRoot, new WalkerOptions())],
                runtimeStatus,
                TestContext.Current.CancellationToken);

            var row = Assert.Single(health.Roots);
            Assert.Equal(IndexHealthStatus.CatchingUp, row.Status);
            Assert.Equal(12, row.FilesIndexed);
            Assert.Equal(3, row.FilesPending);
            Assert.Equal(3, row.QueueDepth);
            Assert.Equal(900, row.LastCommittedUsn);
            Assert.Equal(now.AddSeconds(-5), row.LastWatcherEventUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetHealthAsyncReportsMissingConfiguredRootAsOffline()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-health-offline-" + Guid.NewGuid().ToString("N"));
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var service = new IndexHealthService(new FakeHealthFileIndex());

        var health = await service.GetHealthAsync(
            [new IndexedLocation(normalizedRoot, new WalkerOptions())],
            new IndexingStatus(false, false, false, 0, "Idle"),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(health.Roots);
        Assert.Equal(IndexHealthStatus.Offline, row.Status);
    }

    [Fact]
    public async Task GetHealthAsyncMapsRetainedJournalGapToJournalExpired()
    {
        var root = CreateTempRoot();
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var index = new FakeHealthFileIndex
        {
            Locations =
            [
                new IndexedLocationInfo(
                    normalizedRoot,
                    FileCount: 1,
                    LineCount: 1,
                    IndexedUtc: DateTime.UtcNow,
                    Profile: "profile",
                    Exists: true,
                    VolumeKey: "volume-1"),
            ],
            DatabaseInfo = DatabaseInfo(
                rootStrategies:
                [
                    new IndexRootStrategyInfo(
                        normalizedRoot,
                        IndexLocationKind.LocalUsn,
                        IndexUpdateStrategy.UsnJournalAndWatcher,
                        "Local NTFS/ReFS: USN journal + watcher",
                        string.Empty,
                        UsnCatchUpEnabled: true,
                        WatcherRecommended: true),
                ]),
        };
        var runtimeStatus = new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: false,
            QueueLength: 0,
            Message: "Idle",
            RootStatusDetails: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedRoot] = "Snapshot scan queued: Saved USN checkpoint is older than the retained journal.",
            });

        try
        {
            var service = new IndexHealthService(index);
            var health = await service.GetHealthAsync(
                [new IndexedLocation(normalizedRoot, new WalkerOptions())],
                runtimeStatus,
                TestContext.Current.CancellationToken);

            var row = Assert.Single(health.Roots);
            Assert.Equal(IndexHealthStatus.JournalExpired, row.Status);
            Assert.Equal("Journal expired", row.JournalStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetHealthAsyncWarnsWhenExtractorFailuresExceedThreshold()
    {
        var root = CreateTempRoot();
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var index = new FakeHealthFileIndex
        {
            Locations =
            [
                new IndexedLocationInfo(
                    normalizedRoot,
                    FileCount: 100,
                    LineCount: 100,
                    IndexedUtc: DateTime.UtcNow,
                    Profile: "profile",
                    Exists: true),
            ],
            Failures = Enumerable.Range(0, 25)
                .Select(i => new IndexFailureInfo(
                    normalizedRoot,
                    Path.Combine(normalizedRoot, $"failed-{i}.pdf"),
                    "extractor",
                    "1",
                    "failed",
                    1,
                    DateTime.UtcNow))
                .ToArray(),
        };

        try
        {
            var service = new IndexHealthService(index);
            var health = await service.GetHealthAsync(
                [new IndexedLocation(normalizedRoot, new WalkerOptions())],
                new IndexingStatus(true, false, false, 0, "Idle"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(health.Roots);
            Assert.Equal(IndexHealthStatus.TooManyExtractorFailures, row.Status);
            Assert.Equal(25, row.FilesFailed);
            Assert.Contains("25", row.ExtractorFailures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetHealthAsyncReportsValidationDriftAsNeedsFullScan()
    {
        var root = CreateTempRoot();
        var normalizedRoot = IndexPath.NormalizeRoot(root);
        var index = new FakeHealthFileIndex
        {
            Locations =
            [
                new IndexedLocationInfo(
                    normalizedRoot,
                    FileCount: 10,
                    LineCount: 20,
                    IndexedUtc: DateTime.UtcNow,
                    Profile: "profile",
                    Exists: true,
                    LastFullValidationUtc: DateTime.UtcNow,
                    LastValidationStatus: IndexValidationStatus.DriftDetected.ToString(),
                    LastValidationMessage: "Drift detected: 1 missing, 0 changed, 0 removed, 0 failed checks.",
                    LastValidationFilesChecked: 11,
                    LastValidationMissingFromIndexCount: 1),
            ],
        };

        try
        {
            var service = new IndexHealthService(index);
            var health = await service.GetHealthAsync(
                [new IndexedLocation(normalizedRoot, new WalkerOptions())],
                new IndexingStatus(true, false, false, 0, "Idle"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(health.Roots);
            Assert.Equal(IndexHealthStatus.NeedsFullScan, row.Status);
            Assert.Contains("1 missing", row.ValidationSummary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IndexDatabaseInfo DatabaseInfo(
        IReadOnlyList<IndexRootStrategyInfo>? rootStrategies = null,
        IReadOnlyList<IndexVolumeHealthInfo>? volumeHealth = null) =>
        new(
            string.Empty,
            Exists: true,
            IsCompatible: true,
            SchemaVersion: IndexDatabase.CurrentSchemaVersion,
            DatabaseBytes: 0,
            WalBytes: 0,
            ShmBytes: 0,
            LocationCount: 0,
            TotalFileCount: 0,
            TotalLineCount: 0,
            PendingChangeCount: 0,
            LastIndexedUtc: null,
            VolumeHealth: volumeHealth,
            RootStrategies: rootStrategies);

    private sealed class FakeHealthFileIndex : IFileIndex
    {
        public string DatabasePath => DatabaseInfo.DatabasePath;

        public IReadOnlyList<IndexedLocationInfo> Locations { get; init; } = Array.Empty<IndexedLocationInfo>();

        public IndexDatabaseInfo DatabaseInfo { get; init; } = IndexHealthServiceTests.DatabaseInfo();

        public IReadOnlyList<IndexFailureInfo> Failures { get; init; } = Array.Empty<IndexFailureInfo>();

        public IReadOnlyList<PendingIndexChange> PendingChanges { get; init; } = Array.Empty<PendingIndexChange>();

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexCoverage(IndexCoverageStatus.Missing, "fake"));

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Locations);

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(DatabaseInfo);

        public Task<IReadOnlyList<IndexFailureInfo>> GetFailedFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Failures);

        public Task ExportFailedFilesAsync(
            string path,
            IndexFailureExportFormat format,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePendingChangeAsync(
            string root,
            string? path,
            IndexChangeKind kind,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(PendingChanges);

        public Task RemovePendingChangeAsync(
            string root,
            string? path,
            IndexChangeKind kind,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
