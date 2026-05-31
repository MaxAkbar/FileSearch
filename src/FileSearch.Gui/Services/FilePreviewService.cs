using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Gui.Services;

public sealed class FilePreviewService : IFilePreviewService
{
    public async Task<string> LoadHitsPreviewAsync(
        string path,
        IReadOnlyList<int> hitLineNumbers,
        int contextLines,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || hitLineNumbers.Count == 0)
            return string.Empty;

        var sortedHits = hitLineNumbers.Distinct().OrderBy(x => x).ToList();
        var hitSet = new HashSet<int>(sortedHits);
        var windows = MergeWindows(sortedHits, contextLines);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var sb = new StringBuilder();
        int lineNumber = 0;
        int windowIndex = 0;
        bool needSeparator = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;

            // Advance past windows we've finished.
            while (windowIndex < windows.Count && lineNumber > windows[windowIndex].End)
            {
                windowIndex++;
                needSeparator = true;
            }
            if (windowIndex >= windows.Count) break;
            if (lineNumber < windows[windowIndex].Start) continue;

            if (needSeparator)
            {
                sb.AppendLine("---");
                needSeparator = false;
            }

            char marker = hitSet.Contains(lineNumber) ? '►' : ' ';
            sb.Append(marker).Append(' ')
              .Append(lineNumber.ToString().PadLeft(6))
              .Append("  ")
              .AppendLine(line);
        }

        return sb.ToString();
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
