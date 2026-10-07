using FileSearch.Core.Replacement;

namespace FileSearch.Core.Workflows;

/// <summary>Previews and applies replacement in a fresh folder scan or an earlier search's exact item set.</summary>
public sealed record ReplacementStep : WorkflowStep
{
    public string Find { get; init; } = "";
    public string ReplaceWith { get; init; } = "";
    public ReplacementTarget Target { get; init; }
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public ReplacementNameTarget NameTarget { get; init; } = ReplacementNameTarget.Both;
    public bool IncludeExtensions { get; init; }
    public bool IncludeFormulas { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
    public string? ScopeStepId { get; init; }
    public SearchFilters Filters { get; init; } = new();
    public IReadOnlyList<string> AdditionalTextExtensions { get; init; } = [];
}
