namespace FileSearch.Gui.Services;

internal readonly record struct PreviewTextMatch(int Start, int Length)
{
    public int End => Start + Length;
}

[Flags]
internal enum PreviewTextFindLimits { None = 0, TextLength = 1, MatchCount = 2 }

internal sealed record PreviewTextFindResult(
    IReadOnlyList<PreviewTextMatch> Matches, PreviewTextFindLimits Limits, int TextCharactersSearched)
{
    public bool IsComplete => Limits == PreviewTextFindLimits.None;
    public string? LimitReason => Limits switch
    {
        PreviewTextFindLimits.None => null,
        PreviewTextFindLimits.TextLength => "Only the first 4,000,000 characters were searched.",
        PreviewTextFindLimits.MatchCount => "Only the first 10,000 matches are listed.",
        _ => "Only the first 4,000,000 characters were searched; only the first 10,000 matches are listed.",
    };
}

/// <summary>Literal, non-overlapping Find over bounded preview text, using UTF-16 offsets.</summary>
internal static class PreviewTextFind
{
    public const int MaxQueryCharacters = 2048;
    public const int MaxTextCharacters = 4_000_000;
    public const int MaxMatches = 10_000;
    private const int SearchWindowCharacters = 16 * 1024;

    public static PreviewTextFindResult Find(string text, string query, bool matchCase, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        token.ThrowIfCancellationRequested();
        if (query.Length > MaxQueryCharacters) throw new ArgumentOutOfRangeException(nameof(query), "Find text is limited to 2,048 characters.");
        if (query.Length == 0) return new([], PreviewTextFindLimits.None, 0);

        int length = Math.Min(text.Length, MaxTextCharacters);
        if (!IsBoundary(text, length)) length--;
        var limits = length < text.Length ? PreviewTextFindLimits.TextLength : PreviewTextFindLimits.None;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<PreviewTextMatch>();
        int position = 0;
        int searched = 0;
        while (position <= length - query.Length)
        {
            token.ThrowIfCancellationRequested();
            // Include the query overlap so a match crossing a window edge is not lost.
            int windowLength = Math.Min(length - position, SearchWindowCharacters + query.Length - 1);
            int found = text.IndexOf(query, position, windowLength, comparison);
            if (found < 0)
            {
                searched = position + windowLength;
                position += windowLength - query.Length + 1;
                continue;
            }
            int end = found + query.Length;
            searched = end;
            if (!IsBoundary(text, found) || !IsBoundary(text, end))
            {
                position = found + 1;
                continue;
            }
            if (matches.Count == MaxMatches)
            {
                limits |= PreviewTextFindLimits.MatchCount;
                break;
            }
            matches.Add(new PreviewTextMatch(found, query.Length));
            position = end;
        }
        token.ThrowIfCancellationRequested();
        return new(matches.AsReadOnly(), limits, (limits & PreviewTextFindLimits.MatchCount) != 0 ? searched : length);
    }

    private static bool IsBoundary(string text, int offset) => offset <= 0 || offset >= text.Length ||
        !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);
}
