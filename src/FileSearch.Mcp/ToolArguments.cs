using System.Globalization;
using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.WindowsOcr;
using ModelContextProtocol;

namespace FileSearch.Mcp;

/// <summary>
/// Parsing for tool-supplied argument strings, mirroring the CLI's accepted
/// spellings so documentation for one surface applies to the other. All
/// failures are <see cref="McpException"/>s with recovery hints, because the
/// caller is a model, not a person reading usage text.
/// </summary>
internal static class ToolArguments
{
    public static QueryMode ParseMode(string? value) =>
        value is null ? QueryMode.PlainText : value.Trim().ToLowerInvariant() switch
        {
            "plain" or "text" or "literal" => QueryMode.PlainText,
            "regex" or "regexp" => QueryMode.Regex,
            "bool" or "boolean" => QueryMode.Boolean,
            "unified" or "query" or "structured" => QueryMode.Unified,
            "semantic" => QueryMode.Semantic,
            _ => throw new McpException($"Unknown mode '{value}'. Use plain, regex, boolean, unified, or semantic."),
        };

    public static SearchTarget ParseTarget(string? value) =>
        value is null ? SearchTarget.Content : value.Trim().ToLowerInvariant() switch
        {
            "content" or "contents" or "text" => SearchTarget.Content,
            "files" or "file" or "filenames" or "filename" => SearchTarget.FileNames,
            "folders" or "folder" or "directories" or "directory" => SearchTarget.FolderNames,
            "names" or "name" => SearchTarget.FileAndFolderNames,
            _ => throw new McpException($"Unknown target '{value}'. Use content, files, folders, or names."),
        };

    public static DateTime? ParseUtc(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new McpException($"{parameterName} must be an ISO 8601 date/time, for example 2026-06-09 or 2026-06-09T13:00:00Z.");
        }

        return parsed;
    }

    public static Query BuildQuery(IQueryFactory factory, string query, QueryMode mode, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query must be a non-empty string.");

        try
        {
            return factory.Build(query, mode, caseSensitive);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new McpException($"Invalid query: {ex.Message}");
        }
    }

    public static WalkerOptions BuildWalkerOptions(
        string[]? includeGlobs,
        string[]? excludeGlobs,
        string[]? includeExtensions,
        string[]? excludeExtensions,
        bool includeHidden,
        string? modifiedAfter,
        string? modifiedBefore,
        bool excludeImageFiles)
    {
        var excluded = NormalizeExtensions(excludeExtensions);
        if (excludeImageFiles)
        {
            // The MCP server exposes no OCR switch, so a live scan reading
            // image bytes as text would only produce garbage hits (and an
            // accidental OCR pass would be slow). Indexed searches keep image
            // files: their OCR lines are already stored.
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
                excluded.Add(extension);
        }

        return new WalkerOptions
        {
            IncludeGlobs = includeGlobs ?? [],
            ExcludeGlobs = excludeGlobs ?? [],
            IncludeExtensions = NormalizeExtensions(includeExtensions),
            ExcludeExtensions = excluded,
            IncludeHidden = includeHidden,
            ModifiedAfterUtc = ParseUtc(modifiedAfter, "modifiedAfter"),
            ModifiedBeforeUtc = ParseUtc(modifiedBefore, "modifiedBefore"),
        };
    }

    /// <summary>
    /// Widens a request's exclusions with a root's build-time exclusions.
    /// Everything added here was never indexed, so results are unchanged —
    /// but <c>IndexProfile.Covers</c> requires the request to exclude at
    /// least what the build excluded, and without this a GUI- or CLI-built
    /// index (which excludes image files when OCR is off) would report
    /// "Index does not cover this search" for every default request.
    /// </summary>
    public static WalkerOptions MergeBuildTimeExcludes(WalkerOptions options, IndexProfile profile)
    {
        var extensions = new HashSet<string>(options.ExcludeExtensions, StringComparer.OrdinalIgnoreCase);
        extensions.UnionWith(profile.ExcludeExtensions);
        var directories = new HashSet<string>(options.ExcludeDirectories, StringComparer.OrdinalIgnoreCase);
        directories.UnionWith(profile.ExcludeDirectories);
        return options with
        {
            ExcludeExtensions = extensions,
            ExcludeDirectories = directories,
        };
    }

    private static HashSet<string> NormalizeExtensions(string[]? extensions)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extensions is null)
            return result;

        foreach (var extension in extensions)
        {
            if (!string.IsNullOrWhiteSpace(extension))
                result.Add(ExtensionList.Normalize(extension));
        }

        return result;
    }
}
