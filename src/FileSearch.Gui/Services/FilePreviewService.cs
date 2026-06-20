using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;

namespace FileSearch.Gui.Services;

public sealed class FilePreviewService : IFilePreviewService
{
    private readonly IExtractorRegistry _extractorRegistry;

    public FilePreviewService(IExtractorRegistry extractorRegistry)
    {
        _extractorRegistry = extractorRegistry ?? throw new ArgumentNullException(nameof(extractorRegistry));
    }

    public Task<string> LoadHitsPreviewAsync(
        string path,
        IReadOnlyList<int> hitLineNumbers,
        int contextLines,
        CancellationToken cancellationToken)
    {
        if (hitLineNumbers.Count == 0)
            return Task.FromResult(string.Empty);

        var extractor = _extractorRegistry.GetFor(path);
        if (extractor is null)
            return Task.FromResult(string.Empty);

        // Async iterators execute between yields on whichever thread consumes
        // them, so run the whole extraction loop on the thread pool — this
        // service is called from the UI thread, and document parsers
        // (PDF/Excel/Word) do heavy synchronous work per line batch.
        return Task.Run(
            () => BuildHitsPreviewAsync(extractor, path, hitLineNumbers, contextLines, cancellationToken),
            cancellationToken);
    }

    private static async Task<string> BuildHitsPreviewAsync(
        ITextExtractor extractor,
        string path,
        IReadOnlyList<int> hitLineNumbers,
        int contextLines,
        CancellationToken cancellationToken)
    {
        var sortedHits = hitLineNumbers.Distinct().OrderBy(x => x).ToList();
        var hitSet = new HashSet<int>(sortedHits);
        var windows = MergeWindows(sortedHits, contextLines);

        var sb = new StringBuilder();
        int windowIndex = 0;
        bool needSeparator = false;

        await foreach (var line in extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false))
        {
            // Advance past windows we've finished.
            while (windowIndex < windows.Count && line.Number > windows[windowIndex].End)
            {
                windowIndex++;
                needSeparator = true;
            }
            if (windowIndex >= windows.Count) break;
            if (line.Number < windows[windowIndex].Start) continue;

            if (needSeparator)
            {
                sb.AppendLine("---");
                needSeparator = false;
            }

            char marker = hitSet.Contains(line.Number) ? '►' : ' ';
            sb.Append(marker).Append(' ')
              .Append(line.Number.ToString(CultureInfo.InvariantCulture).PadLeft(6))
              .Append("  ")
              .Append(line.Content);
            if (!string.IsNullOrWhiteSpace(line.Anchor?.DisplayText))
                sb.Append("  [").Append(line.Anchor.DisplayText).Append(']');
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public Task<string> LoadFullTextAsync(string path, CancellationToken cancellationToken)
    {
        var extractor = _extractorRegistry.GetFor(path);
        if (extractor is null)
            return Task.FromResult(string.Empty);

        return Task.Run(async () =>
        {
            var sb = new StringBuilder();
            await foreach (var line in extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(line.Content);
            }

            return sb.ToString();
        }, cancellationToken);
    }

    /// <summary>
    /// Expand each hit to [hit-N, hit+N] and merge overlapping/adjacent ranges
    /// so we walk the file exactly once and don't print duplicate lines.
    /// </summary>
    private static List<(int Start, int End)> MergeWindows(List<int> sortedHits, int contextLines)
    {
        var windows = new List<(int Start, int End)>();
        foreach (var hit in sortedHits)
        {
            int start = Math.Max(1, hit - contextLines);
            int end = hit + contextLines;
            if (windows.Count > 0 && windows[^1].End >= start - 1)
            {
                var last = windows[^1];
                windows[^1] = (last.Start, Math.Max(last.End, end));
            }
            else
            {
                windows.Add((start, end));
            }
        }
        return windows;
    }
}
