using System;
using System.Collections.Generic;

namespace FileSearch.Core.Walker;

public sealed record WalkerOptions
{
    public IReadOnlyList<string> IncludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> IncludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ExcludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool Recursive { get; init; } = true;
    public bool IncludeHidden { get; init; } = false;

    /// <summary>Files smaller than this are skipped. 0 disables the filter.</summary>
    public long MinFileSizeBytes { get; init; } = 0;

    /// <summary>Files larger than this are skipped. 0 disables the filter.</summary>
    public long MaxFileSizeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Only include files modified at or after this UTC time.</summary>
    public DateTime? ModifiedAfterUtc { get; init; }

    /// <summary>Only include files modified at or before this UTC time.</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }
}
