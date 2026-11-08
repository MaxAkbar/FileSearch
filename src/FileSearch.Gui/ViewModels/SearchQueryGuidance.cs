using FileSearch.Core.Queries;

namespace FileSearch.Gui.ViewModels;

/// <summary>Explains the current parser interpretation without changing the query.</summary>
internal static class SearchQueryGuidance
{
    private const int MaximumDescriptionLength = 240;
    private const int MaximumParsedInputLength = 2_048;

    public static string Describe(string? input, QueryMode mode, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ModeHint(mode);

        if (mode == QueryMode.PlainText)
            return Limit($"Literal text: {Display(input)} · Quotes and operators are text.");

        if (mode == QueryMode.Semantic)
            return "Meaning search: the whole input describes a concept; a local Smart Search model and indexed vectors are required.";

        // A live hint must stay inexpensive while someone pastes a large or
        // deeply nested expression. The real search still parses all input.
        if (input.Length > MaximumParsedInputLength || input.Count(character => character == '(') > 32)
            return ModeHint(mode);

        try
        {
            var query = new QueryFactory().Build(input, mode, caseSensitive);
            return Limit(DescribeQuery(query));
        }
        catch (ArgumentException ex)
        {
            return InvalidQuery(ex.Message);
        }
        catch (FormatException ex)
        {
            return InvalidQuery(ex.Message);
        }
        catch (OverflowException ex)
        {
            return InvalidQuery(ex.Message);
        }
    }

    private static string DescribeQuery(Query query)
    {
        if (query is not UnifiedQuery unified)
            return DescribeExpression(query);

        var filterHint = unified.Filters.HasFileFilters ? " · File filters apply." : string.Empty;
        if (unified.HasSemantic)
        {
            var textHint = unified.HasContentCriteria ? "Text and meaning search" : "Meaning search";
            return $"{textHint}: a local Smart Search model and indexed vectors are required.{filterHint}";
        }

        if (!unified.HasContentCriteria)
            return "File filters only; no content text is required.";

        if (unified.HasMailCriteria)
        {
            var textHint = unified.HasTextCriteria ? "Mail fields and content" : "Mail fields only";
            return $"{textHint}: match one message; AND, OR and NOT preserve groups. Sent dates use UTC; modified/created/size describe the physical file.{filterHint}";
        }

        if (unified.ContentQuery is RegexQuery &&
            unified.Chips.FirstOrDefault(chip => chip.Field == "Wildcard") is { } wildcard)
        {
            return $"Wildcard text: {Display(wildcard.Value)}{filterHint}";
        }

        return DescribeExpression(unified.ContentQuery) + filterHint;
    }

    private static string DescribeExpression(Query query) => query switch
    {
        TermQuery term => term.Term.Any(char.IsWhiteSpace)
            ? $"Phrase: {Display(term.Term)}"
            : $"Text contains: {Display(term.Term)}",
        AndQuery andQuery when andQuery.Children.All(child => child is TermQuery) =>
            $"All terms (AND): {DescribeTerms(andQuery.Children)}",
        OrQuery orQuery when orQuery.Children.All(child => child is TermQuery) =>
            $"Either term (OR): {DescribeTerms(orQuery.Children)}",
        AndQuery => "Combined expression: AND requires every clause; groups and NOT are preserved.",
        OrQuery => "Alternative expression: OR accepts any clause; groups and NOT are preserved.",
        NotQuery => "Exclude expression (NOT): the following term or group must not match.",
        RegexQuery regex => $"Regular expression: {Display(regex.Pattern)}",
        FuzzyQuery fuzzy => $"Similar word: {Display(fuzzy.Term)} · Up to {fuzzy.MaxEdits} edits.",
        NearQuery near => $"Nearby terms: at most {near.MaxDistance} words between their matches.",
        MatchAllQuery => "No content text is required by this expression.",
        _ => "Matching follows the selected search mode.",
    };

    private static string DescribeTerms(IReadOnlyList<Query> children) =>
        string.Join(", ", children.Take(6).Cast<TermQuery>().Select(term =>
            term.Term.Any(char.IsWhiteSpace) ? $"\"{Display(term.Term)}\"" : Display(term.Term))) +
        (children.Count > 6 ? ", …" : string.Empty);

    private static string ModeHint(QueryMode mode) => mode switch
    {
        QueryMode.Unified => "Unified: adjacent terms use AND; OR allows alternatives; quotes keep a phrase. Fields filter files or individual mail messages.",
        QueryMode.Boolean => "Boolean: adjacent terms use AND; OR allows alternatives; quotes keep a phrase.",
        QueryMode.PlainText => "Plain text: search the whole input literally, including spaces, quotes and operators.",
        QueryMode.Regex => "Regular expression: search using the pattern as typed.",
        QueryMode.Semantic => "Meaning search: a local Smart Search model and indexed vectors are required.",
        _ => "Choose a search mode.",
    };

    private static string InvalidQuery(string message) =>
        Limit("Incomplete or invalid query: " + message);

    private static string Display(string value) =>
        Limit(Limit(value, 120).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal), 120);

    private static string Limit(string value, int maximumLength = MaximumDescriptionLength)
    {
        if (value.Length <= maximumLength)
            return value;

        int length = maximumLength - 1;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length] + "…";
    }
}
