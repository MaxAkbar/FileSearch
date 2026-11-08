using FileSearch.WindowsOcr;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer.CsWinRT;

namespace FileSearch.PowerToys;

internal static class Program
{
    [MTAThread]
    private static void Main(string[] args)
    {
        if (WindowsPdfRenderer.TryRunWorker(args))
            return;

        // Only Command Palette's COM activation starts the companion. No tray
        // process, startup registration, or dependency on PowerToys in the GUI.
        if (!IsComServerActivation(args))
            return;

        using var stopped = new ManualResetEvent(false);
        var server = new Shmuelie.WinRTServer.ComServer();
        using var extension = new FileSearchExtension(stopped);
        server.RegisterClass<FileSearchExtension, IExtension>(() => extension);
        server.Start();
        stopped.WaitOne();
        server.Stop();
        server.UnsafeDispose();
    }

    internal static bool IsComServerActivation(string[] args) =>
        args.Contains("-RegisterProcessAsComServer", StringComparer.Ordinal);
}
