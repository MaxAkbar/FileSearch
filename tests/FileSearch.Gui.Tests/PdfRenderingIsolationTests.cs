using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FileSearch.Gui.Services;
using FileSearch.WindowsOcr;

namespace FileSearch.Gui.Tests;

public sealed class PdfRenderingIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.PdfIsolation", Guid.NewGuid().ToString("N"));

    public PdfRenderingIsolationTests() => PreviewFixtures.Create(_root);

    [Fact]
    public async Task WholePageRenderingLeavesTheNativePdfRuntimeOutsideTheCallerAndReleasesTheSource()
    {
        string path = Path.Combine(_root, "pages.pdf");
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        var preview = await DocumentPreviewService.LoadPdfPageAsync(path, 2, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentPreviewKind.Pdf, preview.Kind);
        Assert.Equal(2, preview.Page);
        Assert.Equal(2, preview.PageCount);
        Assert.NotNull(preview.Image);
        Assert.Equal(IntPtr.Zero, GetModuleHandle("Windows.Data.Pdf.dll"));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CancellationAfterWorkerStartReleasesTheSource()
    {
        string path = Path.Combine(_root, "pages.pdf");
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = WindowsPdfRenderer.RenderAsync(path, 1, cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task InvalidPdfReturnsAControlledErrorAndReleasesTheSource()
    {
        string path = Path.Combine(_root, "invalid.pdf");
        await File.WriteAllTextAsync(path, "This is not a PDF.", TestContext.Current.CancellationToken);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsPdfRenderer.RenderAsync(path, 1, TestContext.Current.CancellationToken));

        Assert.Equal(IntPtr.Zero, GetModuleHandle("Windows.Data.Pdf.dll"));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
