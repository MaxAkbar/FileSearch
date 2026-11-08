using System.Collections.Concurrent;
using CSharpDB.Engine;
using FileSearch.Core.Indexing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Tests;

public sealed class IndexDatabaseLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IdleDisposeFinishesBeforeExclusiveReopenWithoutPumpingTheCallingContext()
    {
        using var files = new LifetimeFiles();
        using var database = await files.CreateAsync();
        var context = new NonPumpingContext();
        try
        {
            await Task.Run(() =>
            {
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    database.Dispose();
                    Assert.Equal(0, context.PostCount);
                    AssertExclusiveAccess(files.Path);
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
        }
        finally { context.Release(); }
        Assert.Equal("before", (await ReadPersistedValuesAsync(files.Path))["lifecycle_marker"]);
    }

    [Fact]
    public async Task DisposeLeavesAnActiveReaderUsableAndDoesNotWaitForItsRelease()
    {
        using var files = new LifetimeFiles();
        using var database = await files.CreateAsync();
        using var lease = await database.OpenReadLeaseAsync(Token);
        Assert.NotNull(lease);
        try
        {
            await Task.Run(database.Dispose, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Null(await database.OpenReadLeaseAsync(Token));
            await using var result = await lease.Session.ExecuteReadAsync("SELECT value FROM meta WHERE name = 'lifecycle_marker'", Token);
            Assert.True(await result.MoveNextAsync(Token));
            Assert.Equal("before", result.Current[0].AsText);
        }
        finally { lease.Dispose(); }
        await WaitForExclusiveAccessAsync(files.Path);
        Assert.Equal("before", (await ReadPersistedValuesAsync(files.Path))["lifecycle_marker"]);
    }

    [Fact]
    public async Task ConcurrentDisposeLetsActiveAndAlreadyQueuedWritesFinishBeforeClosing()
    {
        using var files = new LifetimeFiles();
        using var database = await files.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = database.RunExclusiveWriteAsync(async db =>
        {
            entered.SetResult();
            await release.Task;
            await ExecuteAsync(db, "UPDATE meta SET value = 'after' WHERE name = 'lifecycle_marker'");
        }, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var queued = database.RunExclusiveWriteAsync(db =>
            ExecuteAsync(db, "UPDATE meta SET value = 'queued complete' WHERE name = 'queued_marker'"), Token);
        try
        {
            await Task.WhenAll(Task.Run(database.Dispose, Token), Task.Run(database.Dispose, Token))
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(writing.IsCompleted);
            Assert.False(queued.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(writing, queued).WaitAsync(TimeSpan.FromSeconds(15), Token);
        await WaitForExclusiveAccessAsync(files.Path);
        var values = await ReadPersistedValuesAsync(files.Path);
        Assert.Equal("after", values["lifecycle_marker"]);
        Assert.Equal("queued complete", values["queued_marker"]);
    }

    private static async Task ExecuteAsync(Database database, string sql)
    {
        await using var result = await database.ExecuteAsync(sql, Token);
    }

    private static async Task<Dictionary<string, string>> ReadPersistedValuesAsync(string path)
    {
        await using var reopened = await Database.OpenAsync(path, Token);
        await using var result = await reopened.ExecuteAsync("SELECT name, value FROM meta", Token);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await result.MoveNextAsync(Token)) values[result.Current[0].AsText] = result.Current[1].AsText;
        return values;
    }

    private static void AssertExclusiveAccess(string path)
    {
        foreach (string file in new[] { path, path + ".wal", path + ".shm" })
        {
            if (!File.Exists(file)) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static async Task WaitForExclusiveAccessAsync(string path)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            try { AssertExclusiveAccess(path); return; }
            catch (IOException) { await Task.Delay(10, deadline.Token); }
        }
    }

    private sealed class LifetimeFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileSearch.IndexLifetime", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "index.db");
        public async Task<IndexDatabase> CreateAsync()
        {
            var database = new IndexDatabase(Path, NullLogger.Instance);
            await database.RunExclusiveWriteAsync(async db =>
            {
                await ExecuteAsync(db, "INSERT INTO meta VALUES ('lifecycle_marker', 'before')");
                await ExecuteAsync(db, "INSERT INTO meta VALUES ('queued_marker', 'before')");
            }, Token);
            return database;
        }
        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly object _gate = new();
        private bool _released;
        private int _posts;
        public int PostCount => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            lock (_gate)
            {
                if (!_released) { _pending.Enqueue((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        public void Release()
        {
            lock (_gate) _released = true;
            while (_pending.TryDequeue(out var work)) ThreadPool.QueueUserWorkItem(_ => work.Callback(work.State));
        }
    }
}
