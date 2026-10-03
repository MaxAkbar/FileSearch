using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// One row of the preview pane's code view, parsed from the numbered preview
/// text (<see cref="SearchViewModel.PreviewContent"/>): a source line, a hit
/// line, or a gap marker between two disjoint context windows.
/// </summary>
public sealed partial class PreviewLineViewModel : ObservableObject
{
    private const char HitMarker = '►';

    private PreviewLineViewModel(int? lineNumber, string text, bool isHit, bool isGap, IReadOnlyList<MatchSpan> highlights)
    {
        LineNumber = lineNumber;
        Text = text;
        IsHit = isHit;
        IsGap = isGap;
        DisplayHit = new Hit(string.Empty, lineNumber ?? 0, text, highlights);
    }

    public int? LineNumber { get; }

    public string LineNumberText =>
        IsGap ? "⋯" : LineNumber?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string Text { get; }

    public bool IsHit { get; }

    public bool IsGap { get; }

    /// <summary>The row as a <see cref="Hit"/> so the shared hit highlighter can mark the matched spans.</summary>
    public Hit DisplayHit { get; }

    /// <summary>True for the match the preview navigator currently points at.</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>
    /// Parses preview text in either of the formats the app produces: the
    /// numbered listing (<c>"► " + number.PadLeft(6) + "  " + line</c>, windows
    /// separated by <c>---</c>) or the unnumbered snippet/metadata listing.
    /// Hit rows borrow highlight spans from the matching <paramref name="hits"/>.
    /// </summary>
    public static IReadOnlyList<PreviewLineViewModel> Parse(string? content, IReadOnlyList<Hit>? hits)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<PreviewLineViewModel>();

        var rawLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lastContentIndex = Array.FindLastIndex(rawLines, line => line.Length > 0);
        var numbered = rawLines.FirstOrDefault(line => line.Length > 0 && line != "---") is { } first &&
            TryParseNumbered(StripMarker(first), out _, out _);
        var hitsByLine = (hits ?? Array.Empty<Hit>())
            .Where(hit => hit.LineNumber > 0)
            .GroupBy(hit => hit.LineNumber)
            .ToDictionary(group => group.Key, group => group.First());

        var rows = new List<PreviewLineViewModel>();
        int? lastNumber = null;
        var pendingGap = false;
        for (var i = 0; i <= lastContentIndex; i++)
        {
            var line = rawLines[i];
            if (line == "---")
            {
                pendingGap = true;
                continue;
            }

            var isHit = line.Length > 0 && line[0] == HitMarker;
            var body = StripMarker(line);
            int? number = null;
            var text = body;
            if (numbered && TryParseNumbered(body, out var parsed, out var parsedText))
            {
                number = parsed;
                text = parsedText;
            }

            if (pendingGap)
            {
                rows.Add(CreateGap(lastNumber, number));
                pendingGap = false;
            }

            var highlights = isHit && number is { } n && hitsByLine.TryGetValue(n, out var hit)
                ? AlignHighlights(hit, text)
                : Array.Empty<MatchSpan>();
            rows.Add(new PreviewLineViewModel(number, text, isHit, isGap: false, highlights));
            if (number is not null)
                lastNumber = number;
        }

        return rows;
    }

    private static PreviewLineViewModel CreateGap(int? previous, int? next)
    {
        var text = previous is { } from && next is { } to && to - from > 1
            ? to - from == 2
                ? $"Line {from + 1:n0} hidden"
                : $"Lines {from + 1:n0}–{to - 1:n0} hidden"
            : "Lines hidden";
        return new PreviewLineViewModel(null, text, isHit: false, isGap: true, Array.Empty<MatchSpan>());
    }

    private static string StripMarker(string line) =>
        line.Length >= 2 && (line[0] == HitMarker || line[0] == ' ') && line[1] == ' '
            ? line[2..]
            : line.Length == 1 && line[0] == HitMarker ? string.Empty : line;

    private static bool TryParseNumbered(string body, out int number, out string text)
    {
        number = 0;
        text = body;

        var start = 0;
        while (start < body.Length && body[start] == ' ')
            start++;

        var end = start;
        while (end < body.Length && char.IsAsciiDigit(body[end]))
            end++;

        // The listing right-aligns numbers in a 6-wide column, then two spaces.
        if (end == start || end < 6 || end + 2 > body.Length || body[end] != ' ' || body[end + 1] != ' ')
            return false;

        if (!int.TryParse(body.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return false;

        text = body[(end + 2)..];
        return true;
    }

    /// <summary>Shifts the hit's spans onto the preview text, which may carry a trailing anchor.</summary>
    private static MatchSpan[] AlignHighlights(Hit hit, string text)
    {
        if (hit.Highlights.Count == 0 || string.IsNullOrEmpty(hit.LineContent))
            return Array.Empty<MatchSpan>();

        var offset = text.IndexOf(hit.LineContent, StringComparison.Ordinal);
        if (offset < 0)
            return Array.Empty<MatchSpan>();

        return hit.Highlights
            .Select(span => new MatchSpan(span.Start + offset, span.Length))
            .ToArray();
    }
}
