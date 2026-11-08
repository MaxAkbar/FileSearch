using System.Globalization;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Queries;

public enum MailQueryField
{
    From,
    To,
    Cc,
    Recipient,
    Subject,
    Sent,
}

/// <summary>A predicate on a message's metadata, never on words in its body.</summary>
public sealed class MailFieldQuery : Query
{
    private readonly StringComparison _comparison;
    private readonly DateTime? _afterUtc;
    private readonly DateTime? _beforeUtc;
    private readonly bool _includeAfter;
    private readonly bool _includeBefore;

    public MailFieldQuery(MailQueryField field, string value, bool caseSensitive = false)
    {
        if (!Enum.IsDefined(field))
            throw new ArgumentOutOfRangeException(nameof(field));
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"Mail field '{field.ToString().ToLowerInvariant()}:' requires a value.");
        Field = field;
        Value = value.Trim();
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (field == MailQueryField.Sent)
            (_afterUtc, _includeAfter, _beforeUtc, _includeBefore) = ParseDateRange(Value);
    }

    public MailQueryField Field { get; }
    public string Value { get; }

    // A metadata-free caller cannot turn a negated field into a match-all query.
    // UnifiedQuery evaluates the complete Boolean tree with message metadata.
    public override bool IsMatch(string line) => false;

    public bool Matches(MailMessageMetadata message) => Field switch
    {
        MailQueryField.From => message.From.Contains(Value, _comparison),
        MailQueryField.To => message.To.Contains(Value, _comparison),
        MailQueryField.Cc => message.Cc.Contains(Value, _comparison),
        MailQueryField.Recipient => message.To.Contains(Value, _comparison) || message.Cc.Contains(Value, _comparison),
        MailQueryField.Subject => message.Subject.Contains(Value, _comparison),
        MailQueryField.Sent => MatchesDate(message.SentUtc),
        _ => false,
    };

    private bool MatchesDate(DateTime? date)
    {
        if (date is null)
            return false;
        var utc = date.Value.Kind == DateTimeKind.Local
            ? date.Value.ToUniversalTime()
            : DateTime.SpecifyKind(date.Value, DateTimeKind.Utc);
        if (_afterUtc is { } after && (utc < after || (!_includeAfter && utc == after)))
            return false;
        if (_beforeUtc is { } before && (utc > before || (!_includeBefore && utc == before)))
            return false;
        return true;
    }

    private static (DateTime? AfterUtc, bool IncludeAfter, DateTime? BeforeUtc, bool IncludeBefore) ParseDateRange(string value)
    {
        var range = value.Split("..", StringSplitOptions.None);
        if (range.Length > 1)
        {
            if (range.Length != 2 || (range[0].Length == 0 && range[1].Length == 0))
                throw InvalidDate(value);
            DateTime? after = range[0].Length == 0 ? null : ParseDate(range[0]).Start;
            DateTime? before = range[1].Length == 0 ? null : ParseDate(range[1]).End;
            if (after is not null && before is not null && after > before)
                throw new FormatException("The sent-date range starts after it ends.");
            return (after, true, before, true);
        }

        if (value.StartsWith(">=", StringComparison.Ordinal))
            return (ParseDate(value[2..]).Start, true, null, true);
        if (value.StartsWith('>'))
            return (ParseDate(value[1..]).End, false, null, true);
        if (value.StartsWith("<=", StringComparison.Ordinal))
            return (null, true, ParseDate(value[2..]).End, true);
        if (value.StartsWith('<'))
            return (null, true, ParseDate(value[1..]).Start, false);

        var exact = ParseDate(value);
        return (exact.Start, true, exact.End, true);
    }

    private static (DateTime Start, DateTime End) ParseDate(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var start = DateTime.SpecifyKind(day, DateTimeKind.Utc);
            return (start, start.AddTicks(TimeSpan.TicksPerDay - 1));
        }

        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mmzzz",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        ];
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var instant))
            return (instant.UtcDateTime, instant.UtcDateTime);
        throw InvalidDate(value);
    }

    private static FormatException InvalidDate(string value) =>
        new($"Invalid sent date '{value}'. Use an ISO date (yyyy-MM-dd) in UTC or an ISO timestamp with Z or a numeric offset.");
}

internal static class MailQueryEvaluation
{
    public static bool ContainsMail(Query query) => query switch
    {
        MailFieldQuery => true,
        UnifiedQuery unified => unified.HasMailCriteria,
        AndQuery and => and.Children.Any(ContainsMail),
        OrQuery or => or.Children.Any(ContainsMail),
        NotQuery not => ContainsMail(not.Child),
        NearQuery near => ContainsMail(near.Left) || ContainsMail(near.Right),
        _ => false,
    };

    public static bool ContainsText(Query query) => query switch
    {
        MatchAllQuery or MailFieldQuery => false,
        UnifiedQuery unified => unified.HasTextCriteria,
        AndQuery and => and.Children.Any(ContainsText),
        OrQuery or => or.Children.Any(ContainsText),
        NotQuery not => ContainsText(not.Child),
        _ => true,
    };

    public static void ValidateProximity(Query query)
    {
        switch (query)
        {
            case NearQuery near when ContainsMail(near):
                throw new FormatException("Mail fields cannot be NEAR operands. Use AND to combine mail fields with content terms.");
            case AndQuery and:
                foreach (var child in and.Children) ValidateProximity(child);
                break;
            case OrQuery or:
                foreach (var child in or.Children) ValidateProximity(child);
                break;
            case NotQuery not:
                ValidateProximity(not.Child);
                break;
        }
    }

    public static bool TryCollectHighlights(Query query, string line, MailMessageMetadata message, List<MatchSpan> sink)
    {
        int start = sink.Count;
        bool matched;
        switch (query)
        {
            case MailFieldQuery mail:
                matched = mail.Matches(message);
                break;
            case UnifiedQuery unified:
                matched = unified.TryCollectHighlights(line, message, sink);
                break;
            case AndQuery and:
                matched = true;
                foreach (var child in and.Children)
                {
                    if (TryCollectHighlights(child, line, message, sink)) continue;
                    matched = false;
                    break;
                }
                break;
            case OrQuery or:
                matched = false;
                foreach (var child in or.Children)
                    matched |= TryCollectHighlights(child, line, message, sink);
                break;
            case NotQuery not:
                matched = !TryCollectHighlights(not.Child, line, message, sink);
                sink.RemoveRange(start, sink.Count - start);
                break;
            default:
                matched = query.TryCollectHighlights(line, sink);
                break;
        }

        if (!matched)
            sink.RemoveRange(start, sink.Count - start);
        return matched;
    }
}
