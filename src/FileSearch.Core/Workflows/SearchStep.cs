using System;
using System.Collections.Generic;
using System.Linq;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Runs a search and records its results under the step id so later steps can
/// reference them (conditions, sub-searches, exports, file operations).
/// With <see cref="ScopeStepId"/> set, the search runs only over the files
/// matched by that earlier step (a sub-search) instead of walking the
/// filesystem under <see cref="Roots"/>.
/// </summary>
public sealed record SearchStep : WorkflowStep
{
    /// <summary>Query text; supports <c>${variable}</c> substitution.</summary>
    public string Query { get; init; } = "";

    public QueryMode Mode { get; init; } = QueryMode.PlainText;

    public bool CaseSensitive { get; init; }

    /// <summary>
    /// Directories to search. Ignored when <see cref="ScopeStepId"/> is set.
    /// Supports <c>${variable}</c> substitution.
    /// </summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Id of an earlier search step; when set, this step searches only the
    /// files that step matched (sub-search over previous results).
    /// </summary>
    public string? ScopeStepId { get; init; }

    /// <summary>Prefer the index when coverage allows (filesystem searches only).</summary>
    public bool UseIndex { get; init; }

    /// <summary>
    /// File filters for filesystem searches. Not applied when scoped to
    /// previous results — that file set is already fixed.
    /// </summary>
    public SearchFilters Filters { get; init; } = new();

    /// <summary>Stop the search after this many hits; 0 means unlimited.</summary>
    public int MaxHits { get; init; }
}

/// <summary>
/// The serializable subset of <see cref="WalkerOptions"/> exposed to workflow
/// authors; kept separate so the on-disk format does not change when engine
/// internals do.
/// </summary>
public sealed record SearchFilters
{
    public IReadOnlyList<string> IncludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Directory names whose subtrees are pruned. Null applies the engine
    /// default (<see cref="WalkerOptions.DefaultExcludeDirectories"/>: .git,
    /// .vs, node_modules); an empty list walks everything.
    /// </summary>
    public IReadOnlyList<string>? ExcludeDirectories { get; init; }

    public bool Recursive { get; init; } = true;
    public bool IncludeHidden { get; init; }

    /// <summary>Files smaller than this are skipped. 0 disables the filter.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>Files larger than this are skipped. 0 disables the filter.</summary>
    public long MaxFileSizeBytes { get; init; } = WalkerOptions.DefaultMaxFileSizeBytes;

    public DateTime? ModifiedAfterUtc { get; init; }
    public DateTime? ModifiedBeforeUtc { get; init; }

    public WalkerOptions ToWalkerOptions(IReadOnlyList<string> includeGlobs, IReadOnlyList<string> excludeGlobs) => new()
    {
        IncludeGlobs = includeGlobs,
        ExcludeGlobs = excludeGlobs,
        ExcludeDirectories = ExcludeDirectories is null
            ? WalkerOptions.DefaultExcludeDirectories
            : new HashSet<string>(ExcludeDirectories, StringComparer.OrdinalIgnoreCase),
        Recursive = Recursive,
        IncludeHidden = IncludeHidden,
        MinFileSizeBytes = MinFileSizeBytes,
        MaxFileSizeBytes = MaxFileSizeBytes,
        ModifiedAfterUtc = ModifiedAfterUtc,
        ModifiedBeforeUtc = ModifiedBeforeUtc,
    };
}
