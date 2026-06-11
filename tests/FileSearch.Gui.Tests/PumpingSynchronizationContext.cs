using System.Collections.Concurrent;
using System.Diagnostics;

namespace FileSearch.Gui.Tests;

/// <summary>
/// A test stand-in for the WPF dispatcher: posted continuations queue up and
/// run on the test thread when pumped. Lets view-model flows that use
/// ConfigureAwait(true) (e.g. the search drain loop) execute deterministically
/// in unit tests, with all UI-collection mutations on one thread.
/// </summary>
internal sealed class PumpingSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Runs queued continuations until the condition holds.</summary>
    public void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!done())
        {
            if (stopwatch.Elapsed > timeout)
                throw new TimeoutException($"Condition not met within {timeout} while pumping the test dispatcher.");

            if (_queue.TryTake(out var item, millisecondsTimeout: 25))
                item.Callback(item.State);
        }
    }
}
