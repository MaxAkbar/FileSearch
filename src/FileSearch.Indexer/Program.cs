using FileSearch.Core.Indexing;
using Forms = System.Windows.Forms;

namespace FileSearch.Indexer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
