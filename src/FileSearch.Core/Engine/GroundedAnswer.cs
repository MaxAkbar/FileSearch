using System.Globalization;
using System.IO;
using System.Text;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Engine;

public sealed record GroundedAnswerEvidence(
    string Path,
    int SearchRank,
    int HitRank,
    string Text,
    int LineNumber = 0,
    double Score = 0,
    SourceAnchor? Anchor = null,
    SourceLocator? Locator = null,
    SearchSnippet? Snippet = null)
{
    public static GroundedAnswerEvidence FromHit(
        string path,
        int searchRank,
        int hitRank,
        Hit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new GroundedAnswerEvidence(
            path,
            Math.Max(0, searchRank),
            Math.Max(0, hitRank),
            string.IsNullOrWhiteSpace(hit.Snippet?.Text) ? hit.LineContent : hit.Snippet.Text,
            hit.LineNumber,
            hit.Score,
            hit.Anchor,
            hit.Snippet?.Locator ?? hit.Locator ?? SourceLocator.FromAnchor(hit.Anchor, hit.LineNumber),
            hit.Snippet);
    }
}

public sealed record GroundedAnswerCitation(
    int Number,
    string Path,
    string FileName,
    string Location,
    string Text,
    int SearchRank,
    int HitRank,
    double Score,
    SourceLocator? Locator,
    SearchSnippet? Snippet);

public sealed record GroundedAnswerDraft(
    string Query,
    DateTime CreatedUtc,
    string Summary,
    IReadOnlyList<GroundedAnswerCitation> Citations,
    string Markdown);

public sealed record GroundedAnswerOptions(
    int MaxCitations = 8,
    int MaxCitationCharacters = 700)
{
    public GroundedAnswerOptions Normalize() =>
        new(
            Math.Clamp(MaxCitations, 1, 50),
            Math.Clamp(MaxCitationCharacters, 120, 8_000));
}

public interface IGroundedAnswerBuilder
{
    GroundedAnswerDraft Build(
        string query,
        IReadOnlyList<GroundedAnswerEvidence> evidence,
        GroundedAnswerOptions? options = null);
}

public sealed class GroundedAnswerBuilder : IGroundedAnswerBuilder
{
    public GroundedAnswerDraft Build(
        string query,
        IReadOnlyList<GroundedAnswerEvidence> evidence,
        GroundedAnswerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var normalizedOptions = (options ?? new GroundedAnswerOptions()).Normalize();
        var citations = evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.SearchRank <= 0 ? int.MaxValue : item.SearchRank)
            .ThenBy(item => item.HitRank <= 0 ? int.MaxValue : item.HitRank)
            .ThenByDescending(item => item.Score)
            .Take(normalizedOptions.MaxCitations)
            .Select((item, index) => ToCitation(item, index + 1, normalizedOptions.MaxCitationCharacters))
            .ToArray();

        var createdUtc = DateTime.UtcNow;
        var summary = CreateSummary(query, citations);
        return new GroundedAnswerDraft(
            query.Trim(),
            createdUtc,
            summary,
            citations,
            RenderMarkdown(query, createdUtc, summary, citations));
    }

    private static GroundedAnswerCitation ToCitation(
        GroundedAnswerEvidence evidence,
        int number,
        int maxCharacters)
    {
        var locator = evidence.Locator ?? SourceLocator.FromAnchor(evidence.Anchor, evidence.LineNumber);
        return new GroundedAnswerCitation(
            number,
            evidence.Path,
            Path.GetFileName(evidence.Path),
            FormatLocation(evidence.Anchor, locator),
            TrimEvidence(evidence.Text, maxCharacters),
            evidence.SearchRank,
            evidence.HitRank,
            evidence.Score,
            locator,
            evidence.Snippet);
    }

    private static string CreateSummary(
        string query,
        GroundedAnswerCitation[] citations)
    {
        var fileCount = citations
            .Select(citation => citation.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var queryText = string.IsNullOrWhiteSpace(query) ? "the current query" : $"\"{query.Trim()}\"";
        return citations.Length == 0
            ? $"No grounded evidence was available for {queryText}."
            : $"Found {citations.Length:n0} cited evidence item(s) across {fileCount:n0} file(s) for {queryText}.";
    }

    private static string RenderMarkdown(
        string query,
        DateTime createdUtc,
        string summary,
        GroundedAnswerCitation[] citations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Grounded Answer Draft");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Query: {query.Trim()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Created: {createdUtc:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine("- Mode: extractive, local evidence only");
        sb.AppendLine();
        sb.AppendLine("## Answer");
        sb.AppendLine();
        sb.AppendLine(summary);
        if (citations.Length > 0)
        {
            sb.AppendLine();
            foreach (var citation in citations)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {FirstLine(citation.Text)} [{citation.Number}]");
        }

        sb.AppendLine();
        sb.AppendLine("## Citations");
        if (citations.Length == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No citations.");
            return sb.ToString();
        }

        foreach (var citation in citations)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"[{citation.Number}] `{citation.Path}`");
            if (!string.IsNullOrWhiteSpace(citation.Location))
                sb.Append(CultureInfo.InvariantCulture, $" ({citation.Location})");
            sb.AppendLine();
            sb.AppendLine();
            foreach (var line in citation.Text.Split(["\r\n", "\n"], StringSplitOptions.None))
                sb.Append("> ").AppendLine(line);
        }

        return sb.ToString();
    }

    private static string FirstLine(string text)
    {
        var normalized = NormalizeWhitespace(text);
        return normalized.Length <= 220 ? normalized : normalized[..217] + "...";
    }

    private static string TrimEvidence(string text, int maxCharacters)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxCharacters)
            return trimmed;

        return trimmed[..Math.Max(0, maxCharacters - 3)].TrimEnd() + "...";
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FormatLocation(SourceAnchor? anchor, SourceLocator? locator)
    {
        if (!string.IsNullOrWhiteSpace(anchor?.DisplayText))
            return anchor.DisplayText;
        if (!string.IsNullOrWhiteSpace(locator?.DisplayText))
            return locator.DisplayText;
        if (locator is null)
            return string.Empty;

        var parts = new List<string>();
        if (locator.Page is { } page)
            parts.Add($"page {page}");
        if (locator.Slide is { } slide)
            parts.Add($"slide {slide}");
        if (!string.IsNullOrWhiteSpace(locator.Sheet))
            parts.Add(string.IsNullOrWhiteSpace(locator.CellRange)
                ? $"sheet {locator.Sheet}"
                : $"sheet {locator.Sheet} {locator.CellRange}");
        else if (!string.IsNullOrWhiteSpace(locator.CellRange))
            parts.Add(locator.CellRange);
        if (locator.StartLine is { } line)
        {
            if (locator.EndLine is { } endLine && endLine != line)
                parts.Add($"lines {line}-{endLine}");
            else if (locator.Column is { } column)
                parts.Add($"line {line}, column {column}");
            else
                parts.Add($"line {line}");
        }
        if (!string.IsNullOrWhiteSpace(locator.Section))
            parts.Add(locator.Section);
        if (!string.IsNullOrWhiteSpace(locator.ArchiveMember))
            parts.Add(locator.ArchiveMember);
        if (locator.X is { } x &&
            locator.Y is { } y &&
            locator.Width is { } width &&
            locator.Height is { } height)
        {
            parts.Add($"region {x},{y} {width}x{height}");
        }

        return string.Join(", ", parts);
    }
}
