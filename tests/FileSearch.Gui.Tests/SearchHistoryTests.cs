using System.Collections.ObjectModel;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class SearchHistoryTests
{
    [Fact]
    public void PromoteToFront_InsertsNewEntryAtFront()
    {
        var list = new ObservableCollection<string> { "old" };

        SearchHistory.PromoteToFront(list, "new", maxEntries: 5);

        Assert.Equal(new[] { "new", "old" }, list);
    }

    [Fact]
    public void PromoteToFront_MovesExistingEntryAndRefreshesCasing()
    {
        var list = new ObservableCollection<string> { "alpha", "BETA", "gamma" };

        SearchHistory.PromoteToFront(list, "beta", maxEntries: 5);

        Assert.Equal(new[] { "beta", "alpha", "gamma" }, list);
    }

    [Fact]
    public void PromoteToFront_RemovesCaseInsensitiveDuplicates()
    {
        var list = new ObservableCollection<string> { "alpha", "Query", "QUERY" };

        SearchHistory.PromoteToFront(list, "query", maxEntries: 5);

        Assert.Equal(new[] { "query", "alpha" }, list);
    }

    [Fact]
    public void PromoteToFront_CapsListLength()
    {
        var list = new ObservableCollection<string> { "a", "b", "c" };

        SearchHistory.PromoteToFront(list, "d", maxEntries: 3);

        Assert.Equal(new[] { "d", "a", "b" }, list);
    }

    [Fact]
    public void PromoteToFront_IgnoresWhitespace()
    {
        var list = new ObservableCollection<string> { "a" };

        SearchHistory.PromoteToFront(list, "   ", maxEntries: 5);
        SearchHistory.PromoteToFront(list, null, maxEntries: 5);

        Assert.Equal(new[] { "a" }, list);
    }

    [Fact]
    public void Remove_DeletesCaseInsensitively()
    {
        var list = new ObservableCollection<string> { "Alpha", "beta", "ALPHA" };

        SearchHistory.Remove(list, "alpha");

        Assert.Equal(new[] { "beta" }, list);
    }
}
