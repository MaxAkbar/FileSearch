using System;
using System.Collections.Generic;

namespace FileSearch.Core.Walker;

public sealed record WalkerOptions
{
    /// <summary>
    /// Directory names whose entire subtrees are pruned from traversal.
    /// Defaults to folders that are huge and almost never the search target;
    /// pass an empty set to walk everything. Note: bin/obj are deliberately
    /// not excluded by default — build output can contain files users search.
    /// </summary>
    public static IReadOnlySet<string> DefaultExcludeDirectories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "node_modules" };

    public IReadOnlyList<string> IncludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> IncludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ExcludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory names pruned from traversal (see <see cref="DefaultExcludeDirectories"/>).</summary>
    public IReadOnlySet<string> ExcludeDirectories { get; init; } = DefaultExcludeDirectories;
    public bool Recursive { get; init; } = true;
    public bool IncludeHidden { get; init; }

    /// <summary>Files smaller than this are skipped. 0 disables the filter.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>Files larger than this are skipped. 0 disables the filter.</summary>
    public long MaxFileSizeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Only include files modified at or after this UTC time.</summary>
    public DateTime? ModifiedAfterUtc { get; init; }

    /// <summary>Only include files modified at or before this UTC time.</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }
}
