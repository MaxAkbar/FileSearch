namespace FileSearch.Core.Extractors;

/// <summary>Parent-owned parser scratch space, also removed after a host is terminated.</summary>
public sealed class ExtractionScratchDirectory : IAsyncDisposable
{
    private static readonly string Root = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileSearchExtraction", "Requests"));

    private ExtractionScratchDirectory(string path) => Path = path;

    public string Path { get; }

    public static ExtractionScratchDirectory Create()
    {
        string path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new ExtractionScratchDirectory(path);
    }

    public static string ValidateRequestDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("The archive parser requires parent-owned scratch space.");
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(fullPath), Root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Guid.TryParseExact(System.IO.Path.GetFileName(fullPath), "N", out _) ||
            !Directory.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The archive parser scratch directory is invalid.");
        return fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path)) return;
                // Revalidate the exact generated child before recursive cleanup.
                string target = ValidateRequestDirectory(Path);
                Directory.Delete(target, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
