using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Queries;

internal sealed record NaturalQueryInterpretation(string QueryText, IReadOnlyList<UnifiedQueryChip> Chips);

internal static partial class NaturalLanguageQueryInterpreter
{
    private static readonly HashSet<string> s_stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "and",
        "are",
        "by",
        "document",
        "documents",
        "doc",
        "docs",
        "file",
        "files",
        "for",
        "from",
        "in",
        "is",
        "of",
        "on",
        "the",
        "that",
        "to",
        "with",
    };

    private static readonly HashSet<string> s_semanticTriggerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "describing",
        "related",
        "semantic",
        "semantically",
        "similar",
    };

    private static readonly Dictionary<string, string> s_typeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["archive"] = "archive",
        ["archives"] = "archive",
        ["code"] = "code",
        ["docx"] = "docx",
        ["email"] = "email",
        ["emails"] = "email",
        ["excel"] = "excel",
        ["epub"] = "epub",
        ["image"] = "image",
        ["images"] = "image",
        ["jpeg"] = "image",
        ["jpg"] = "image",
        ["pdf"] = "pdf",
        ["pdfs"] = "pdf",
        ["photo"] = "image",
        ["photos"] = "image",
        ["picture"] = "image",
        ["pictures"] = "image",
        ["powerpoint"] = "powerpoint",
        ["presentation"] = "powerpoint",
        ["presentations"] = "powerpoint",
        ["spreadsheet"] = "excel",
        ["spreadsheets"] = "excel",
        ["word"] = "word",
        ["zip"] = "archive",
        ["zips"] = "archive",
    };

    private static readonly HashSet<string> s_dateFieldWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "changed",
        "created",
        "made",
        "modified",
        "updated",
    };

    public static bool TryInterpret(string input, out NaturalQueryInterpretation interpretation)
    {
        interpretation = null!;
        if (string.IsNullOrWhiteSpace(input) || LooksExplicit(input))
            return false;

        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            return false;

        var consumed = new bool[tokens.Count];
        var fragments = new List<Fragment>();
        var chips = new List<UnifiedQueryChip>();
        var hasStructuredInterpretation = false;

        InterpretTypeWords(tokens, consumed, fragments, chips, ref hasStructuredInterpretation);
        InterpretDatePhrases(tokens, consumed, fragments, chips, ref hasStructuredInterpretation);
        InterpretSizePhrases(tokens, consumed, fragments, chips, ref hasStructuredInterpretation);
        InterpretSemanticPhrases(tokens, consumed, fragments, chips, ref hasStructuredInterpretation);
        InterpretContentWords(tokens, consumed, fragments, chips);

        if (!hasStructuredInterpretation || fragments.Count == 0)
            return false;

        var orderedFragments = fragments
            .OrderBy(fragment => fragment.Position)
            .Select(fragment => fragment.Text);
        var orderedChips = chips
            .OrderBy(chip => chip.Position)
            .ToArray();
        interpretation = new NaturalQueryInterpretation(string.Join(' ', orderedFragments), orderedChips);
        return true;
    }

    private static bool LooksExplicit(string input)
    {
        if (input.Contains('"') ||
            input.Contains('(') ||
            input.Contains(')') ||
            input.Contains('/'))
        {
            return true;
        }

        if (Regex.IsMatch(input, @"\b(?:AND|OR|NOT|NEAR/\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            input,
            @"\b(?:name|path|content|semantic|ext|type|modified|created|size|folder|root|status|extractor|regex):",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void InterpretTypeWords(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips,
        ref bool hasStructuredInterpretation)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i] || !s_typeWords.TryGetValue(tokens[i].Normalized, out var type))
                continue;

            consumed[i] = true;
            AddField("Type", "type", type, tokens[i], fragments, chips);
            hasStructuredInterpretation = true;
        }
    }

    private static void InterpretDatePhrases(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips,
        ref bool hasStructuredInterpretation)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            var field = DateFieldFor(tokens[i].Normalized);
            if (field is not null)
            {
                if (TryReadDateValue(tokens, i + 1, out var value, out var end))
                {
                    AddDateField(field.Value.Display, field.Value.QueryField, value, i, end, tokens, consumed, fragments, chips);
                    hasStructuredInterpretation = true;
                }

                continue;
            }

            if (TryReadDateValue(tokens, i, out var bareValue, out var bareEnd) && IsRelativeDateValue(bareValue))
            {
                AddDateField("Modified", "modified", bareValue, i, bareEnd, tokens, consumed, fragments, chips);
                hasStructuredInterpretation = true;
            }
        }
    }

    private static void InterpretSizePhrases(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips,
        ref bool hasStructuredInterpretation)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            var normalized = tokens[i].Normalized;
            if (normalized == "between" &&
                i + 3 < tokens.Count &&
                IsSizeToken(tokens[i + 1].Normalized) &&
                tokens[i + 2].Normalized == "and" &&
                IsSizeToken(tokens[i + 3].Normalized))
            {
                var value = tokens[i + 1].Raw + ".." + tokens[i + 3].Raw;
                AddDateLikeField("Size", "size", value, i, i + 3, tokens, consumed, fragments, chips);
                hasStructuredInterpretation = true;
                continue;
            }

            var op = SizeOperatorFor(normalized);
            if (op is null)
                continue;

            var valueIndex = i + 1;
            if (valueIndex < tokens.Count && tokens[valueIndex].Normalized == "than")
                valueIndex++;

            if (valueIndex >= tokens.Count || !IsSizeToken(tokens[valueIndex].Normalized))
                continue;

            AddDateLikeField("Size", "size", op + tokens[valueIndex].Raw, i, valueIndex, tokens, consumed, fragments, chips);
            hasStructuredInterpretation = true;
        }
    }

    private static void InterpretContentWords(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i] || ShouldSkipContentToken(tokens[i].Normalized))
                continue;

            var value = NormalizeContentWord(tokens[i].Raw);
            if (value.Length == 0)
                continue;

            fragments.Add(new Fragment(tokens[i].Position, QuoteIfNeeded(value)));
            chips.Add(new UnifiedQueryChip("Content", value, tokens[i].Raw, tokens[i].Position, tokens[i].Length));
        }
    }

    private static void InterpretSemanticPhrases(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips,
        ref bool hasStructuredInterpretation)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            var normalized = tokens[i].Normalized;
            int valueStart;
            if (normalized is "about" or "describing")
            {
                valueStart = i + 1;
            }
            else if (normalized is "related" or "similar" &&
                     i + 1 < tokens.Count &&
                     tokens[i + 1].Normalized == "to")
            {
                valueStart = i + 2;
            }
            else if (normalized is "semantic" or "semantically")
            {
                valueStart = i + 1;
                if (valueStart < tokens.Count && tokens[valueStart].Normalized == "search")
                    valueStart++;
                if (valueStart < tokens.Count && tokens[valueStart].Normalized == "for")
                    valueStart++;
            }
            else
            {
                continue;
            }

            if (!TryBuildSemanticValue(tokens, consumed, valueStart, out var value, out var valueEnd))
                continue;

            for (var j = i; j <= valueEnd; j++)
                consumed[j] = true;

            var position = tokens[i].Position;
            var length = tokens[valueEnd].Position + tokens[valueEnd].Length - position;
            var rawText = tokens[i].Source.Substring(position, length);
            fragments.Add(new Fragment(position, $"semantic:{QuoteIfNeeded(value)}"));
            chips.Add(new UnifiedQueryChip(
                "Semantic",
                value,
                rawText,
                position,
                length,
                IsEnabled: false,
                UnifiedQuery.SemanticUnavailableMessage));
            hasStructuredInterpretation = true;
        }
    }

    private static bool TryBuildSemanticValue(
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        int start,
        out string value,
        out int end)
    {
        var terms = new List<string>();
        end = start;
        for (var i = start; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            var normalized = tokens[i].Normalized;
            if (s_semanticTriggerWords.Contains(normalized) ||
                s_dateFieldWords.Contains(normalized) ||
                SizeOperatorFor(normalized) is not null ||
                IsSizeToken(normalized))
            {
                continue;
            }

            if (s_stopWords.Contains(normalized))
                continue;

            terms.Add(NormalizeContentWord(tokens[i].Raw));
            end = i;
        }

        terms = terms.Where(term => term.Length > 0).ToList();
        value = string.Join(' ', terms);
        return value.Length > 0;
    }

    private static (string Display, string QueryField)? DateFieldFor(string value) =>
        value switch
        {
            "created" or "made" => ("Created", "created"),
            "changed" or "modified" or "updated" => ("Modified", "modified"),
            _ => null,
        };

    private static bool TryReadDateValue(
        IReadOnlyList<Token> tokens,
        int start,
        out string value,
        out int end)
    {
        value = string.Empty;
        end = start;
        if (start >= tokens.Count)
            return false;

        var first = tokens[start].Normalized;
        if (first is "before" or "after" or "since" && start + 1 < tokens.Count)
        {
            if (TryReadSingleDate(tokens[start + 1], out var dateValue))
            {
                value = (first == "before" ? "<" : ">") + dateValue;
                end = start + 1;
                return true;
            }

            return false;
        }

        if (first is "last" or "this" && start + 1 < tokens.Count)
        {
            var period = NormalizePeriod(tokens[start + 1].Normalized);
            if (period is not null)
            {
                value = first + "-" + period;
                end = start + 1;
                return true;
            }
        }

        if (TryReadSingleDate(tokens[start], out value))
        {
            end = start;
            return true;
        }

        return false;
    }

    private static bool TryReadSingleDate(Token token, out string value)
    {
        value = token.Normalized;
        if (value is "today" or "yesterday" or "last-week" or "last-month" or "last-year" or
            "this-week" or "this-month" or "this-year" or "last-spring" or "last-summer" or
            "last-fall" or "last-autumn" or "last-winter" or "this-spring" or "this-summer" or
            "this-fall" or "this-autumn" or "this-winter")
        {
            return true;
        }

        if (YearRegex().IsMatch(value) || IsoDateRegex().IsMatch(value))
            return true;

        var period = NormalizePeriod(value);
        if (period is not null)
        {
            value = "this-" + period;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? NormalizePeriod(string value) =>
        value switch
        {
            "autumn" => "autumn",
            "fall" => "fall",
            "month" => "month",
            "spring" => "spring",
            "summer" => "summer",
            "week" => "week",
            "winter" => "winter",
            "year" => "year",
            _ => null,
        };

    private static bool IsRelativeDateValue(string value) =>
        value.StartsWith("last-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("this-", StringComparison.OrdinalIgnoreCase) ||
        value is "today" or "yesterday";

    private static string? SizeOperatorFor(string value) =>
        value switch
        {
            "above" or "bigger" or "greater" or "larger" or "over" => ">",
            "below" or "less" or "smaller" or "under" => "<",
            _ => null,
        };

    private static bool IsSizeToken(string value) =>
        SizeTokenRegex().IsMatch(value);

    private static void AddField(
        string displayField,
        string queryField,
        string value,
        Token token,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips)
    {
        fragments.Add(new Fragment(token.Position, $"{queryField}:{QuoteIfNeeded(value)}"));
        chips.Add(new UnifiedQueryChip(displayField, value, token.Raw, token.Position, token.Length));
    }

    private static void AddDateField(
        string displayField,
        string queryField,
        string value,
        int start,
        int end,
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips) =>
        AddDateLikeField(displayField, queryField, value, start, end, tokens, consumed, fragments, chips);

    private static void AddDateLikeField(
        string displayField,
        string queryField,
        string value,
        int start,
        int end,
        IReadOnlyList<Token> tokens,
        bool[] consumed,
        List<Fragment> fragments,
        List<UnifiedQueryChip> chips)
    {
        for (var i = start; i <= end && i < consumed.Length; i++)
            consumed[i] = true;

        var position = tokens[start].Position;
        var length = tokens[end].Position + tokens[end].Length - position;
        var rawText = tokens[start].Source.Substring(position, length);
        fragments.Add(new Fragment(position, $"{queryField}:{QuoteIfNeeded(value)}"));
        chips.Add(new UnifiedQueryChip(displayField, value, rawText, position, length));
    }

    private static bool ShouldSkipContentToken(string value) =>
        s_stopWords.Contains(value) ||
        s_semanticTriggerWords.Contains(value) ||
        s_dateFieldWords.Contains(value) ||
        value is "after" or "before" or "since" or "last" or "this" or "than" ||
        SizeOperatorFor(value) is not null ||
        NormalizePeriod(value) is not null ||
        IsSizeToken(value);

    private static string NormalizeContentWord(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 4 && trimmed.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^3] + "y";
        if (trimmed.Length > 3 && trimmed.EndsWith('s') && !trimmed.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^1];
        return trimmed;
    }

    private static string QuoteIfNeeded(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        return trimmed.Any(ch => char.IsWhiteSpace(ch) || ch is '(' or ')' or '"')
            ? "\"" + trimmed.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : trimmed;
    }

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        foreach (Match match in TokenRegex().Matches(input))
            tokens.Add(new Token(input, match.Value, match.Value.ToLowerInvariant(), match.Index, match.Length));
        return tokens;
    }

    private sealed record Fragment(int Position, string Text);

    private sealed record Token(string Source, string Raw, string Normalized, int Position, int Length);

    [GeneratedRegex(@"[\p{L}\p{Nd}_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?(?:b|kb|mb|gb|tb|k|m|g|t)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeTokenRegex();
}
