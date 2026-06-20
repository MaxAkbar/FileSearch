using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Queries;

public sealed record UnifiedQueryChip(
    string Field,
    string Value,
    string RawText,
    int Position = -1,
    int Length = 0,
    bool IsEnabled = true,
    string? Explanation = null);

public sealed record UnifiedQueryFilters(
    IReadOnlyList<string> NameTerms,
    IReadOnlyList<string> PathTerms,
    IReadOnlyList<string> FolderTerms,
    IReadOnlyList<string> RootTerms,
    IReadOnlySet<string> Extensions,
    IReadOnlySet<string> TypeCategories,
    IReadOnlyList<string> SemanticTerms,
    DateTime? ModifiedAfterUtc,
    DateTime? ModifiedBeforeUtc,
    DateTime? CreatedAfterUtc,
    DateTime? CreatedBeforeUtc,
    long? MinSizeBytes,
    long? MaxSizeBytes,
    IReadOnlyList<string> StatusTerms,
    IReadOnlyList<string> ExtractorTerms)
{
    public static UnifiedQueryFilters Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>(),
        null,
        null,
        null,
        null,
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool HasFileFilters =>
        NameTerms.Count > 0 ||
        PathTerms.Count > 0 ||
        FolderTerms.Count > 0 ||
        RootTerms.Count > 0 ||
        Extensions.Count > 0 ||
        TypeCategories.Count > 0 ||
        ModifiedAfterUtc is not null ||
        ModifiedBeforeUtc is not null ||
        CreatedAfterUtc is not null ||
        CreatedBeforeUtc is not null ||
        MinSizeBytes is not null ||
        MaxSizeBytes is not null ||
        StatusTerms.Count > 0 ||
        ExtractorTerms.Count > 0;
}

public sealed class UnifiedQuery : Query
{
    public UnifiedQuery(Query contentQuery, UnifiedQueryFilters filters, IReadOnlyList<UnifiedQueryChip> chips)
    {
        ContentQuery = contentQuery ?? throw new ArgumentNullException(nameof(contentQuery));
        Filters = filters ?? throw new ArgumentNullException(nameof(filters));
        Chips = chips ?? throw new ArgumentNullException(nameof(chips));
    }

    public Query ContentQuery { get; }
    public UnifiedQueryFilters Filters { get; }
    public IReadOnlyList<UnifiedQueryChip> Chips { get; }
    public bool HasContentCriteria => ContentQuery is not MatchAllQuery;
    public bool HasUnavailableSemantic => Filters.SemanticTerms.Count > 0;
    public const string SemanticUnavailableMessage =
        "Semantic search is not available yet. Configure a local semantic index or remove the semantic chip.";

    public override bool IsMatch(string line) => ContentQuery.IsMatch(line);

    public override void CollectHighlights(string line, List<MatchSpan> sink) =>
        ContentQuery.CollectHighlights(line, sink);

    public IReadOnlyList<string> MetadataTerms
    {
        get
        {
            var terms = new List<string>();
            terms.AddRange(Filters.NameTerms);
            terms.AddRange(Filters.PathTerms);
            terms.AddRange(Filters.FolderTerms);
            terms.AddRange(Filters.RootTerms);
            terms.AddRange(Filters.Extensions.Select(extension => extension.TrimStart('.')));
            return terms;
        }
    }

