using System;
using System.IO;

namespace FileSearch.Core.Indexing;

public static class IndexPath
{
    public static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.IsNullOrEmpty(root) && trimmed.Length < root.Length
            ? root
            : trimmed;
    }

    public static string NormalizeFile(string path) => Path.GetFullPath(path);

    public static bool EqualsPath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
