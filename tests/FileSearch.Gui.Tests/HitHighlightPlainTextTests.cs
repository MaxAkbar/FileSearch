using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Highlighting;

namespace FileSearch.Gui.Tests;

public sealed class HitHighlightPlainTextTests
{
    [Fact]
    public Task RecyclingHighlightedCellToIdenticalPlainTextRemovesOldFormatting() => OnDispatcherAsync(() =>
    {
        const string value = "needle 東京 record";
        var block = new TextBlock();
        HitHighlight.SetText(block, value);
        HitHighlight.SetHit(block, new Hit("records.db", 1, value, [new MatchSpan(0, 6)]));
        Assert.Equal("needle", Assert.Single(Marked(block)));

        // Recycled cells can lose their match while their text remains identical.
        HitHighlight.SetHit(block, null);
        Assert.Equal(value, block.Text);
        Assert.Empty(Marked(block));
        Assert.Equal(value, AutomationProperties.GetName(block));

        HitHighlight.SetText(block, "different cell");
        Assert.Equal("different cell", block.Text);
        Assert.Empty(Marked(block));
    });

    [Fact]
    public Task PlainCellCanEnterAndLeaveInheritedFilterHighlightingThenChangeValue() => OnDispatcherAsync(() =>
    {
        var parent = new StackPanel();
        var block = new TextBlock();
        parent.Children.Add(block);
        HitHighlight.SetText(block, "invoice renewal RENEWAL");
        Assert.Equal("invoice renewal RENEWAL", block.Text);

        HitHighlight.SetFilterText(parent, "renewal");
        Assert.Equal(new[] { "renewal", "RENEWAL" }, Marked(block));
        HitHighlight.SetFilterText(parent, "  ");
        Assert.Equal("invoice renewal RENEWAL", block.Text);
        Assert.Empty(Marked(block));

        HitHighlight.SetText(block, "replacement 東京");
        Assert.Equal("replacement 東京", block.Text);
        Assert.Equal("replacement 東京", AutomationProperties.GetName(block));
        Assert.Empty(Marked(block));
        HitHighlight.SetFilterText(parent, "東京");
        Assert.Equal("東京", Assert.Single(Marked(block)));
    });

    [Theory]
    [InlineData("ordinary record")]
    [InlineData("東京 invoice\nsecond line")]
    [InlineData("  whitespace\t ")]
    [InlineData("")]
    public Task PlainFallbackKeepsExactTextAndAccessibleName(string value) => OnDispatcherAsync(() =>
    {
        var block = new TextBlock { FontSize = 19, Foreground = Brushes.DarkBlue, FontWeight = FontWeights.Bold };
        HitHighlight.SetText(block, value);
        Assert.Equal(value, block.Text);
        Assert.Equal(value, AutomationProperties.GetName(block));
        Assert.Equal(19, block.FontSize);
        Assert.Same(Brushes.DarkBlue, block.Foreground);
        Assert.Equal(FontWeights.Bold, block.FontWeight);
    });

    [Fact]
    public Task ClearingPlainFallbackClearsTextAndAccessibleNameAfterAFilter() => OnDispatcherAsync(() =>
    {
        var block = new TextBlock();
        HitHighlight.SetText(block, "record renewal");
        HitHighlight.SetFilterText(block, "renewal");
        Assert.Equal("renewal", Assert.Single(Marked(block)));
        HitHighlight.SetFilterText(block, "");
        HitHighlight.SetText(block, null);
        Assert.Equal(string.Empty, block.Text);
        Assert.Equal(string.Empty, AutomationProperties.GetName(block));
        Assert.Empty(Marked(block));
    });

    [Fact]
    public Task RealHitRetainsPrimaryHighlightsAndSourceLocationAcrossFilterChanges() => OnDispatcherAsync(() =>
    {
        var block = new TextBlock();
        var hit = new Hit("report.pdf", 1, "invoice renewal", [new MatchSpan(0, 7)], Anchor: SourceAnchor.PdfPage(2));
        HitHighlight.SetText(block, "fallback text");
        Layout(block);
        HitHighlight.SetHit(block, hit);
        Assert.Equal("invoice", Assert.Single(Marked(block)));
        Assert.Contains("page 2", block.Text);
        Assert.Contains("invoice renewal", AutomationProperties.GetName(block));
        Assert.Contains("page 2", AutomationProperties.GetName(block));

        HitHighlight.SetFilterText(block, "renewal");
        Assert.Equal(new[] { "invoice", "renewal" }, Marked(block));
        HitHighlight.SetFilterText(block, "");
        Assert.Equal("invoice", Assert.Single(Marked(block)));
        Assert.Contains("page 2", block.Text);
        Assert.Equal(new MatchSpan(0, 7), Assert.Single(hit.Highlights));
    });

    [Fact]
    public Task HitWithoutHighlightsStillShowsSourceLocationAndCanReturnToPlainFallback() => OnDispatcherAsync(() =>
    {
        var block = new TextBlock();
        HitHighlight.SetText(block, "same record");
        Layout(block);
        HitHighlight.SetHit(block, new Hit("report.pdf", 1, "same record", [], Anchor: SourceAnchor.PdfPage(3)));
        Assert.Contains("page 3", block.Text);
        Assert.Contains("page 3", AutomationProperties.GetName(block));

        HitHighlight.SetHit(block, null);
        Assert.Equal("same record", block.Text);
        Assert.Equal("same record", AutomationProperties.GetName(block));
        Assert.Empty(Marked(block));
    });

    [Fact]
    public Task InitialUnmeasuredHitHasCorrectInlineContentAndAccessibleSourceLocation() => OnDispatcherAsync(() =>
    {
        var block = new TextBlock();
        HitHighlight.SetText(block, "initial fallback");
        HitHighlight.SetHit(block, new Hit("report.pdf", 1, "invoice renewal", [new MatchSpan(0, 7)], Anchor: SourceAnchor.PdfPage(4)));

        // Before its first measure, WPF may retain a cached Text property while
        // the inline content already describes what the control will render.
        string content = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));
        Assert.StartsWith("invoice renewal", content);
        Assert.Contains("page 4", content);
        Assert.Equal("invoice", Assert.Single(Marked(block)));
        Assert.Contains("invoice renewal", AutomationProperties.GetName(block));
        Assert.Contains("page 4", AutomationProperties.GetName(block));
    });

    [Fact]
    public Task InheritedFilterLeavesOrdinaryUnregisteredLabelsUntouched() => OnDispatcherAsync(() =>
    {
        var parent = new StackPanel();
        var label = new TextBlock { Text = "renewal label" };
        AutomationProperties.SetName(label, "Existing accessible label");
        parent.Children.Add(label);
        HitHighlight.SetFilterText(parent, "renewal");
        HitHighlight.SetFilterText(parent, "");
        Assert.Equal("renewal label", label.Text);
        Assert.Equal("Existing accessible label", AutomationProperties.GetName(label));
    });

    private static string[] Marked(TextBlock block) => block.Inlines.OfType<Run>()
        .Where(run => run.Background is not null).Select(run => run.Text).ToArray();

    private static void Layout(TextBlock block)
    {
        // A visible recycled cell has already been measured in its plain state.
        block.Measure(new Size(500, double.PositiveInfinity));
        block.Arrange(new Rect(new Point(), block.DesiredSize));
    }

    private static async Task OnDispatcherAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}
