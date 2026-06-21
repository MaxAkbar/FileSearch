namespace FileSearch.Core.Engine;

public sealed record SearchResultExplanation(
    string Code,
    string Message,
    CandidateProviderKind Provider = CandidateProviderKind.None,
    double ScoreContribution = 0);

