using System.ComponentModel;
using System.Windows.Data;
using FileSearch.Core.Engine;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ResultCollectionViewTests
{
    [Fact]
    public void IncrementalGroupChangesRemainVisibleBetweenRefreshes()
    {
        var files = new System.Collections.ObjectModel.ObservableCollection<FileResultViewModel>();
        var view = new ResultCollectionView(files);
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.Directory)));
            view.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.ResultGroupRank), ListSortDirection.Ascending));
        }
        var first = new FileResultViewModel(@"C:\first\a.txt", new FakeFileLauncher(), isDirectory: false);
        var second = new FileResultViewModel(@"C:\second\b.txt", new FakeFileLauncher(), isDirectory: false);
        files.Add(first);
        files.Add(second);
        Assert.Equal(2, view.Groups!.Count);
        Assert.Equal(2, view.Count);
        files.Remove(first);
        Assert.Equal(@"C:\second", ((CollectionViewGroup)Assert.Single(view.Groups!)).Name);
        Assert.Same(second, Assert.Single(view.Cast<FileResultViewModel>()));
        files.Clear();
        Assert.Empty(view.Groups!);
        Assert.Empty(view);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContiguousGroupingPreservesNativeGroupAndFileOrder(bool filter)
    {
        var files = Enumerable.Range(0, 2_000).Select(i =>
        {
            var file = new FileResultViewModel($@"C:\folder{i % 1_000}\{i}.txt", new FakeFileLauncher(), searchRank: i, isDirectory: false);
            file.AddHit(new Hit(file.FullPath, 1, i % 3 == 0 ? "keep" : "skip", [], Score: i % 17));
            return file;
        }).ToArray();
        var native = new ListCollectionView(files);
        var optimized = new ResultCollectionView(files);
        foreach (var view in new ListCollectionView[] { native, optimized })
        {
            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileResultViewModel.Directory)));
                view.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.BestScore), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(FileResultViewModel.SearchRank), ListSortDirection.Ascending));
                if (ReferenceEquals(view, optimized))
                    view.SortDescriptions.Insert(0, new SortDescription(nameof(FileResultViewModel.ResultGroupRank), ListSortDirection.Ascending));
                if (filter)
                    view.Filter = item => ((FileResultViewModel)item).Hits[0].LineContent == "keep";
            }
        }
        Assert.Equal(native.Cast<FileResultViewModel>().Select(file => file.FullPath), optimized.Cast<FileResultViewModel>().Select(file => file.FullPath));
        Assert.Equal(native.Groups!.Cast<CollectionViewGroup>().Select(group => group.Name), optimized.Groups!.Cast<CollectionViewGroup>().Select(group => group.Name));
        Assert.True(optimized.IsDataInGroupOrder);
        var groupEvents = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        ((System.Collections.Specialized.INotifyCollectionChanged)optimized.Groups!).CollectionChanged +=
            (_, args) => groupEvents.Add(args.Action);
        optimized.Refresh();
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset], groupEvents);
        Assert.Equal(native.Cast<FileResultViewModel>().Select(file => file.FullPath), optimized.Cast<FileResultViewModel>().Select(file => file.FullPath));
    }
}
