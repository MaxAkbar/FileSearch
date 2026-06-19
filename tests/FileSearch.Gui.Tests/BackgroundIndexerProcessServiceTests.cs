using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class BackgroundIndexerProcessServiceTests
{
    [Fact]
    public async Task SendsQuickCommandsWithShortTimeout()
    {
        var calls = new List<IpcCall>();
        var service = CreateService(calls);

        await service.GetStatusAsync(TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal(BackgroundIndexerCommand.GetStatus, call.Command);
        Assert.Equal(TimeSpan.FromSeconds(1), call.Timeout);
    }

    [Fact]
    public async Task SendsMutationCommandsWithLongerTimeout()
    {
        var calls = new List<IpcCall>();
        var service = CreateService(calls);

        await service.RemoveLocationAsync(@"C:\Code", TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal(BackgroundIndexerCommand.RemoveLocation, call.Command);
        Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
    }

    [Fact]
    public async Task SendsMaintenanceCommandsWithMaintenanceTimeout()
    {
        var calls = new List<IpcCall>();
        var service = CreateService(calls);

        await service.CompactDatabaseAsync(TestContext.Current.CancellationToken);
        await service.ValidateRootAsync(
            new IndexedLocation(Path.GetTempPath(), new WalkerOptions(), WatchEnabled: false),
            TestContext.Current.CancellationToken);

        Assert.Contains(calls, call =>
            call.Command == BackgroundIndexerCommand.CompactDatabase &&
            call.Timeout == TimeSpan.FromSeconds(90));
        Assert.Contains(calls, call =>
            call.Command == BackgroundIndexerCommand.ValidateRoot &&
            call.Timeout == TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task ShutdownReturnsFalseWhenWorkerStillRespondsAfterPolling()
    {
        var calls = new List<IpcCall>();
        var service = CreateService(calls);

        var stopped = await service.ShutdownIfRunningAsync(TestContext.Current.CancellationToken);

        Assert.False(stopped);
        Assert.Equal(1, calls.Count(call => call.Command == BackgroundIndexerCommand.Shutdown));
        Assert.Equal(15, calls.Count(call => call.Command == BackgroundIndexerCommand.Ping));
        Assert.Contains(calls, call =>
            call.Command == BackgroundIndexerCommand.Shutdown &&
            call.Timeout == TimeSpan.FromSeconds(5));
    }

    private static BackgroundIndexerProcessService CreateService(List<IpcCall> calls) =>
        new(
            (request, timeout, _) =>
            {
                calls.Add(new IpcCall(request.Command, timeout));
                var validation = request.Command == BackgroundIndexerCommand.ValidateRoot
                    ? IndexValidationResult.Create(
                        request.Location?.Root ?? string.Empty,
                        DateTime.UtcNow,
                        filesChecked: 0,
                        filesMatched: 0,
                        missingFromIndex: 0,
                        changedSinceIndex: 0,
                        missingFromDisk: 0,
                        failedChecks: 0)
                    : null;

                return Task.FromResult<BackgroundIndexerResponse?>(new BackgroundIndexerResponse(
                    true,
                    "ok",
                    ValidationResult: validation));
            },
            (_, _) => Task.CompletedTask);

    private sealed record IpcCall(BackgroundIndexerCommand Command, TimeSpan Timeout);
}