    public bool MatchesLiveFile(IReadOnlyList<string> roots, string path, string? extractorId)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            var root = FindContainingRoot(roots, path);
            return MatchesFile(
                root,
                path,
                info.Name,
                info.Extension,
                info.Length,
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks,
                "ok",
                extractorId,
                null);
        }
        catch
        {
            return false;
        }
    }

    public bool MatchesFile(
        string root,
        string path,
        string fileName,
        string extension,
        long sizeBytes,
        long createdUtcTicks,
        long modifiedUtcTicks,
        string? status,
        string? extractorId,
        string? fileTypeCategory)
    {
        if (!AllContain(fileName, Filters.NameTerms) &&
            !AllContain(Path.GetFileNameWithoutExtension(fileName), Filters.NameTerms))
        {
            return false;
        }

        if (!AllContain(path, Filters.PathTerms) && !AllContain(GetRelativePath(root, path), Filters.PathTerms))
            return false;

        if (Filters.FolderTerms.Count > 0 && !HasFolderTerms(root, path, Filters.FolderTerms))
            return false;

        if (!AllContain(root, Filters.RootTerms) &&
            !AllContain(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), Filters.RootTerms))
        {
            return false;
        }

        var normalizedExtension = NormalizeExtension(extension);
        if (Filters.Extensions.Count > 0 && !Filters.Extensions.Contains(normalizedExtension))
            return false;

        if (Filters.TypeCategories.Count > 0)
        {
            var category = string.IsNullOrWhiteSpace(fileTypeCategory)
                ? InferFileTypeCategory(normalizedExtension)
                : fileTypeCategory;
            if (!Filters.TypeCategories.Contains(category))
                return false;
        }

        if (Filters.MinSizeBytes is { } minSize && sizeBytes < minSize)
            return false;
        if (Filters.MaxSizeBytes is { } maxSize && sizeBytes > maxSize)
            return false;

        if (!MatchesDateRange(modifiedUtcTicks, Filters.ModifiedAfterUtc, Filters.ModifiedBeforeUtc))
            return false;
        if (!MatchesDateRange(createdUtcTicks, Filters.CreatedAfterUtc, Filters.CreatedBeforeUtc))
            return false;

        if (Filters.StatusTerms.Count > 0 && !AllContain(status ?? string.Empty, Filters.StatusTerms))
            return false;
        if (Filters.ExtractorTerms.Count > 0 && !AllContain(extractorId ?? string.Empty, Filters.ExtractorTerms))
            return false;

        return true;
    }

    private static bool MatchesDateRange(long ticks, DateTime? afterUtc, DateTime? beforeUtc)
    {
        if (afterUtc is null && beforeUtc is null)
            return true;
        if (ticks <= 0)
            return false;

        var value = new DateTime(ticks, DateTimeKind.Utc);
        if (afterUtc is { } after && value < after)
            return false;
        if (beforeUtc is { } before && value > before)
            return false;
        return true;
    }

    private static string FindContainingRoot(IReadOnlyList<string> roots, string path)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            try
            {
                var normalizedRoot = Path.GetFullPath(root);
                var normalizedPath = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
                if (relative.Length > 0 &&
                    relative != "." &&
                    !relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative))
                {
                    return normalizedRoot;
                }
            }
            catch
            {
            }
        }

        return roots.Count > 0 ? roots[0] : string.Empty;
    }

    private static bool HasFolderTerms(string root, string path, IReadOnlyList<string> terms)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var relative = GetRelativePath(root, directory);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var term in terms)
        {
            var matched = segments.Any(segment => ContainsIgnoreCase(segment, term)) ||
                ContainsIgnoreCase(directory, term);
            if (!matched)
                return false;
        }

        return true;
    }

    private static bool AllContain(string value, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
            if (!ContainsIgnoreCase(value, term))
                return false;
        return true;
    }

    private static bool ContainsIgnoreCase(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;
        var trimmed = extension.Trim();
        return trimmed[0] == '.' ? trimmed.ToLowerInvariant() : "." + trimmed.ToLowerInvariant();
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

    private static string InferFileTypeCategory(string extension)
    {
        if (s_codeExtensions.Contains(extension))
            return "code";
        if (s_documentExtensions.Contains(extension))
            return "document";
        if (s_imageExtensions.Contains(extension))
            return "image";
        if (s_audioExtensions.Contains(extension))
            return "audio";
        if (s_videoExtensions.Contains(extension))
            return "video";
        if (s_archiveExtensions.Contains(extension))
            return "archive";
        if (extension is ".exe" or ".dll" or ".msi" or ".appx")
            return "application";
        return "other";
    }

    private static readonly HashSet<string> s_codeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".cpp", ".c", ".h",
        ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".kt", ".kts", ".sql", ".ps1", ".sh",
        ".bat", ".cmd", ".json", ".xml", ".xaml", ".html", ".css", ".scss", ".yaml", ".yml",
    };

    private static readonly HashSet<string> s_documentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ppt",
        ".pptx", ".csv", ".tsv", ".epub", ".eml", ".ics", ".vcf",
    };

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico",
    };

    private static readonly HashSet<string> s_audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
    };

    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v",
    };

    private static readonly HashSet<string> s_archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz",
    };
}

