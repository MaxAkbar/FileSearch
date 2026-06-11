using System.ComponentModel;
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
