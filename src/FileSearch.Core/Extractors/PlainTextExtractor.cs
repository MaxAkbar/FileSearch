using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Streams a file as text, one line at a time. Skips files that look binary
/// (contain a NUL byte in the first 8 KB) so we don't waste time matching
/// against executables, images, etc.
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly string[] s_extensions =
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

    public IReadOnlyCollection<string> SupportedExtensions => s_extensions;

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (LooksBinary(path)) yield break;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        int lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            yield return new TextLine(lineNumber, line);
        }
    }

    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[8192];
            int read = fs.Read(buffer);
            for (int i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch
        {
            return true; // Couldn't read it — don't try to extract.
        }
    }
}
