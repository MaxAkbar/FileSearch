using System.Globalization;

namespace FileSearch.Core.Queries;

/// <summary>
/// Ranks metadata candidates that have already passed the caller's literal
/// matching and location filters. This type never expands a query or determines
/// whether a candidate is eligible.
/// </summary>
public sealed class QuickSearchNameRanking
{
    internal const int MaxScoredDepth = 32;
    private const double TierSize = 10_000;
    private readonly string _phrase;
    private readonly string[] _terms;

    private QuickSearchNameRanking(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _phrase = string.Join(' ', terms);
        _terms = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Compile once per query; terms and punctuation remain literal.</summary>
    public static QuickSearchNameRanking Compile(string text) => new(text);

    /// <summary>Score an already eligible file or folder without accessing the filesystem.</summary>
    public double Score(string path, bool isDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        var value = path.AsSpan();
        foreach (string term in _terms)
            if (!value.Contains(term, StringComparison.OrdinalIgnoreCase)) return 0;
        while (value.Length > 0 && IsSeparator(value[^1])) value = value[..^1];
        int separator = value.LastIndexOfAny('\\', '/');
        return ScoreName(value[(separator + 1)..], isDirectory, BoundedPathDepth(value));
    }

    internal double ScoreName(ReadOnlySpan<char> name, bool isDirectory, int depth)
    {
        if (_terms.Length == 0 || name.Length == 0) return 0;

        int ownTerms = 0, completeTerms = 0;
        foreach (string term in _terms)
        {
            int first = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (first < 0) continue;
            ownTerms++;
            if (HasCompleteComponent(name, term, first)) completeTerms++;
        }

        var stem = name;
        int dot = name.LastIndexOf('.');
        if (!isDirectory && dot > 0) stem = name[..dot];
        int tier = name.Equals(_phrase, StringComparison.OrdinalIgnoreCase) ? 6
            : !isDirectory && stem.Equals(_phrase, StringComparison.OrdinalIgnoreCase) ? 5
            : name.StartsWith(_phrase, StringComparison.OrdinalIgnoreCase) ? 4
            : ownTerms == _terms.Length ? 3
            : ownTerms > 0 ? 2 : 1;

        // Component coverage contributes at most 2,048; the other signals at
        // most 767 combined. All remain below the 10,000 tier gap, preserving
        // exact/full-stem/phrase-prefix policy while preferring complete words
        // over embedded substrings within a tier.
        double componentBonus = 2048d * completeTerms / _terms.Length;
        double ownNameBonus = 256d * ownTerms / _terms.Length;
        int lengthBonus = 255 - Math.Min(name.Length, 255);
        int depthBonus = 256 - Math.Clamp(depth, 0, MaxScoredDepth) * 8;
        return tier * TierSize + componentBonus + ownNameBonus + lengthBonus + depthBonus;
    }

    private static bool HasCompleteComponent(ReadOnlySpan<char> name, string term, int start)
    {
        while (true)
        {
            if (IsComponentBoundary(name, start) && IsComponentBoundary(name, start + term.Length)) return true;
            int offset = start + 1;
            int next = name[offset..].IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (next < 0) return false;
            start = offset + next;
        }
    }

    private static bool IsComponentBoundary(ReadOnlySpan<char> name, int position)
    {
        if (position == 0 || position == name.Length) return true;
        char right = name[position];
        // Marks continue their base character. Keep original UTF-16 spelling;
        // boundary scoring performs no normalization or query expansion.
        if (IsMark(right)) return false;
        int previous = position - 1;
        while (previous >= 0 && IsMark(name[previous])) previous--;
        if (previous < 0) return false;
        char left = name[previous];
        if (!IsWordCharacter(left) || !IsWordCharacter(right)) return true;
        if (char.IsLower(left) && char.IsUpper(right)) return true;
        if (char.IsLetter(left) && char.IsDigit(right) || char.IsDigit(left) && char.IsLetter(right)) return true;
        if (!char.IsUpper(left) || !char.IsUpper(right)) return false;
        int next = position + 1;
        while (next < name.Length && IsMark(name[next])) next++;
        return next < name.Length && char.IsLower(name[next]);
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || char.IsSurrogate(value) || IsMark(value);

    private static bool IsMark(char value) => char.GetUnicodeCategory(value) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static int BoundedPathDepth(ReadOnlySpan<char> path)
    {
        int position = 0;
        if (path.Length >= 2 && path[1] == ':') position = 2;
        else if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            // UNC server/share together form the root, just as a drive prefix
            // does. They do not make otherwise equal network candidates deeper.
            position = 2;
            for (int component = 0; component < 2 && position < path.Length; component++)
            {
                while (position < path.Length && IsSeparator(path[position])) position++;
                while (position < path.Length && !IsSeparator(path[position])) position++;
            }
        }

        int depth = 0;
        while (position < path.Length && depth < MaxScoredDepth)
        {
            while (position < path.Length && IsSeparator(path[position])) position++;
            if (position == path.Length) break;
            depth++;
            while (position < path.Length && !IsSeparator(path[position])) position++;
        }
        return depth;
    }

    private static bool IsSeparator(char value) => value is '\\' or '/';
}
