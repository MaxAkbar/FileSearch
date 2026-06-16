using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;

namespace FileSearch.Core.Walker;

/// <summary>
/// Walks the filesystem using <see cref="FileSystemEnumerable{T}"/>, applying
/// include/exclude glob, size, and modified-date filters without buffering
/// the full tree.
/// </summary>
public sealed class FileWalker : IFileWalker
{
    public IEnumerable<string> Enumerate(
        IEnumerable<string> roots,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var enumerable = new FileSystemEnumerable<string>(
                root,
                (ref FileSystemEntry entry) => entry.ToFullPath(),
                new EnumerationOptions
                {
                    RecurseSubdirectories = options.Recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = options.IncludeHidden
                        ? FileAttributes.None
                        : FileAttributes.Hidden | FileAttributes.System,
                    ReturnSpecialDirectories = false,
                })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => ShouldInclude(ref e, root, options),
                // Prune excluded directories entirely — their subtrees are
                // never enumerated, which is what makes skipping .git or
                // node_modules cheap instead of a per-file filter.
                ShouldRecursePredicate = (ref FileSystemEntry e) =>
                    options.ExcludeDirectories.Count == 0 ||
                    !options.ExcludeDirectories.Contains(e.FileName.ToString()),
            };

            foreach (var path in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }

    private static bool ShouldInclude(ref FileSystemEntry e, string root, WalkerOptions options)
    {
        if (e.IsDirectory) return false;

        if (options.IncludeDirectories.Count > 0 &&
            !HasDirectorySegment(e.ToFullPath(), root, options.IncludeDirectories))
        {
            return false;
        }

        if (options.MinFileSizeBytes > 0 && e.Length < options.MinFileSizeBytes) return false;
        if (options.MaxFileSizeBytes > 0 && e.Length > options.MaxFileSizeBytes) return false;

        if (options.ModifiedAfterUtc is { } after && e.LastWriteTimeUtc < after) return false;
        if (options.ModifiedBeforeUtc is { } before && e.LastWriteTimeUtc > before) return false;

        if (options.IncludeExtensions.Count > 0 || options.ExcludeExtensions.Count > 0)
        {
            // Only materialize the extension string when a filter needs it.
            var extension = Path.GetExtension(e.FileName).ToString();
            if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(extension)) return false;
            if (options.ExcludeExtensions.Count > 0 && options.ExcludeExtensions.Contains(extension)) return false;
        }

        if (!MatchesGlobs(e.FileName, options.IncludeGlobs, defaultIfEmpty: true)) return false;
        if (MatchesGlobs(e.FileName, options.ExcludeGlobs, defaultIfEmpty: false)) return false;

        return true;
    }

    private static bool HasDirectorySegment(
        string path,
        string root,
        IReadOnlySet<string> directoryNames)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(rootName) && directoryNames.Contains(rootName))
            return true;

        var relative = Path.GetRelativePath(root, directory);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return false;

        foreach (var segment in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (directoryNames.Contains(segment))
                return true;
        }

        return false;
    }

    private static bool MatchesGlobs(
        ReadOnlySpan<char> fileName,
        IReadOnlyList<string> globs,
        bool defaultIfEmpty)
    {
        if (globs.Count == 0) return defaultIfEmpty;
        foreach (var glob in globs)
        {
            if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                return true;
        }
        return false;
    }
}
