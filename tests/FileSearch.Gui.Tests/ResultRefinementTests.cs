using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Highlighting;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ResultRefinementTests
{
    [Fact]
    public void RefinementMatchesDisplayedNamesPathsAndReturnedTextAsOneLiteralPhrase()
    {
        var file = new FileResultViewModel(@"C:\Clients\Acme\report.txt", new FakeFileLauncher(), isDirectory: false);
        file.AddHit(new Hit(file.FullPath, 5, "invoice for annual renewal", [new MatchSpan(0, 7)]));
        Assert.True(file.ApplyRefinement("ACME"));
        Assert.True(file.ApplyRefinement("report.txt"));
        Assert.True(file.ApplyRefinement(" annual renewal "));
        Assert.False(file.ApplyRefinement("invoice OR renewal"));
        Assert.False(file.ApplyRefinement("not returned"));
        Assert.True(file.ApplyRefinement(null));
        var mail = new MailMessageMetadata("id", "Project renewal", "sender", "to", "", null, "Inbox", "fingerprint");
        var message = new FileResultViewModel(@"C:\Mail\archive.pst", new FakeFileLauncher(), isDirectory: false);
        message.AddHit(new Hit(message.FullPath, 1, "ordinary body", [], Locator: new SourceLocator(MailMessage: mail)));
        Assert.True(message.ApplyRefinement("Project renewal"));
        Assert.True(message.ApplyRefinement("sender"));
    }

    [Fact]
    public void MatchingExcerptsBeyondTheCollapsedCardAreShownAndClearingRestoresTheOriginalHits()
    {
        var file = new FileResultViewModel(@"C:\docs\report.txt", new FakeFileLauncher(), isDirectory: false);
        var original = Enumerable.Range(1, 8).Select(line => new Hit(file.FullPath, line,
            line < 5 ? "invoice ordinary" : "invoice annual renewal", [new MatchSpan(0, 7)])).ToArray();
        file.AddHits(original);
        Assert.True(file.ApplyRefinement("annual renewal"));
        Assert.Equal(new[] { 5, 6, 7 }, file.VisibleHits.Select(hit => hit.LineNumber));
        Assert.True(file.HasMoreHits);
        Assert.Equal(1, file.ExtraHitCount);
        file.ToggleExpandCommand.Execute(null);
        Assert.Equal(new[] { 5, 6, 7, 8 }, file.VisibleHits.Select(hit => hit.LineNumber));
        file.ApplyRefinement("");
        Assert.Equal(original, file.Hits);
        Assert.Equal(original, file.VisibleHits);
        Assert.All(file.Hits, hit => Assert.Equal(new MatchSpan(0, 7), Assert.Single(hit.Highlights)));
    }

    [Fact]
    public void StreamingHitsInvalidateTheRefinedExcerptCache()
    {
        var file = new FileResultViewModel(@"C:\docs\report.txt", new FakeFileLauncher(), isDirectory: false);
        file.AddHits(Enumerable.Range(1, 3).Select(line => new Hit(file.FullPath, line, "invoice ordinary", [])).ToArray());
        Assert.False(file.ApplyRefinement("renewal"));
        _ = file.VisibleHits.ToArray();
        file.AddHit(new Hit(file.FullPath, 4, "invoice renewal", []));
        Assert.True(file.ApplyRefinement("renewal"));
        Assert.Equal(4, Assert.Single(file.VisibleHits).LineNumber);
        Assert.False(file.HasMoreHits);
    }

    [Fact]
    public async Task OpeningARefinedCardUsesTheMatchingSourceLocation()
    {
        string path = Path.GetTempFileName();
        try
        {
            var launcher = new FakeFileLauncher { OpenAtLocationResult = true };
            var file = new FileResultViewModel(path, launcher, isDirectory: false);
            var matched = new Hit(path, 9, "invoice renewal", [new MatchSpan(0, 7)], Locator: new SourceLocator(Page: 3, StartLine: 9));
            file.AddHits([new Hit(path, 1, "invoice original", [new MatchSpan(0, 7)]), matched]);
            Assert.True(file.ApplyRefinement("renewal"));
            await file.OpenCommand.ExecuteAsync(null);
            Assert.Same(matched, launcher.LastOpenedAtHit);
            Assert.Equal(3, launcher.LastOpenedAtHit!.Locator!.Page);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public Task InheritedRefinementHighlightsTextAndHitsWithoutDestroyingThePrimaryHighlights() => OnDispatcherAsync(async () =>
    {
        var panel = new StackPanel();
        var body = new TextBlock(); var path = new TextBlock(); var ordinary = new TextBlock { Text = "Leave this label unchanged" };
        panel.Children.Add(body); panel.Children.Add(path); panel.Children.Add(ordinary);
        var hit = new Hit("report.txt", 1, "invoice renewal RENEWAL", [new MatchSpan(0, 7)], Anchor: SourceAnchor.PdfPage(2));
        HitHighlight.SetHit(body, hit); HitHighlight.SetText(path, @"C:\Renewal\report.txt");
        HitHighlight.SetFilterText(panel, "renewal");
        Assert.Equal(new[] { "invoice", "renewal", "RENEWAL" }, Marked(body));
        Assert.Equal("Renewal", Assert.Single(Marked(path)));
        Assert.Contains("page 2", string.Concat(body.Inlines.OfType<Run>().Select(run => run.Text)));
        Assert.Contains("page 2", System.Windows.Automation.AutomationProperties.GetName(body));
        Assert.Equal("Leave this label unchanged", ordinary.Text);
        HitHighlight.SetFilterText(panel, "report");
        Assert.Equal("invoice", Assert.Single(Marked(body)));
        Assert.Equal("report", Assert.Single(Marked(path)));
        HitHighlight.SetFilterText(panel, "");
        Assert.Equal("invoice", Assert.Single(Marked(body)));
        Assert.Empty(Marked(path));
        Assert.Single(hit.Highlights);
        await Task.CompletedTask;
    });

    [Fact]
    public Task RefinementNarrowsOnlyLoadedResultsAndClearingRestoresTheView() => OnDispatcherAsync(async () =>
    {
        var settings = new FakeSettingsService(); var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var source = new CountingSearcher();
        using var search = new SearchViewModel(source, new ExtractorRegistry([]), new QueryFactory(), new FakePreviewService(),
            new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        var first = new FileResultViewModel(@"C:\Clients\Acme\one.txt", new FakeFileLauncher(), isDirectory: false);
        first.AddHit(new Hit(first.FullPath, 1, "invoice ordinary", [new MatchSpan(0, 7)]));
        var second = new FileResultViewModel(@"C:\Other\two.txt", new FakeFileLauncher(), isDirectory: false);
        second.AddHit(new Hit(second.FullPath, 7, "invoice renewal", [new MatchSpan(0, 7)]));
        search.Files.Add(first); search.Files.Add(second);
        search.RefinementQuery = "acme"; await Task.Delay(350);
        Assert.Same(first, Assert.Single(search.FilesView.Cast<FileResultViewModel>()));
        search.RefinementQuery = "renewal"; await Task.Delay(350);
        Assert.Same(second, Assert.Single(search.FilesView.Cast<FileResultViewModel>()));
        search.RefinementQuery = "invoice OR renewal"; await Task.Delay(350);
        Assert.Empty(search.FilesView.Cast<FileResultViewModel>());
        search.ClearRefinementCommand.Execute(null);
        Assert.Equal(2, search.FilesView.Cast<FileResultViewModel>().Count());
        Assert.Equal(0, source.Calls);
    });

    private static string[] Marked(TextBlock block) => block.Inlines.OfType<Run>().Where(run => run.Background is not null).Select(run => run.Text).ToArray();

    private static async Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private sealed class CountingSearcher : ISearcher
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++; await Task.Yield(); yield break;
        }
    }
}
