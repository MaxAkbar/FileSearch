using System.IO;

namespace FileSearch.Gui.Services;

internal readonly record struct PreviewSourceVersion(long Length, DateTime ModifiedUtc, DateTime CreatedUtc)
{
    internal static PreviewSourceVersion? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
