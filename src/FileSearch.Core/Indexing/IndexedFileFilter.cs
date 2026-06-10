using System;
using System.IO.Enumeration;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

internal static class IndexedFileFilter
{
    public static bool Matches(
        string path,
        string fileName,
        string extension,
        long sizeBytes,
        long modifiedUtcTicks,
        WalkerOptions options)
    {
        if (options.ExcludeDirectories.Count > 0 && HasExcludedDirectory(path, options.ExcludeDirectories))
            return false;

        if (options.MinFileSizeBytes > 0 && sizeBytes < options.MinFileSizeBytes)
            return false;

        if (options.MaxFileSizeBytes > 0 && sizeBytes > options.MaxFileSizeBytes)
            return false;

        if (options.ModifiedAfterUtc is { } after && modifiedUtcTicks < after.Ticks)
            return false;

        if (options.ModifiedBeforeUtc is { } before && modifiedUtcTicks > before.Ticks)
            return false;

        if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(extension))
            return false;

        if (options.ExcludeExtensions.Count > 0 && options.ExcludeExtensions.Contains(extension))
            return false;

        if (!MatchesGlobs(fileName, options.IncludeGlobs, defaultIfEmpty: true))
            return false;

        if (MatchesGlobs(fileName, options.ExcludeGlobs, defaultIfEmpty: false))
            return false;

        return true;
    }

    private static bool HasExcludedDirectory(
        string path,
        System.Collections.Generic.IReadOnlySet<string> excluded)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return false;

        foreach (var segment in directory.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (excluded.Contains(segment))
                return true;
        }

        return false;
    }

    private static bool MatchesGlobs(
        string fileName,
        System.Collections.Generic.IReadOnlyList<string> globs,
        bool defaultIfEmpty)
    {
        if (globs.Count == 0)
            return defaultIfEmpty;

        foreach (var glob in globs)
        {
            if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                return true;
        }

        return false;
    }
}
