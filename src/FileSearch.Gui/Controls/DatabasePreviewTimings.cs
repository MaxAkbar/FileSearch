using System.Diagnostics;

namespace FileSearch.Gui.Controls;

public sealed partial class DatabasePreviewControl
{
    // Attached only by the diagnostic probe. Ordinary previews neither allocate a
    // timing object nor read the clock. Names ending in ".scheduled" are events.
    internal Action<string, double>? PagePhaseMeasured { get; set; }

    private static long StartPreviewPhase(Action<string, double>? measured) =>
        measured is null ? 0 : Stopwatch.GetTimestamp();

    private static void FinishPreviewPhase(Action<string, double>? measured, string name, long started)
    {
        if (measured is not null) measured(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
