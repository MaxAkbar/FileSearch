using System.Text.RegularExpressions;

namespace FileSearch.Core.Replacement;

internal sealed class ReplacementMatcher
{
    private readonly Regex _regex;
    private readonly ReplacementRequest _request;

    public ReplacementMatcher(ReplacementRequest request)
    {
        if (string.IsNullOrEmpty(request.Find))
            throw new ArgumentException("Find text must not be empty.");
        _request = request;
        _regex = new Regex(request.UseRegex ? request.Find : Regex.Escape(request.Find),
            RegexOptions.CultureInvariant | (request.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase),
            TimeSpan.FromSeconds(2));
    }

    public IReadOnlyList<TextReplacement> FindChanges(string text) => _regex.Matches(text).Cast<Match>()
        .Select(match => new TextReplacement(match.Index, match.Length,
            _request.UseRegex ? match.Result(_request.ReplaceWith) : _request.ReplaceWith))
        .Where(change => text.AsSpan(change.Start, change.Length).SequenceEqual(change.Text.AsSpan()) == false)
        .ToArray();

    public string Replace(string text, out int count)
    {
        var changes = FindChanges(text);
        count = changes.Count;
        return ApplyChanges(text, changes);
    }

    public static string ApplyChanges(string text, IReadOnlyList<TextReplacement> changes)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var change in changes)
        {
            builder.Append(text.AsSpan(position, change.Start - position));
            builder.Append(change.Text);
            position = change.Start + change.Length;
        }
        builder.Append(text.AsSpan(position));
        return builder.ToString();
    }
}

internal sealed record TextReplacement(int Start, int Length, string Text);
internal sealed record ContentReplacement(byte[] Bytes, int Count, IReadOnlyList<ReplacementChange> Changes);
