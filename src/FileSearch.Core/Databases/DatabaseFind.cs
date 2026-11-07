using FileSearch.Core.Queries;

namespace FileSearch.Core.Databases;

public enum DatabaseFindMode { AllTerms, AnyTerm, Phrase }

/// <summary>Literal, cell-based matching and explicit upper bounds for a table scan.</summary>
public sealed record DatabaseFindOptions(
    string Query,
    DatabaseFindMode Mode = DatabaseFindMode.AllTerms,
    string? Column = null,
    bool MatchCase = false,
    int MaxRows = DatabaseFiles.MaxRows,
    int MaxCells = DatabaseFiles.MaxCells,
    int MaxCharacters = DatabaseFiles.MaxSearchCharacters,
    int MaxMatches = 1_000);

public sealed record DatabaseCellMatch(
    DatabaseLocation Location,
    IReadOnlyList<MatchSpan> Highlights,
    bool HighlightsTruncated = false);

/// <summary>Completed work is returned on a scan limit; cancellation and source changes throw.</summary>
public sealed record DatabaseFindResult(
    IReadOnlyList<DatabaseCellMatch> Matches,
    int RowsScanned,
    int CellsScanned,
    int CharactersScanned,
    bool IsComplete,
    string? LimitReason,
    bool ValuesTruncated);

internal sealed class DatabaseFindMatcher
{
    private const int MaximumQueryCharacters = 2_048;
    private const int MaximumTerms = 64;
    private const int MaximumHighlightSpans = 256;
    private readonly string[] _terms;
    private readonly bool _allTerms;
    private readonly StringComparison _comparison;

    public DatabaseFindMatcher(DatabaseFindOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query) || options.Query.Length > MaximumQueryCharacters)
            throw new ArgumentException("Enter Find text from 1 to 2,048 characters.", nameof(options));
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxRows is < 1 or > DatabaseFiles.MaxRows || options.MaxCells is < 1 or > DatabaseFiles.MaxCells ||
            options.MaxCharacters is < 1 or > DatabaseFiles.MaxSearchCharacters || options.MaxMatches is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Find limits must be positive and cannot exceed the preview scan budgets.");

        _comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _terms = options.Mode == DatabaseFindMode.Phrase ? [options.Query.Trim()] :
            options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(options.MatchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToArray();
        if (_terms.Length > MaximumTerms) throw new ArgumentException("Find supports up to 64 literal terms. Choose Phrase for one literal phrase.", nameof(options));
        _allTerms = options.Mode != DatabaseFindMode.AnyTerm;
    }

    public (IReadOnlyList<MatchSpan> Spans, bool Truncated)? Match(string value, CancellationToken token)
    {
        Span<int> firstIndexes = stackalloc int[_terms.Length];
        bool found = false;
        for (int termIndex = 0; termIndex < _terms.Length; termIndex++)
        {
            token.ThrowIfCancellationRequested();
            int index = value.IndexOf(_terms[termIndex], _comparison);
            if (index < 0 && _allTerms) return null;
            firstIndexes[termIndex] = index;
            found |= index >= 0;
        }
        if (!found) return null;

        // One bounded mask merges overlapping/adjacent occurrences without
        // accumulating an unbounded list for repeated one-character terms.
        var covered = new bool[value.Length];
        for (int termIndex = 0; termIndex < _terms.Length; termIndex++)
        {
            string term = _terms[termIndex];
            int index = firstIndexes[termIndex], previousEnd = 0, occurrences = 0;
            while (index >= 0)
            {
                if ((occurrences++ & 63) == 0) token.ThrowIfCancellationRequested();
                int end = index + term.Length;
                int start = Math.Max(index, previousEnd);
                if (end > start) covered.AsSpan(start, end - start).Fill(true);
                previousEnd = end;
                index = index + 1 < value.Length ? value.IndexOf(term, index + 1, _comparison) : -1;
            }
        }

        var spans = new List<MatchSpan>();
        for (int index = 0; index < covered.Length; index++)
        {
            if (!covered[index]) continue;
            if (spans.Count == MaximumHighlightSpans) return (spans, true);
            int start = index;
            while (index + 1 < covered.Length && covered[index + 1]) index++;
            spans.Add(new MatchSpan(start, index - start + 1));
        }
        return (spans, false);
    }
}
