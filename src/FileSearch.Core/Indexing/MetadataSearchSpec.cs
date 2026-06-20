using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Indexing;

internal sealed record MetadataSearchSpec(
    IReadOnlyList<string> Terms,
    bool RequireAllTerms,
    bool MetadataDominant,
    SearchTarget SearchTarget)
{
    public static bool TryCreate(SearchRequest request, out MetadataSearchSpec spec)
    {
        spec = null!;

        var expression = request.Expression;
        if (expression is UnifiedQuery unified)
        {
            if (!unified.HasContentCriteria)
            {
                var metadataTerms = unified.MetadataTerms
                    .Select(NormalizeTerm)
                    .Where(term => term.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (metadataTerms.Length == 0)
                    return false;

                spec = new MetadataSearchSpec(metadataTerms, true, true, request.SearchTarget);
                return true;
            }

            expression = unified.ContentQuery;
        }

        if (request.Mode == QueryMode.Regex || expression is RegexQuery)
            return false;

        var metadataTarget = request.SearchTarget != SearchTarget.Content;
        if (!TryCollectTerms(expression, metadataTarget, out var terms, out var requireAllTerms))
            return false;

        var normalized = terms
            .Select(NormalizeTerm)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
            return false;

        var singleTerm = normalized.Length == 1 && expression is TermQuery;
        var metadataDominant =
            metadataTarget ||
            singleTerm ||
            normalized.Any(LooksLikePathOrFileName) ||
            request.Mode == QueryMode.PlainText;

        if (!metadataDominant && !normalized.Any(LooksLikePathOrFileName))
            return false;

        spec = new MetadataSearchSpec(normalized, requireAllTerms, metadataDominant, request.SearchTarget);
        return true;
    }

    public double Score(IndexedFileMetadata file, string root, out string displayText)
    {
        displayText = file.FileName;

        var rootRelative = SearchTarget == SearchTarget.FileNames
            ? file.FileName
            : GetRelativePath(root, file.Path);
        var fileName = file.FileName;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var path = SearchTarget == SearchTarget.FileNames ? string.Empty : file.Path;
        var directory = SearchTarget == SearchTarget.FileNames ? string.Empty : file.DirectoryPath;
        var extension = file.Extension.TrimStart('.');

        var score = 0d;
        var matched = 0;
        foreach (var term in Terms)
        {
            var termScore = ScoreTerm(
                term,
                fileName,
                fileNameWithoutExtension,
                extension,
                path,
                rootRelative,
                directory,
                out var termDisplay);

            if (termScore <= 0)
            {
                if (RequireAllTerms)
                    return 0;

                continue;
            }

            matched++;
            score += termScore;
            if (!string.IsNullOrWhiteSpace(termDisplay))
                displayText = termDisplay;
        }

        if (matched == 0)
            return 0;

        score += RecencyScore(file.ModifiedUtcTicks);
        score += Math.Min(100, Math.Max(0, file.OpenCount) * 10);
        score += LastOpenedScore(file.LastOpenedUtcTicks);
        return score;
    }

    public IReadOnlyList<MatchSpan> CollectHighlights(string text)
    {
        var spans = new List<MatchSpan>();
        foreach (var term in Terms)
        {
            var index = 0;
            while (index <= text.Length - term.Length)
            {
                var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                spans.Add(new MatchSpan(found, term.Length));
                index = found + term.Length;
            }
        }

        return spans;
    }

    private static bool TryCollectTerms(
        Query query,
        bool metadataTarget,
        out IReadOnlyList<string> terms,
        out bool requireAllTerms)
    {
        requireAllTerms = true;
        switch (query)
        {
            case TermQuery term:
                terms = new[] { term.Term };
                return true;

            case AndQuery and:
                var andTerms = new List<string>();
                foreach (var child in and.Children)
                {
                    if (child is NotQuery)
                        continue;

                    if (child is not TermQuery childTerm)
                    {
                        terms = Array.Empty<string>();
                        return false;
                    }

                    andTerms.Add(childTerm.Term);
                }

                terms = andTerms;
                return andTerms.Count > 0;

            case OrQuery or:
                var orTerms = new List<string>();
                foreach (var child in or.Children)
                {
                    if (child is not TermQuery childTerm)
                    {
                        terms = Array.Empty<string>();
                        return false;
                    }

                    orTerms.Add(childTerm.Term);
                }

                terms = orTerms;
                requireAllTerms = false;
                return orTerms.Count > 0 && (metadataTarget || orTerms.Any(LooksLikePathOrFileName));

            default:
                terms = Array.Empty<string>();
                return false;
        }
    }

    private static string NormalizeTerm(string value) =>
        value.Trim().Trim('*', '?', '"').ToLowerInvariant();

    private static bool LooksLikePathOrFileName(string value) =>
        value.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '.', '*', '?' }) >= 0 ||
        value.Any(char.IsDigit);

    private static double ScoreTerm(
        string term,
        string fileName,
        string fileNameWithoutExtension,
        string extension,
        string path,
        string rootRelative,
        string directory,
        out string displayText)
    {
        displayText = fileName;

        if (EqualsIgnoreCase(fileName, term) || EqualsIgnoreCase(fileNameWithoutExtension, term))
            return 1000;

        if (StartsWithIgnoreCase(fileName, term) || StartsWithIgnoreCase(fileNameWithoutExtension, term))
            return 800;

        if (EqualsIgnoreCase(extension, term.TrimStart('.')))
            return 700;

        if (HasPathSegment(path, term))
        {
            displayText = rootRelative;
            return 600;
        }

        if (ContainsIgnoreCase(fileName, term))
            return 450;

        if (ContainsIgnoreCase(directory, term))
        {
            displayText = rootRelative;
            return 300;
        }

        if (ContainsIgnoreCase(path, term))
        {
            displayText = rootRelative;
            return 200;
        }

        return 0;
    }

    private static double RecencyScore(long modifiedUtcTicks)
    {
        if (modifiedUtcTicks <= 0)
            return 0;

        var age = DateTime.UtcNow - new DateTime(modifiedUtcTicks, DateTimeKind.Utc);
        if (age <= TimeSpan.FromDays(7))
            return 50;
        if (age <= TimeSpan.FromDays(30))
            return 25;
        if (age <= TimeSpan.FromDays(365))
            return 10;

        return 0;
    }

    private static double LastOpenedScore(long lastOpenedUtcTicks)
    {
        if (lastOpenedUtcTicks <= 0)
            return 0;

        var age = DateTime.UtcNow - new DateTime(lastOpenedUtcTicks, DateTimeKind.Utc);
        if (age <= TimeSpan.FromDays(1))
            return 75;
        if (age <= TimeSpan.FromDays(7))
            return 40;
        if (age <= TimeSpan.FromDays(30))
            return 20;

        return 0;
    }

    private static string GetRelativePath(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private static bool HasPathSegment(string path, string term)
    {
        foreach (var segment in path.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (EqualsIgnoreCase(segment, term))
                return true;

            if (EqualsIgnoreCase(Path.GetFileNameWithoutExtension(segment), term))
                return true;
        }

        return false;
    }

    private static bool EqualsIgnoreCase(string value, string term) =>
        string.Equals(value, term, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithIgnoreCase(string value, string term) =>
        value.StartsWith(term, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
