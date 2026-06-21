using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace FileSearch.Gui.Highlighting;

/// <summary>
/// Attached behavior that renders a <see cref="Hit"/>'s line content into a
/// <see cref="TextBlock"/>, wrapping each <see cref="MatchSpan"/> in a
/// highlighted <see cref="Run"/>. Set <c>HitHighlight.Hit="{Binding}"</c> on a
/// TextBlock whose data context is a <see cref="Hit"/>.
/// </summary>
public static class HitHighlight
{
    public static readonly DependencyProperty HitProperty =
        DependencyProperty.RegisterAttached(
            "Hit",
            typeof(Hit),
            typeof(HitHighlight),
            new PropertyMetadata(null, OnHitChanged));

    public static void SetHit(DependencyObject element, Hit? value) =>
        element.SetValue(HitProperty, value);

    public static Hit? GetHit(DependencyObject element) =>
        (Hit?)element.GetValue(HitProperty);

    private static void OnHitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        textBlock.Inlines.Clear();
        if (e.NewValue is not Hit hit)
            return;

        foreach (var run in BuildRuns(hit.LineContent, hit.Highlights))
            textBlock.Inlines.Add(run);

        var location = SourceLocationFormatter.Format(hit.Anchor, hit.Locator);
        if (!string.IsNullOrWhiteSpace(location))
            AddLocationRun(textBlock, location);
    }

    private static void AddLocationRun(TextBlock textBlock, string location)
    {
        var anchorBrush =
            Application.Current?.TryFindResource("Atlas.Ink3Brush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        textBlock.Inlines.Add(new Run("  " + location)
        {
            Foreground = anchorBrush,
            FontSize = Math.Max(10, textBlock.FontSize - 1),
        });
    }

    private static IEnumerable<Run> BuildRuns(string text, IReadOnlyList<MatchSpan> spans)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var highlightBrush =
            Application.Current?.TryFindResource("Atlas.HitBrush") as Brush
            ?? new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xCE, 0x54));
        var highlightInk =
            Application.Current?.TryFindResource("Atlas.HitInkBrush") as Brush;

        var ordered = (spans ?? Array.Empty<MatchSpan>())
            .Where(s => s.Length > 0 && s.Start < text.Length)
            .OrderBy(s => s.Start)
            .ToList();

        if (ordered.Count == 0)
        {
            yield return new Run(text);
            yield break;
        }

        var pos = 0;
        foreach (var span in ordered)
        {
            var start = Math.Clamp(span.Start, 0, text.Length);
            var end = Math.Clamp(span.Start + span.Length, start, text.Length);

            // Skip spans that fall entirely inside an already-emitted region
            // (defensive against overlapping highlights from the engine).
            if (end <= pos)
                continue;
            if (start < pos)
                start = pos;

            if (start > pos)
                yield return new Run(text.Substring(pos, start - pos));

            var marked = new Run(text.Substring(start, end - start))
            {
                Background = highlightBrush,
                FontWeight = FontWeights.SemiBold,
            };
            if (highlightInk is not null)
                marked.Foreground = highlightInk;
            yield return marked;

            pos = end;
        }

        if (pos < text.Length)
            yield return new Run(text.Substring(pos));
    }
}
