using System.Windows.Threading;

namespace FileSearch.Gui.Tests;

internal static class StaTestDispatcher
{
    internal static void Run(Action action) =>
        RunAsync(action, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    internal static Task RunAsync(Action action, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync(() => { action(); return Task.CompletedTask; }, timeout, cancellationToken);

    internal static async Task RunAsync(Func<Task> action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        int shutdownRequested = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var currentDispatcher = Dispatcher.CurrentDispatcher;
                Volatile.Write(ref dispatcher, currentDispatcher);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(currentDispatcher));
                if (Volatile.Read(ref shutdownRequested) != 0)
                    return;

                currentDispatcher.BeginInvoke(async () =>
                {
                    try { await action(); }
                    catch (Exception error) { failure = error; }
                    finally { currentDispatcher.InvokeShutdown(); }
                });
                Dispatcher.Run();
            }
            catch (Exception error) { failure ??= error; }
            finally
            {
                try
                {
                    if (dispatcher is { HasShutdownFinished: false })
                        dispatcher.InvokeShutdown();
                }
                catch (Exception error) { failure ??= error; }
                SynchronizationContext.SetSynchronizationContext(null);
                // WPF must purge its thread-owned weak events before the test can finish.
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref shutdownRequested, 1);
            var ownedDispatcher = Volatile.Read(ref dispatcher);
            if (ownedDispatcher is { HasShutdownStarted: false })
                ownedDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            // Completion runs inside the thread's finally; join also covers its actual exit.
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test STA thread did not exit after dispatcher shutdown.");
        }
    }
}
