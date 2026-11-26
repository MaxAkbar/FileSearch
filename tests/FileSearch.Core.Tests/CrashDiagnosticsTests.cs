using FileSearch.Core.Logging;

namespace FileSearch.Core.Tests;

public sealed class CrashDiagnosticsTests
{
    [Fact]
    public void SummaryIncludesManagedFramesAndInnerCauseWithoutUserContent()
    {
        const string privateText = @"C:\private\user-query.txt";
        var cause = Assert.Throws<IOException>(() => ThrowReadError(privateText));
        var wrapper = new InvalidOperationException(privateText, cause);
        wrapper.Data["query"] = privateText;

        var report = CrashDiagnostics.FormatReport(CrashProcessRole.PdfRenderWorker, "RenderPage", "1.2.3", "FileSearch.Gui", wrapper);

        Assert.Contains("Role: PdfRenderWorker", report);
        Assert.Contains("Operation: RenderPage", report);
        Assert.Contains("System.InvalidOperationException", report);
        Assert.Contains("System.IO.IOException", report);
        Assert.Contains(nameof(ThrowReadError), report);
        Assert.DoesNotContain(privateText, report);
        Assert.DoesNotContain(".cs:line", report);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowReadError(string message) => throw new IOException(message);
}
