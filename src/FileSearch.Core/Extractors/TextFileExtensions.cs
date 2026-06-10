using System;
using System.Collections.Generic;

namespace FileSearch.Core.Extractors;

/// <summary>
/// The single list of extensions treated as plain text, shared by
/// <see cref="PlainTextExtractor"/> and <see cref="ZipExtractor"/> so the
/// two can't drift apart.
/// </summary>
internal static class TextFileExtensions
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv",
        ".jsonl", ".ndjson",
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".properties", ".lock", ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj",
        ".css", ".scss", ".less",
        ".html", ".htm", ".cshtml", ".vbhtml", ".asp", ".aspx", ".ascx", ".ashx", ".asmx", ".master",
        ".razor", ".jsp", ".jspx", ".ejs", ".hbs", ".handlebars", ".mustache", ".vue", ".svelte", ".astro",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".cs", ".csx", ".fs", ".fsx", ".vb",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".kts", ".swift", ".php", ".pl", ".pm",
        ".lua", ".r", ".m", ".mm", ".scala", ".sc", ".groovy", ".gvy", ".dart", ".ex", ".exs",
        ".erl", ".hrl", ".clj", ".cljs", ".cljc", ".zig", ".nim", ".hs", ".lhs", ".ml", ".mli",
        ".pas", ".pp", ".inc",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd",
        ".sql", ".graphql", ".gql", ".proto", ".http", ".rest",
        ".dockerfile", ".gradle", ".tf", ".tfvars", ".bicep",
        ".env", ".gitignore", ".gitattributes", ".editorconfig",
    };
}
