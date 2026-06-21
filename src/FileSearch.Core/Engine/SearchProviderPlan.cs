namespace FileSearch.Core.Engine;

public sealed record SearchProviderPlan(
    CandidateProviderKind Provider,
    RetrievalLayer Layer,
    bool IsEnabled = true,
    string? Explanation = null,
    double Weight = 1);

