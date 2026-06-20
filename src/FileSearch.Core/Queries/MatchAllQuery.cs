namespace FileSearch.Core.Queries;

/// <summary>
/// Query node used when a structured query only constrains file metadata.
/// </summary>
public sealed class MatchAllQuery : Query
{
    public static MatchAllQuery Instance { get; } = new();

    private MatchAllQuery()
    {
    }

    public override bool IsMatch(string line) => true;
}
