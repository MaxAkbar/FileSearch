using System.Collections.ObjectModel;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class PagedSidebarListTests
{
    [Fact]
    public void FiltersAndPagesItems()
    {
        var source = new ObservableCollection<string>
        {
            "alpha",
            "beta",
            "gamma",
            "delta",
            "epsilon",
        };
        var list = new PagedSidebarList<string>(
            source,
            (item, needle) => item.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "items",
            pageSize: 2);

        Assert.Equal(new[] { "alpha", "beta" }, list.Items);
        Assert.Equal("1-2 of 5", list.PageSummaryText);
        Assert.True(list.IsPagerVisible);
        Assert.True(list.NextPageCommand.CanExecute(null));

        list.NextPageCommand.Execute(null);

        Assert.Equal(new[] { "gamma", "delta" }, list.Items);
        Assert.Equal("3-4 of 5", list.PageSummaryText);

        list.SearchText = "ta";

        Assert.Equal(new[] { "beta", "delta" }, list.Items);
        Assert.Equal("1-2 of 2", list.PageSummaryText);
        Assert.False(list.IsPagerVisible);
        Assert.True(list.HasSearchText);
    }

    [Fact]
    public void RefreshesWhenSourceChanges()
    {
        var source = new ObservableCollection<string> { "alpha" };
        var list = new PagedSidebarList<string>(
            source,
            (item, needle) => item.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "items",
            pageSize: 3);

        source.Add("beta");

        Assert.Equal(new[] { "alpha", "beta" }, list.Items);
        Assert.Equal("1-2 of 2", list.PageSummaryText);
    }

    [Fact]
    public void HistoryScopeListCombinesBuiltInsAndCustomScopes()
    {
        var settings = new FakeSettingsService();
        settings.Current.CustomScopes.Add(new SearchScope
        {
            Name = "Generated",
            FileNamePattern = "*.g.cs",
        });

        var history = new HistoryViewModel(settings, new StatusBarViewModel());

        Assert.Contains(history.ScopeList.Items, item => item.Name == "All files" && !item.IsCustom);
        Assert.Contains(history.ScopeList.Items, item => item.Name == "Generated" && item.IsCustom);

        history.ScopeList.SearchText = "generated";

        var item = Assert.Single(history.ScopeList.Items);
        Assert.Equal("Generated", item.Name);
        Assert.Equal("*.g.cs", item.FileNamePattern);
    }
}
