using System.Security.Cryptography;
using CSharpDB.Engine;
using FileSearch.Core.Databases;
using Microsoft.Data.Sqlite;

namespace FileSearch.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabasePreparationProgressCollection
{
    public const string Name = "Database preparation progress";
}

[Collection(DatabasePreparationProgressCollection.Name)]
public sealed class DatabasePreparationProgressTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchDatabasePreparationTests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string SnapshotRoot => Path.Combine(Path.GetTempPath(), "FileSearchDatabasePreview");

    public DatabasePreparationProgressTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task ProgressReportsPreparationInOrderAndCountsTheCompletePrivateCopy(DatabaseKind kind)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "progress.db", kind, 3, Token);
        var before = SourceState(path);
        var events = new List<DatabasePreparationProgress>();
        var progress = new SynchronousProgress(events.Add);
        string snapshot;
        await using (var session = await DatabaseReadSession.OpenAsync(path, null, progress, Token))
        {
            snapshot = session.SnapshotPath;
            Assert.Equal("3", (await session.ReadPageAsync(DatabaseTestFiles.TableName, 3, 1, Token)).Rows[0].Values[0]);
            AssertPreparationEvents(events, new FileInfo(path).Length);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot)));
        Assert.Equal(before, SourceState(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task ProgressIncludesWalBytesAndReadsCommittedWalRecordsWithoutChangingSource(DatabaseKind kind)
    {
        string path = await CreateStableWalSourceAsync(kind);
        string wal = path + (kind == DatabaseKind.Sqlite ? "-wal" : ".wal");
        long walBytes = new FileInfo(wal).Length;
        Assert.True(walBytes > 0);
        var before = SourceState(path);
        var events = new List<DatabasePreparationProgress>();
        string snapshot;
        await using (var session = await DatabaseReadSession.OpenAsync(path, null, new SynchronousProgress(events.Add), Token))
        {
            snapshot = session.SnapshotPath;
            Assert.Equal("committed marker", (await session.ReadPageAsync("Data", 1, 1, Token)).Rows[0].Values[1]);
            AssertPreparationEvents(events, new FileInfo(path).Length + walBytes);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot)));
        Assert.Equal(before, SourceState(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, DatabasePreparationStage.Snapshot)]
    [InlineData(DatabaseKind.Sqlite, DatabasePreparationStage.Engine)]
    [InlineData(DatabaseKind.Sqlite, DatabasePreparationStage.Schema)]
    [InlineData(DatabaseKind.Sqlite, DatabasePreparationStage.Validation)]
    [InlineData(DatabaseKind.CSharpDb, DatabasePreparationStage.Snapshot)]
    [InlineData(DatabaseKind.CSharpDb, DatabasePreparationStage.Engine)]
    [InlineData(DatabaseKind.CSharpDb, DatabasePreparationStage.Schema)]
    [InlineData(DatabaseKind.CSharpDb, DatabasePreparationStage.Validation)]
    public async Task SynchronousProgressCancellationStopsAtTheRequestedStageAndCleansTheSnapshot(DatabaseKind kind, DatabasePreparationStage stage)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "cancel.db", kind, 3, Token);
        var before = SourceState(path);
        var existingSnapshots = SnapshotDirectories();
        var ownedSnapshots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<DatabasePreparationProgress>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new SynchronousProgress(value =>
        {
            events.Add(value);
            ownedSnapshots.UnionWith(SnapshotDirectories().Except(existingSnapshots));
            if (value.Stage == stage) cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseReadSession.OpenAsync(path, null, progress, cancel.Token));
        Assert.Equal(stage, events[^1].Stage);
        Assert.DoesNotContain(events, value => (int)value.Stage > (int)stage);
        if (stage == DatabasePreparationStage.Snapshot)
        {
            Assert.Single(events);
            Assert.Equal(0, events[0].BytesCopied);
        }
        else Assert.Single(ownedSnapshots);
        Assert.All(ownedSnapshots, directory => Assert.False(Directory.Exists(directory)));
        Assert.Empty(SnapshotDirectories().Except(existingSnapshots));
        Assert.Equal(before, SourceState(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task CancellationDuringAMultiMegabyteCopyReportsPartialBytesAndRemovesItsPartialFile(DatabaseKind kind)
    {
        string path = await CreateLargeSourceAsync(kind);
        Assert.True(new FileInfo(path).Length > 2 * 1024 * 1024);
        var before = SourceState(path);
        var existingSnapshots = SnapshotDirectories();
        var ownedSnapshots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<DatabasePreparationProgress>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new SynchronousProgress(value =>
        {
            events.Add(value);
            ownedSnapshots.UnionWith(SnapshotDirectories().Except(existingSnapshots));
            if (value.Stage == DatabasePreparationStage.Snapshot && value.BytesCopied > 0 && value.BytesCopied < value.TotalBytes)
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseReadSession.OpenAsync(path, null, progress, cancel.Token));
        Assert.All(events, value => Assert.Equal(DatabasePreparationStage.Snapshot, value.Stage));
        Assert.Equal(0, events[0].BytesCopied);
        Assert.InRange(events[^1].BytesCopied, 1, events[^1].TotalBytes - 1);
        Assert.Single(ownedSnapshots);
        Assert.All(ownedSnapshots, directory => Assert.False(Directory.Exists(directory)));
        Assert.Empty(SnapshotDirectories().Except(existingSnapshots));
        Assert.Equal(before, SourceState(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    private static void AssertPreparationEvents(IReadOnlyList<DatabasePreparationProgress> events, long bytes)
    {
        Assert.Equal(new[] { DatabasePreparationStage.Snapshot, DatabasePreparationStage.Engine, DatabasePreparationStage.Schema, DatabasePreparationStage.Validation }, events.Select(value => value.Stage).Distinct());
        Assert.All(events.Zip(events.Skip(1)), pair => Assert.True((int)pair.First.Stage <= (int)pair.Second.Stage));
        var copies = events.Where(value => value.Stage == DatabasePreparationStage.Snapshot).ToArray();
        Assert.True(copies.Length >= 2);
        Assert.Equal(0, copies[0].BytesCopied);
        Assert.Equal(bytes, copies[^1].BytesCopied);
        Assert.All(copies, value => Assert.Equal(bytes, value.TotalBytes));
        Assert.All(copies.Zip(copies.Skip(1)), pair => Assert.True(pair.First.BytesCopied <= pair.Second.BytesCopied));
    }

    private async Task<string> CreateStableWalSourceAsync(DatabaseKind kind)
    {
        string writerPath = Path.Combine(_root, "writer.db"), path = Path.Combine(_root, "wal-source.db");
        if (kind == DatabaseKind.Sqlite)
        {
            await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = writerPath, Pooling = false }.ToString());
            await writer.OpenAsync(Token);
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO Data VALUES (9,'committed marker')";
            await command.ExecuteNonQueryAsync(Token);
            File.Copy(writerPath, path); File.Copy(writerPath + "-wal", path + "-wal");
        }
        else
        {
            await using var writer = await Database.OpenAsync(writerPath, Token);
            await using (var result = await writer.ExecuteAsync("CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT)", Token)) { }
            await using (var result = await writer.ExecuteAsync("INSERT INTO Data VALUES (9,'committed marker')", Token)) { }
            File.Copy(writerPath, path); File.Copy(writerPath + ".wal", path + ".wal");
        }
        return path;
    }

    private async Task<string> CreateLargeSourceAsync(DatabaseKind kind)
    {
        string path = Path.Combine(_root, "large.db");
        string payload = new('x', 600 * 1024);
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT)"; await command.ExecuteNonQueryAsync(Token);
            for (int id = 1; id <= 4; id++)
            {
                command.CommandText = $"INSERT INTO Data VALUES ({id},'{payload}')";
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            await using (var result = await database.ExecuteAsync("CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT)", Token)) { }
            for (int id = 1; id <= 4; id++)
            {
                await using var result = await database.ExecuteAsync($"INSERT INTO Data VALUES ({id},'{payload}')", Token);
            }
        }
        return path;
    }

    private static HashSet<string> SnapshotDirectories() => Directory.Exists(SnapshotRoot)
        ? new HashSet<string>(Directory.EnumerateDirectories(SnapshotRoot), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

    private static string[] SourceState(string path) => new[] { "", "-wal", ".wal", "-shm", ".shm", "-journal" }.Select(suffix =>
    {
        string member = path + suffix;
        return File.Exists(member) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(member))) + ":" + File.GetLastWriteTimeUtc(member).Ticks : "missing";
    }).ToArray();

    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, CancellationToken.None); }
        }
    }

    private sealed class SynchronousProgress(Action<DatabasePreparationProgress> report) : IProgress<DatabasePreparationProgress>
    {
        public void Report(DatabasePreparationProgress value) => report(value);
    }
}
