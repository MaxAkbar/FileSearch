namespace FileSearch.Core.Engine;

public sealed record CandidateProviderAvailability(bool IsAvailable, string? Message = null)
{
    public static CandidateProviderAvailability Available { get; } = new(true);

    public static CandidateProviderAvailability Unavailable(string message) => new(false, message);
}