public sealed partial class UnifiedQueryParser
{
    private static readonly HashSet<string> s_supportedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "path",
        "content",
        "semantic",
        "ext",
        "type",
        "modified",
        "created",
        "size",
        "folder",
        "root",
        "status",
        "extractor",
        "regex",
    };

    private static readonly Dictionary<string, string[]> s_typeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = new[] { ".pdf" },
        ["word"] = new[] { ".doc", ".docx", ".rtf", ".odt" },
        ["doc"] = new[] { ".doc", ".docx" },
        ["docx"] = new[] { ".docx" },
        ["powerpoint"] = new[] { ".ppt", ".pptx" },
        ["presentation"] = new[] { ".ppt", ".pptx" },
        ["ppt"] = new[] { ".ppt", ".pptx" },
        ["excel"] = new[] { ".xls", ".xlsx", ".csv", ".tsv" },
        ["spreadsheet"] = new[] { ".xls", ".xlsx", ".csv", ".tsv" },
        ["xls"] = new[] { ".xls", ".xlsx" },
        ["email"] = new[] { ".eml", ".msg" },
        ["mail"] = new[] { ".eml", ".msg" },
        ["epub"] = new[] { ".epub" },
        ["text"] = new[] { ".txt", ".md", ".log" },
        ["markdown"] = new[] { ".md", ".markdown" },
    };

    private static readonly HashSet<string> s_typeCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "code",
        "document",
        "image",
        "audio",
        "video",
        "archive",
        "application",
        "other",
    };

    private static readonly char[] s_valueSeparators = { ',', ';' };
    private static readonly string[] s_rangeSeparator = { ".." };

    private readonly bool _caseSensitive;

    public UnifiedQueryParser(bool caseSensitive = false) =>
        _caseSensitive = caseSensitive;

    public UnifiedQuery Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Query is empty.", nameof(input));

        if (NaturalLanguageQueryInterpreter.TryInterpret(input, out var interpretation))
        {
            var interpreted = ParseExplicit(interpretation.QueryText);
            return new UnifiedQuery(interpreted.ContentQuery, interpreted.Filters, interpretation.Chips);
        }

        return ParseExplicit(input);
    }

    private UnifiedQuery ParseExplicit(string input)
    {
        var tokens = Tokenize(input);
        var builder = new FilterBuilder();
        var cursor = new TokenCursor(tokens);
        var query = Simplify(ParseOr(cursor, builder));

        if (!cursor.AtEnd)
            throw new FormatException($"Unexpected token '{cursor.Peek().RawText}' at position {cursor.Peek().Position}.");

        return new UnifiedQuery(query, builder.Build(), builder.Chips.ToArray());
    }

    private Query ParseOr(TokenCursor c, FilterBuilder builder)
    {
        var children = new List<Query> { ParseAnd(c, builder) };
        while (c.MatchKeyword("OR"))
            children.Add(ParseAnd(c, builder));
        return children.Count == 1 ? children[0] : new OrQuery(children);
    }

    private Query ParseAnd(TokenCursor c, FilterBuilder builder)
    {
        var children = new List<Query> { ParseNear(c, builder) };
        while (true)
        {
            if (c.MatchKeyword("AND"))
            {
                children.Add(ParseNear(c, builder));
                continue;
            }

            if (StartsPrimary(c))
            {
                children.Add(ParseNear(c, builder));
                continue;
            }

            break;
        }

        return children.Count == 1 ? children[0] : new AndQuery(children);
    }

    private Query ParseNear(TokenCursor c, FilterBuilder builder)
    {
        var query = ParseNot(c, builder);
        while (c.MatchNearOperator(out var maxDistance))
            query = new NearQuery(query, ParseNot(c, builder), maxDistance);

        return query;
    }

    private Query ParseNot(TokenCursor c, FilterBuilder builder)
    {
        if (c.MatchKeyword("NOT"))
        {
            builder.NegationDepth++;
            try
            {
                return new NotQuery(ParseNot(c, builder));
            }
            finally
            {
                builder.NegationDepth--;
            }
        }

        return ParsePrimary(c, builder);
    }

    private Query ParsePrimary(TokenCursor c, FilterBuilder builder)
    {
        if (c.AtEnd)
            throw new FormatException("Expected a term but reached end of input.");

        var token = c.Peek();
        switch (token.Kind)
        {
            case TokenKind.LParen:
                c.Advance();
                var inner = ParseOr(c, builder);
                if (!c.MatchKind(TokenKind.RParen))
                    throw new FormatException("Missing closing parenthesis.");
                return inner;

            case TokenKind.Quoted:
                c.Advance();
                builder.AddContentChip(token.Text, token.RawText, token.Position, token.RawText.Length);
                return new TermQuery(token.Text, _caseSensitive);

            case TokenKind.Regex:
                c.Advance();
                builder.AddRegexChip(token.Text, token.RawText, token.Position, token.RawText.Length);
                return new RegexQuery(token.Text, _caseSensitive);

            case TokenKind.Word:
                if (TryReadFieldValue(c, out var field, out var value, out var rawText, out var position, out var length))
                    return BuildFieldQuery(field, value, rawText, position, length, builder);

                c.Advance();
                if (IsNearOperator(token.Text))
                    return MatchAllQuery.Instance;

                var query = BuildWordQuery(token.Text, out var chipField, out var chipValue);
                builder.AddChip(builder.IsNegated ? "Exclude " + chipField.ToLowerInvariant() : chipField, chipValue, token.RawText, token.Position, token.RawText.Length);
                return query;

            default:
                throw new FormatException($"Unexpected token '{token.RawText}' at position {token.Position}.");
        }
    }

    private Query BuildFieldQuery(
        string field,
        string value,
        string rawText,
        int position,
        int length,
        FilterBuilder builder)
    {
        switch (field.ToLowerInvariant())
        {
            case "content":
                builder.AddContentChip(value, rawText, position, length);
                return new TermQuery(value, _caseSensitive);

            case "semantic":
                builder.SemanticTerms.Add(value);
                builder.AddUnavailableSemanticChip(builder.IsNegated ? "Exclude semantic" : "Semantic", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "regex":
                builder.AddRegexChip(value, rawText, position, length);
                return new RegexQuery(TrimRegexDelimiters(value), _caseSensitive);

            case "name":
                builder.NameTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not name" : "Name", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "path":
                builder.PathTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not path" : "Path", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "folder":
                builder.FolderTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not folder" : "Folder", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "root":
                builder.RootTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not root" : "Root", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "ext":
                foreach (var extension in SplitValues(value).Select(NormalizeExtension).Where(x => x.Length > 1))
                    builder.Extensions.Add(extension);
                builder.AddChip(builder.IsNegated ? "Not extension" : "Extension", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "type":
                AddTypeFilter(value, builder);
                builder.AddChip(builder.IsNegated ? "Not type" : "Type", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "modified":
                ApplyDateFilter(value, builder, isCreated: false);
                builder.AddChip(builder.IsNegated ? "Not modified" : "Modified", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "created":
                ApplyDateFilter(value, builder, isCreated: true);
                builder.AddChip(builder.IsNegated ? "Not created" : "Created", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "size":
                ApplySizeFilter(value, builder);
                builder.AddChip(builder.IsNegated ? "Not size" : "Size", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "status":
                builder.StatusTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not status" : "Status", value, rawText, position, length);
                return MatchAllQuery.Instance;

            case "extractor":
                builder.ExtractorTerms.AddRange(SplitValues(value));
                builder.AddChip(builder.IsNegated ? "Not extractor" : "Extractor", value, rawText, position, length);
                return MatchAllQuery.Instance;

            default:
                return new TermQuery(rawText, _caseSensitive);
        }
    }

    private Query BuildWordQuery(string value, out string chipField, out string chipValue)
    {
        if (TryParseFuzzyTerm(value, out var fuzzyTerm, out var maxEdits))
        {
            chipField = maxEdits == 1 ? "Fuzzy" : $"Fuzzy {maxEdits}";
            chipValue = fuzzyTerm;
            return new FuzzyQuery(fuzzyTerm, maxEdits, _caseSensitive);
        }

        if (LooksLikeWildcard(value))
        {
            chipField = "Wildcard";
            chipValue = value;
            return new RegexQuery(WildcardToRegex(value), _caseSensitive);
        }

        chipField = "Content";
        chipValue = value;
        return new TermQuery(value, _caseSensitive);
    }

    private static bool TryParseFuzzyTerm(string value, out string term, out int maxEdits)
    {
        term = string.Empty;
        maxEdits = 0;

        var match = FuzzyTermRegex().Match(value);
        if (!match.Success)
            return false;

        term = match.Groups["term"].Value;
        maxEdits = match.Groups["edits"].Success && match.Groups["edits"].Value.Length > 0
            ? int.Parse(match.Groups["edits"].Value, CultureInfo.InvariantCulture)
            : 1;
        return maxEdits > 0;
    }

    private bool TryReadFieldValue(
        TokenCursor c,
        out string field,
        out string value,
        out string rawText,
        out int position,
        out int length)
    {
        field = string.Empty;
        value = string.Empty;
        rawText = string.Empty;
        position = -1;
        length = 0;

        var token = c.Peek();
        if (token.Kind != TokenKind.Word)
            return false;

        var colon = token.Text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return false;

        var candidate = token.Text[..colon];
        if (!s_supportedFields.Contains(candidate))
            return false;

        field = candidate;
        var inline = token.Text[(colon + 1)..];
        c.Advance();
        position = token.Position;

        if (inline.Length > 0)
        {
            value = UnquoteInlineValue(inline);
            rawText = token.RawText;
            length = token.RawText.Length;
            return true;
        }

        if (c.AtEnd || c.Peek().Kind is TokenKind.LParen or TokenKind.RParen || IsKeywordToken(c.Peek()))
            throw new FormatException($"Field '{field}:' requires a value.");

        var valueToken = c.Peek();
        c.Advance();
        value = valueToken.Text;
        rawText = token.RawText + valueToken.RawText;
        length = valueToken.Position + valueToken.RawText.Length - token.Position;
        return true;
    }

    private static Query Simplify(Query query)
    {
        switch (query)
        {
            case AndQuery and:
                var andChildren = and.Children
                    .Select(Simplify)
                    .Where(child => child is not MatchAllQuery)
                    .ToArray();
                return andChildren.Length switch
                {
                    0 => MatchAllQuery.Instance,
                    1 => andChildren[0],
                    _ => new AndQuery(andChildren),
                };

            case OrQuery or:
                var orChildren = or.Children.Select(Simplify).ToArray();
                if (orChildren.Any(child => child is MatchAllQuery))
                    return MatchAllQuery.Instance;
                return orChildren.Length == 1 ? orChildren[0] : new OrQuery(orChildren);

            case NotQuery not:
                return new NotQuery(Simplify(not.Child));

            case NearQuery near:
                var left = Simplify(near.Left);
                var right = Simplify(near.Right);
                if (left is MatchAllQuery)
                    return right;
                if (right is MatchAllQuery)
                    return left;
                return new NearQuery(left, right, near.MaxDistance);

            default:
                return query;
        }
    }

    private static bool StartsPrimary(TokenCursor c)
    {
        if (c.AtEnd) return false;
        var token = c.Peek();
        return token.Kind switch
        {
            TokenKind.LParen => true,
            TokenKind.Quoted => true,
            TokenKind.Regex => true,
            TokenKind.Word => !IsKeyword(token.Text) && !IsNearOperator(token.Text),
            _ => false,
        };
    }

    private static bool IsKeywordToken(Token token) =>
        token.Kind == TokenKind.Word && IsKeyword(token.Text);

    private static bool IsKeyword(string text) =>
        text.Equals("AND", StringComparison.OrdinalIgnoreCase)
        || text.Equals("OR", StringComparison.OrdinalIgnoreCase)
        || text.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsNearOperator(string text) =>
        NearOperatorRegex().IsMatch(text);

    private static bool TryParseNearOperator(string text, out int maxDistance)
    {
        var match = NearOperatorRegex().Match(text);
        if (!match.Success)
        {
            maxDistance = 0;
            return false;
        }

        maxDistance = int.Parse(match.Groups["distance"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool LooksLikeWildcard(string value) =>
        (value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal)) &&
        value.Any(ch => char.IsLetterOrDigit(ch));

    private static string WildcardToRegex(string value)
    {
        var builder = new StringBuilder();
        builder.Append(@"\b");
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        builder.Append(@"\b");
        return builder.ToString();
    }

    private static string TrimRegexDelimiters(string value)
    {
        if (value.Length >= 2 && value[0] == '/' && value[^1] == '/')
            return value[1..^1].Replace(@"\/", "/", StringComparison.Ordinal);
        return value;
    }

    private static string UnquoteInlineValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        return value;
    }

    private static string[] SplitValues(string value) =>
        value
            .Split(s_valueSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToArray();

    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim().TrimStart('*');
        if (trimmed.Length == 0)
            return string.Empty;
        return trimmed[0] == '.'
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }

    private static void AddTypeFilter(string value, FilterBuilder builder)
    {
        foreach (var type in SplitValues(value))
        {
            if (s_typeExtensions.TryGetValue(type, out var extensions))
            {
                foreach (var extension in extensions)
                    builder.Extensions.Add(extension);
                continue;
            }

            if (s_typeCategories.Contains(type))
                builder.TypeCategories.Add(type.ToLowerInvariant());
            else
                builder.Extensions.Add(NormalizeExtension(type));
        }
    }

    private static void ApplyDateFilter(string value, FilterBuilder builder, bool isCreated)
    {
        var (after, before) = ParseDateRange(value);
        if (isCreated)
        {
            builder.CreatedAfterUtc = Later(builder.CreatedAfterUtc, after);
            builder.CreatedBeforeUtc = Earlier(builder.CreatedBeforeUtc, before);
        }
        else
        {
            builder.ModifiedAfterUtc = Later(builder.ModifiedAfterUtc, after);
            builder.ModifiedBeforeUtc = Earlier(builder.ModifiedBeforeUtc, before);
        }
    }

    private static (DateTime? AfterUtc, DateTime? BeforeUtc) ParseDateRange(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new FormatException("Date filter requires a value.");

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            var parts = trimmed.Split(s_rangeSeparator, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new FormatException($"Invalid date range '{value}'.");

            return (
                parts[0].Length == 0 ? null : StartOfDayUtc(ParseDateValue(parts[0])),
                parts[1].Length == 0 ? null : EndOfDayUtc(ParseDateValue(parts[1])));
        }

        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
            return (StartOfDayUtc(ParseDateValue(trimmed[2..])), null);
        if (trimmed.StartsWith('>'))
            return (StartOfDayUtc(ParseDateValue(trimmed[1..])), null);
        if (trimmed.StartsWith("<=", StringComparison.Ordinal))
            return (null, EndOfDayUtc(ParseDateValue(trimmed[2..])));
        if (trimmed.StartsWith('<'))
            return (null, EndOfDayUtc(ParseDateValue(trimmed[1..])));

        var today = DateTime.Today;
        switch (trimmed.ToLowerInvariant())
        {
            case "today":
                return (StartOfDayUtc(today), EndOfDayUtc(today));
            case "yesterday":
                var yesterday = today.AddDays(-1);
                return (StartOfDayUtc(yesterday), EndOfDayUtc(yesterday));
            case "last-week":
                return (StartOfDayUtc(today.AddDays(-7)), EndOfDayUtc(today));
            case "last-month":
                return (StartOfDayUtc(today.AddMonths(-1)), EndOfDayUtc(today));
            case "last-year":
                return (StartOfDayUtc(today.AddYears(-1)), EndOfDayUtc(today));
            case "this-month":
                return (StartOfDayUtc(new DateTime(today.Year, today.Month, 1)), EndOfDayUtc(today));
            case "this-year":
                return (StartOfDayUtc(new DateTime(today.Year, 1, 1)), EndOfDayUtc(today));
        }

        if (TryParseSeasonRange(trimmed, today, out var seasonAfter, out var seasonBefore))
            return (StartOfDayUtc(seasonAfter), EndOfDayUtc(seasonBefore));

        if (Regex.IsMatch(trimmed, @"^\d{4}$", RegexOptions.CultureInvariant) &&
            int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return (
                StartOfDayUtc(new DateTime(year, 1, 1)),
                EndOfDayUtc(new DateTime(year, 12, 31)));
        }

        var date = ParseDateValue(trimmed);
        return (StartOfDayUtc(date), EndOfDayUtc(date));
    }

    private static DateTime ParseDateValue(string value)
    {
        if (DateTime.TryParse(
                value.Trim(),
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal,
                out var local))
        {
            return local.Date;
        }

        throw new FormatException($"Invalid date value '{value}'.");
    }

    private static bool TryParseSeasonRange(string value, DateTime today, out DateTime after, out DateTime before)
    {
        after = default;
        before = default;

        var parts = value.Split('-', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] is not ("last" or "this"))
            return false;

        var year = parts[0] == "last" ? today.Year - 1 : today.Year;
        (after, before) = parts[1] switch
        {
            "spring" => (new DateTime(year, 3, 1), new DateTime(year, 5, 31)),
            "summer" => (new DateTime(year, 6, 1), new DateTime(year, 9, 30)),
            "autumn" or "fall" => (new DateTime(year, 9, 1), new DateTime(year, 11, 30)),
            "winter" => (new DateTime(year, 12, 1), new DateTime(year + 1, 2, 28)),
            _ => (default, default),
        };

        return after != default;
    }

    private static DateTime StartOfDayUtc(DateTime localDate) =>
        DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local).ToUniversalTime();

    private static DateTime EndOfDayUtc(DateTime localDate) =>
        DateTime.SpecifyKind(localDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

    private static DateTime? Later(DateTime? current, DateTime? candidate) =>
        current is null ? candidate : candidate is null ? current : current > candidate ? current : candidate;

    private static DateTime? Earlier(DateTime? current, DateTime? candidate) =>
        current is null ? candidate : candidate is null ? current : current < candidate ? current : candidate;

    private static void ApplySizeFilter(string value, FilterBuilder builder)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            var parts = trimmed.Split(s_rangeSeparator, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new FormatException($"Invalid size range '{value}'.");
            if (parts[0].Length > 0)
                builder.MinSizeBytes = Larger(builder.MinSizeBytes, ParseSize(parts[0]));
            if (parts[1].Length > 0)
                builder.MaxSizeBytes = Smaller(builder.MaxSizeBytes, ParseSize(parts[1]));
            return;
        }

        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
        {
            builder.MinSizeBytes = Larger(builder.MinSizeBytes, ParseSize(trimmed[2..]));
            return;
        }

        if (trimmed.StartsWith('>'))
        {
            builder.MinSizeBytes = Larger(builder.MinSizeBytes, ParseSize(trimmed[1..]));
            return;
        }

        if (trimmed.StartsWith("<=", StringComparison.Ordinal))
        {
            builder.MaxSizeBytes = Smaller(builder.MaxSizeBytes, ParseSize(trimmed[2..]));
            return;
        }

        if (trimmed.StartsWith('<'))
        {
            builder.MaxSizeBytes = Smaller(builder.MaxSizeBytes, ParseSize(trimmed[1..]));
            return;
        }

        var exact = ParseSize(trimmed);
        builder.MinSizeBytes = Larger(builder.MinSizeBytes, exact);
        builder.MaxSizeBytes = Smaller(builder.MaxSizeBytes, exact);
    }

    private static long ParseSize(string value)
    {
        var match = SizeRegex().Match(value.Trim());
        if (!match.Success)
            throw new FormatException($"Invalid size value '{value}'.");

        var amount = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "" or "b" => 1L,
            "k" or "kb" => 1024L,
            "m" or "mb" => 1024L * 1024L,
            "g" or "gb" => 1024L * 1024L * 1024L,
            "t" or "tb" => 1024L * 1024L * 1024L * 1024L,
            _ => throw new FormatException($"Invalid size unit '{value}'."),
        };

        return checked((long)(amount * multiplier));
    }

    private static long? Larger(long? current, long candidate) =>
        current is null ? candidate : Math.Max(current.Value, candidate);

    private static long? Smaller(long? current, long candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);

    internal enum TokenKind { Word, Quoted, Regex, LParen, RParen }

    internal readonly record struct Token(TokenKind Kind, string Text, string RawText, int Position);

    internal static IReadOnlyList<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(", "(", i)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")", ")", i)); i++; continue; }

            if (c == '"')
            {
                int start = i;
                i++;
                sb.Clear();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        sb.Append(input[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                }

                if (i >= input.Length)
                    throw new FormatException($"Unterminated string starting at position {start}.");
                i++;
                tokens.Add(new Token(TokenKind.Quoted, sb.ToString(), input[start..i], start));
                continue;
            }

            if (c == '/')
            {
                int start = i;
                i++;
                sb.Clear();
                while (i < input.Length && input[i] != '/')
                {
                    if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] == '/')
                    {
                        sb.Append('/');
                        i += 2;
                    }
                    else
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                }

                if (i >= input.Length)
                    throw new FormatException($"Unterminated regex starting at position {start}.");
                i++;
                tokens.Add(new Token(TokenKind.Regex, sb.ToString(), input[start..i], start));
                continue;
            }

            int wordStart = i;
            sb.Clear();
            while (i < input.Length &&
                   !char.IsWhiteSpace(input[i]) &&
                   input[i] != '(' &&
                   input[i] != ')' &&
                   input[i] != '"')
            {
                sb.Append(input[i]);
                i++;
            }

            tokens.Add(new Token(TokenKind.Word, sb.ToString(), input[wordStart..i], wordStart));
        }

        return tokens;
    }

    [GeneratedRegex(@"^(?<value>\d+(?:\.\d+)?)(?<unit>b|kb|mb|gb|tb|k|m|g|t)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^NEAR/(?<distance>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NearOperatorRegex();

    [GeneratedRegex(@"^(?<term>[\p{L}\p{Nd}_-]+)~(?<edits>\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FuzzyTermRegex();

    internal sealed class TokenCursor
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _index;

        public TokenCursor(IReadOnlyList<Token> tokens) => _tokens = tokens;

        public bool AtEnd => _index >= _tokens.Count;
        public Token Peek() => _tokens[_index];
        public void Advance() => _index++;

        public bool MatchKind(TokenKind kind)
        {
            if (AtEnd || _tokens[_index].Kind != kind) return false;
            _index++;
            return true;
        }

        public bool MatchKeyword(string keyword)
        {
            if (AtEnd) return false;
            var token = _tokens[_index];
            if (token.Kind == TokenKind.Word && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                return true;
            }

            return false;
        }

        public bool MatchNearOperator(out int maxDistance)
        {
            maxDistance = 0;
            if (AtEnd) return false;
            var token = _tokens[_index];
            if (token.Kind == TokenKind.Word && TryParseNearOperator(token.Text, out maxDistance))
            {
                _index++;
                return true;
            }

            return false;
        }
    }

    private sealed class FilterBuilder
    {
        public List<string> NameTerms { get; } = new();
        public List<string> PathTerms { get; } = new();
        public List<string> FolderTerms { get; } = new();
        public List<string> RootTerms { get; } = new();
        public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TypeCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SemanticTerms { get; } = new();
        public DateTime? ModifiedAfterUtc { get; set; }
        public DateTime? ModifiedBeforeUtc { get; set; }
        public DateTime? CreatedAfterUtc { get; set; }
        public DateTime? CreatedBeforeUtc { get; set; }
        public long? MinSizeBytes { get; set; }
        public long? MaxSizeBytes { get; set; }
        public List<string> StatusTerms { get; } = new();
        public List<string> ExtractorTerms { get; } = new();
        public List<UnifiedQueryChip> Chips { get; } = new();
        public int NegationDepth { get; set; }
        public bool IsNegated => NegationDepth > 0;

        public void AddContentChip(string value, string rawText, int position, int length) =>
            AddChip(IsNegated ? "Exclude" : "Content", value, rawText, position, length);

        public void AddRegexChip(string value, string rawText, int position, int length) =>
            AddChip(IsNegated ? "Exclude regex" : "Regex", value, rawText, position, length);

        public void AddChip(string field, string value, string rawText, int position, int length) =>
            Chips.Add(new UnifiedQueryChip(field, value, rawText, position, length));

        public void AddUnavailableSemanticChip(string field, string value, string rawText, int position, int length) =>
            Chips.Add(new UnifiedQueryChip(
                field,
                value,
                rawText,
                position,
                length,
                IsEnabled: false,
                UnifiedQuery.SemanticUnavailableMessage));

        public UnifiedQueryFilters Build() => new(
            NameTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PathTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            FolderTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            RootTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            TypeCategories.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SemanticTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ModifiedAfterUtc,
            ModifiedBeforeUtc,
            CreatedAfterUtc,
            CreatedBeforeUtc,
            MinSizeBytes,
            MaxSizeBytes,
            StatusTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ExtractorTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
