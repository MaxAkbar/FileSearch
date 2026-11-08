using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ArchiveStoredPreviewTests
{
    private const string Captured = "captured PDF needle on the matching page";
    private const string Reextracted = "nonempty text from the wrong member at the shifted global line";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MainArchiveMatchesUseCapturedIdentityEvenForAnAlreadyQueuedWholeFileAction(bool indexed, bool wholeFile)
    {
        WithMain((pump, vm, preview) =>
        {
            var file = ArchiveFile(indexed);
            vm.SelectedFile = file;
            pump.PumpUntil(() => vm.PreviewContent.Contains(Captured, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.False(vm.ToggleWholeFilePreviewCommand.CanExecute(null));
            Assert.Equal("Use File preview", vm.WholeFileToggleText);
            Assert.Contains("complete member", vm.WholeFileToggleToolTip);
            if (wholeFile)
            {
                // A queued command can execute after its button becomes disabled.
                // Its whole-file state must never authorize offset-based ZIP re-extraction.
                var pending = vm.ToggleWholeFilePreviewCommand.ExecuteAsync(null);
                pump.PumpUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(5));
                pending.GetAwaiter().GetResult();
                Assert.True(vm.IsWholeFilePreview);
            }
            Assert.Contains(Captured, vm.PreviewContent);
            Assert.Contains("reports/matching.pdf", vm.PreviewContent);
            Assert.DoesNotContain(Reextracted, vm.PreviewContent);
            Assert.Equal(0, preview.ExcerptCalls);
            Assert.Equal(0, preview.WholeFileCalls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuickArchiveMatchesNeverReresolveGlobalOffsetsWithDifferentOcrCoverage(bool indexed)
    {
        WithMain((pump, main, preview) =>
        {
            var settings = new FakeSettingsService();
            using var quick = new QuickSearchViewModel(new EmptySearcher(), new QueryFactory(), preview,
                new FakeFileLauncher(), settings, main, new FakeFolderPicker());
            var file = ArchiveFile(indexed);
            quick.Results.Add(file);
            quick.SelectedResult = file;
            var pending = quick.PreviewResultCommand.ExecuteAsync(null);
            pump.PumpUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(5));
            pending.GetAwaiter().GetResult();
            Assert.Contains(Captured, quick.PreviewContent);
            Assert.Contains("reports/matching.pdf", quick.PreviewContent);
            Assert.DoesNotContain(Reextracted, quick.PreviewContent);
            Assert.Equal(0, preview.ExcerptCalls);
            Assert.Equal(0, preview.WholeFileCalls);
        });
    }

    [Fact]
    public void OrdinaryDocumentOffsetsKeepTheirExistingPreviewRoute()
    {
        WithMain((pump, vm, preview) =>
        {
            var file = new FileResultViewModel(@"C:\fixtures\ordinary.docx", new FakeFileLauncher());
            file.AddHit(new Hit(file.FullPath, 90, "needle", [], Anchor: new SourceAnchor(SourceAnchorKind.Word,
                "paragraph 90", Line: 90, MemberPath: "word/document.xml")));
            vm.SelectedFile = file;
            pump.PumpUntil(() => vm.PreviewContent == Reextracted, TimeSpan.FromSeconds(5));
            Assert.Equal(1, preview.ExcerptCalls);
            Assert.True(vm.ToggleWholeFilePreviewCommand.CanExecute(null));
        });
    }

    private static FileResultViewModel ArchiveFile(bool indexed)
    {
        var file = new FileResultViewModel(@"C:\fixtures\documents.zip", new FakeFileLauncher());
        var inner = SourceAnchor.PdfPage(2);
        var outer = new SourceAnchor(SourceAnchorKind.Archive, "archive member reports/matching.pdf · page 2",
            Line: 7, Page: 2, MemberPath: "reports/matching.pdf", ArchiveFingerprint: "1234:5678", ArchiveContentAnchor: inner);
        var locator = SourceLocator.FromAnchor(outer);
        // Earlier OCR lines make this global offset differ from the local PDF line.
        file.AddHit(new Hit(file.FullPath, 90, Captured, [], Anchor: indexed ? null : outer,
            Locator: indexed ? locator : null, Snippet: indexed ? new SearchSnippet(Captured, locator: locator) : null));
        return file;
    }

    private static void WithMain(Action<PumpingSynchronizationContext, SearchViewModel, MisleadingPreview> action) => StaTestDispatcher.Run(() =>
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var settings = new FakeSettingsService();
            var status = new StatusBarViewModel();
            var options = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, options, status);
            var preview = new MisleadingPreview();
            using var main = new SearchViewModel(new EmptySearcher(), new ExtractorRegistry([]), new QueryFactory(),
                preview, new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
            action(pump, main, preview);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class MisleadingPreview : IFilePreviewService
    {
        public int ExcerptCalls { get; private set; }
        public int WholeFileCalls { get; private set; }
        public Task<string> LoadHitsPreviewAsync(string path, IReadOnlyList<int> hitLineNumbers, int contextLines, CancellationToken token)
        { ExcerptCalls++; return Task.FromResult(Reextracted); }
        public Task<string> LoadFullTextAsync(string path, CancellationToken token)
        { WholeFileCalls++; return Task.FromResult(Reextracted); }
    }

    private sealed class EmptySearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
    }
}
