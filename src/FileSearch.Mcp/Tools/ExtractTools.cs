using System.ComponentModel;
using FileSearch.Core.Extractors;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FileSearch.Mcp.Tools;

[McpServerToolType]
internal sealed class ExtractTools
{
    internal const int DefaultMaxLines = 200;
    internal const int MaxLinesCeiling = 2000;
    internal const int MaxLineChars = 500;
    internal const int MaxTotalChars = 100_000;

    private readonly IExtractorRegistry _extractorRegistry;
    private readonly RootPolicy _rootPolicy;

    public ExtractTools(IExtractorRegistry extractorRegistry, RootPolicy rootPolicy)
    {
        _extractorRegistry = extractorRegistry;
        _rootPolicy = rootPolicy;
    }

    [McpServerTool(
        Name = "extract_text",
        Title = "Extract text from a file",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Extract plain-text lines from one file using FileSearch's format extractors: source/text " +
        "files, PDF, Word, Excel, PowerPoint, OpenDocument, EPUB, RTF, HTML, EML, iCalendar/vCard, " +
        "XML, and ZIP archives (index_status lists every supported extension). Returns numbered " +
        "lines — the same numbers search hits reference — with page/sheet/member anchors where the " +
        "format has them. Page through long files with startLine.")]
    public async Task<string> ExtractTextAsync(
        [Description("Absolute path of the file to read. Must lie under the server's allowed roots.")]
        string path,
        [Description("First line to return (1-based). Default 1. Use the previous call's nextStartLine to page.")]
        int? startLine = null,
        [Description("Maximum lines to return, 1-2000. Default 200.")]
        int? maxLines = null,
        [Description("Extraction deadline in seconds, 5-120. Default 30; partial lines are returned on timeout.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var full = await _rootPolicy.EnsureAllowedAsync(path, "path", cancellationToken).ConfigureAwait(false);
        if (!File.Exists(full))
            throw new McpException($"File does not exist: {full}");

        var extractor = _extractorRegistry.GetFor(full);
        if (extractor is null)
        {
            throw new McpException(
                $"No text extractor is registered for '{Path.GetExtension(full)}' files. " +
                "Call index_status to list the supported extensions.");
        }

        var firstLine = Math.Max(1, startLine ?? 1);
        var take = Math.Clamp(maxLines ?? DefaultMaxLines, 1, MaxLinesCeiling);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ResultShaper.ClampTimeout(timeoutSeconds));

        var lines = new List<ExtractedLineDocument>(Math.Min(take, 64));
        var totalChars = 0;
        var truncated = false;
        var timedOut = false;
        int? nextStartLine = null;

        try
        {
            await foreach (var line in extractor.ExtractAsync(full, timeoutSource.Token).ConfigureAwait(false))
            {
                if (line.Number < firstLine)
                    continue;

                var (text, _) = ResultShaper.ShapeLine(line.Content, [], MaxLineChars);
                lines.Add(new ExtractedLineDocument(line.Number, text, line.Anchor?.DisplayText));
                totalChars += text.Length;

                if (lines.Count >= take || totalChars >= MaxTotalChars)
                {
                    truncated = true;
                    nextStartLine = line.Number + 1;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }

        return McpJson.Serialize(new ExtractResultDocument(
            full,
            extractor.ExtractorId,
            firstLine,
            lines.Count,
            truncated,
            timedOut,
            nextStartLine,
            lines));
    }
}
