using System.Windows.Threading;

namespace FileSearch.Gui.Tests;

public sealed class StaTestDispatcherTests
{
    [Fact]
    public void SynchronousActionPreservesCustomContextAndClosesItsOwnedDispatcher()
    {
        var original = SynchronizationContext.Current;
        Dispatcher? dispatcher = null;
        Thread? thread = null;
        StaTestDispatcher.Run(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            thread = Thread.CurrentThread;
            var previous = SynchronizationContext.Current;
            var custom = new PumpingSynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(custom);
                Assert.Same(custom, SynchronizationContext.Current);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });

        Assert.Same(original, SynchronizationContext.Current);
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.NotNull(thread);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public async Task CompletesOnlyAfterDispatcherShutdownAndThreadExit()
    {
        Dispatcher? dispatcher = null;
        Thread? thread = null;
        await StaTestDispatcher.RunAsync(async () =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            thread = Thread.CurrentThread;
            Assert.Equal(ApartmentState.STA, thread.GetApartmentState());
            await Task.Yield();
            Assert.Same(dispatcher, Dispatcher.CurrentDispatcher);
        }, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.NotNull(thread);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public async Task PreservesActionFailureAfterDispatcherShutdownAndThreadExit()
    {
        Dispatcher? dispatcher = null;
        Thread? thread = null;
        var expected = new InvalidOperationException("Expected test action failure.");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => StaTestDispatcher.RunAsync((Action)(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            thread = Thread.CurrentThread;
            throw expected;
        }), TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.NotNull(thread);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public async Task TimeoutShutsDownDispatcherAndJoinsThread()
    {
        Dispatcher? dispatcher = null;
        Thread? thread = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => StaTestDispatcher.RunAsync(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            thread = Thread.CurrentThread;
            return pending.Task;
        }, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.NotNull(thread);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public async Task CancellationShutsDownDispatcherAndJoinsThread()
    {
        Dispatcher? dispatcher = null;
        Thread? thread = null;
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StaTestDispatcher.RunAsync(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            thread = Thread.CurrentThread;
            cancellation.Cancel();
            return pending.Task;
        }, TimeSpan.FromSeconds(20), cancellation.Token));

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.NotNull(thread);
        Assert.False(thread.IsAlive);
    }
}
