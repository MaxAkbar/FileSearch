using System.Buffers;
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

    private const int BinarySniffBytes = 8192;

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One open serves both the binary sniff and the line read — the
        // sniff used to open every file a second time.
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, useAsync: true);
        }
        catch
        {
            // Unreadable file — skip it, matching the old sniff behavior.
        }

        if (stream is null) yield break;

        await using (stream.ConfigureAwait(false))
        {
            var probe = ArrayPool<byte>.Shared.Rent(BinarySniffBytes);
            bool binary;
            try
            {
                int read = await stream.ReadAsync(probe.AsMemory(0, BinarySniffBytes), cancellationToken).ConfigureAwait(false);
                binary = LooksBinary(probe.AsSpan(0, read));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probe);
            }

            if (binary) yield break;

            stream.Position = 0;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

            int lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                yield return new TextLine(lineNumber, line);
            }
        }
    }

    private static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        // UTF-16/UTF-32 text is full of NUL bytes (PowerShell 5's Out-File
        // default is UTF-16LE); trust the BOM before applying the NUL
        // heuristic so those files aren't dropped as binary. The
        // StreamReader's BOM detection picks the right decoder.
        if (HasUtf16OrUtf32Bom(head))
            return false;

        for (int i = 0; i < head.Length; i++)
            if (head[i] == 0) return true;
        return false;
    }

    private static bool HasUtf16OrUtf32Bom(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2)
        {
            if (head[0] == 0xFF && head[1] == 0xFE) return true; // UTF-16LE (and UTF-32LE prefix)
            if (head[0] == 0xFE && head[1] == 0xFF) return true; // UTF-16BE
        }

        return head.Length >= 4 &&
            head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF; // UTF-32BE
    }
}
