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
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        if (options is null) throw new ArgumentNullException(nameof(options));

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
                ShouldIncludePredicate = (ref FileSystemEntry e) => ShouldInclude(ref e, options),
            };

            foreach (var path in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }

    private static bool ShouldInclude(ref FileSystemEntry e, WalkerOptions options)
    {
        if (e.IsDirectory) return false;

        if (options.MinFileSizeBytes > 0 && e.Length < options.MinFileSizeBytes) return false;
        if (options.MaxFileSizeBytes > 0 && e.Length > options.MaxFileSizeBytes) return false;

        if (options.ModifiedAfterUtc is { } after && e.LastWriteTimeUtc < after) return false;
        if (options.ModifiedBeforeUtc is { } before && e.LastWriteTimeUtc > before) return false;

        if (!MatchesGlobs(e.FileName, options.IncludeGlobs, defaultIfEmpty: true)) return false;
        if (MatchesGlobs(e.FileName, options.ExcludeGlobs, defaultIfEmpty: false)) return false;

        return true;
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
