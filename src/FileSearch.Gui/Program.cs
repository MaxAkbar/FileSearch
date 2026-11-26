using System.Runtime.CompilerServices;
using FileSearch.Core.Logging;
using FileSearch.Gui.Services;
using FileSearch.WindowsOcr;

namespace FileSearch.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var role = WindowsPdfRenderer.IsWorkerInvocation(args) ? CrashProcessRole.PdfRenderWorker
            : NativePreviewHandler.IsWorkerInvocation(args) ? CrashProcessRole.NativePreviewWorker
            : CrashProcessRole.Desktop;
        CrashDiagnostics.Initialize(role);
        RunApplication();
    }

    // Let the crash handler observe failures loading or initializing WPF itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
