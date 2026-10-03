using FileSearch.Core.Indexing;
using FileSearch.Core.Volumes;
using Forms = System.Windows.Forms;

namespace FileSearch.Indexer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The elevated drive-scan helper runs before the single-instance
        // guard: it is a one-shot process, not a second tray indexer.
        if (VolumeScanHelper.IsScanCommand(args))
        {
            Environment.ExitCode = VolumeScanHelper.Run(args);
            return;
        }

        using var singleInstance = new WorkerSingleInstance();
        if (!singleInstance.IsPrimary)
        {
            _ = BackgroundIndexerClient.TrySendAsync(
                    new BackgroundIndexerRequest(BackgroundIndexerCommand.Ping),
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var context = new IndexerApplicationContext(args);
        Forms.Application.Run(context);
    }
}
