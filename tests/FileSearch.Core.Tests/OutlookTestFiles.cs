using System.IO.Compression;

namespace FileSearch.Core.Tests;

internal sealed class OutlookTestFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FileSearch.OutlookTests", Guid.NewGuid().ToString("N"));
    public OutlookTestFiles()
    {
        Directory.CreateDirectory(Root);
        ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Outlook", "mail-samples.zip"), Root);
    }
    public string PathFor(string name) => Path.Combine(Root, name);
    public void Dispose()
    {
        // CSharpDB can finish closing its database asynchronously. Mail handles
        // are tested separately with exclusive opens, without swallowing failures.
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
