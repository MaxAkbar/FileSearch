using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

public sealed record IndexingThrottle(int FilesPerPause, TimeSpan Pause)
{
    public static IndexingThrottle None { get; } = new(0, TimeSpan.Zero);

    public bool IsEnabled => FilesPerPause > 0 && Pause > TimeSpan.Zero;

    public Task PauseAfterFileAsync(long filesProcessed, CancellationToken cancellationToken)
    {
        if (!IsEnabled || filesProcessed <= 0 || filesProcessed % FilesPerPause != 0)
            return Task.CompletedTask;

        return Task.Delay(Pause, cancellationToken);
    }
}
