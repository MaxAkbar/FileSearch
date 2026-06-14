using System.ComponentModel;
using FileSearch.Core.Indexing;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// Covers the one runtime cross-VM dependency the split introduced: the
/// index view model listening to the search view model's SearchPath.
/// </summary>
public sealed class IndexViewModelTests
{
    [Fact]
    public void SearchPathChangeRefreshesCurrentFolderState()
    {
        var (search, index) = Build();
        var raised = new List<string>();
        index.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var canExecuteChanged = false;
        index.AddCurrentFolderToIndexCommand.CanExecuteChanged += (_, _) => canExecuteChanged = true;

        search.SearchPath = Path.GetTempPath();

        Assert.Contains(nameof(IndexViewModel.IsCurrentFolderIndexed), raised);
        Assert.Contains(nameof(IndexViewModel.CurrentFolderIndexActionText), raised);
        Assert.True(canExecuteChanged);
    }

    [Fact]
    public void DisposeStopsListeningToSearchChanges()
    {
        var (search, index) = Build();
        index.Dispose();

        var raised = new List<string>();
        index.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        search.SearchPath = Path.GetTempPath();

        Assert.Empty(raised);
    }

    [Fact]
    public void ActiveRefreshProgressUpdatesLocationRuntimeSummary()
    {
        var root = Path.GetTempPath();
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations.Add(new() { Root = root });
        var history = new HistoryViewModel(settings, status);
        var search = new SearchViewModel(
            new NullSearcher(),
            new ExtractorRegistry(Array.Empty<ITextExtractor>()),
            new QueryFactory(),
            new FakePreviewService(),
            new FakeFileLauncher(),
            settings,
            new FakeFileTypeOptionsStore(),
            new FakeFolderPicker(),
            history,
            status);
        var indexingService = new FakeIndexingService();
        var index = new IndexViewModel(
            new FakeFileIndex(),
            indexingService,
            settings,
            new FakeFileLauncher(),
            new InlineDispatcher(),
            search,
            status);

        indexingService.RaiseStatus(new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: true,
            QueueLength: 0,
            Message: "Scanning 10; 2 changed, 8 unchanged",
            ActiveRoot: IndexPath.NormalizeRoot(root),
            ActiveKind: IndexChangeKind.RefreshRoot,
            ActiveProgress: new IndexProgress(
                FilesEnumerated: 10,
                FilesIndexed: 2,
                FilesSkippedUnchanged: 8,
                FilesRemoved: 0,
                FilesFailed: 0,
                LinesIndexed: 20)));

        var location = Assert.Single(index.IndexedLocations);
        Assert.Equal("Scanning 10; 2 changed, 8 unchanged", location.RuntimeStatusSummary);
    }

    private static (SearchViewModel Search, IndexViewModel Index) Build()
    {
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        var history = new HistoryViewModel(settings, status);
        var search = new SearchViewModel(
            new NullSearcher(),
            new ExtractorRegistry(Array.Empty<ITextExtractor>()),
            new QueryFactory(),
            new FakePreviewService(),
            new FakeFileLauncher(),
            settings,
            new FakeFileTypeOptionsStore(),
            new FakeFolderPicker(),
            history,
            status);
        var index = new IndexViewModel(
            new FakeFileIndex(),
            new FakeIndexingService(),
            settings,
            new FakeFileLauncher(),
            new InlineDispatcher(),
            search,
            status);
        return (search, index);
    }

    private sealed class NullSearcher : FileSearch.Core.Engine.ISearcher
    {
        public async IAsyncEnumerable<FileSearch.Core.Engine.Hit> SearchAsync(
            FileSearch.Core.Engine.SearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
