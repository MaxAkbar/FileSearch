namespace FileSearch.Core.Engine;

public enum SearchExplanationSeverity
{
    Info,
    Warning,
    Disabled,
}

public sealed record SearchPlanExplanation(
    string Code,
    string Message,
    SearchExplanationSeverity Severity = SearchExplanationSeverity.Info,
    CandidateProviderKind Provider = CandidateProviderKind.None,
    RetrievalLayer Layer = RetrievalLayer.None);

