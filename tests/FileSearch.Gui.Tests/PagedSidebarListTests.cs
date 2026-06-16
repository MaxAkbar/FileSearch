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
    public void PageSizeCanBeChangedAfterConstruction()
    {
        var source = new ObservableCollection<string>
        {
            "alpha",
            "beta",
            "gamma",
        };
        var list = new PagedSidebarList<string>(
            source,
            (item, needle) => item.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "items",
            pageSize: 2);

        Assert.True(list.IsPagerVisible);

        list.PageSize = 3;

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, list.Items);
        Assert.False(list.IsPagerVisible);
        Assert.Equal("1-3 of 3", list.PageSummaryText);
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
        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);

        var history = new HistoryViewModel(settings, appSettings, status);

        Assert.Contains(history.ScopeList.Items, item => item.Name == "All files" && !item.IsCustom);
        Assert.Contains(history.ScopeList.Items, item => item.Name == "Generated" && item.IsCustom);

        history.ScopeList.SearchText = "generated";

        var item = Assert.Single(history.ScopeList.Items);
        Assert.Equal("Generated", item.Name);
        Assert.Equal("*.g.cs", item.FileNamePattern);
    }

    [Fact]
    public void HistoryListsFollowApplicationSidebarPageSizeSetting()
    {
        var settings = new FakeSettingsService();
        settings.Current.SidebarPageSize = 3;
        settings.Current.RecentPaths.AddRange(
        [
            @"C:\One",
            @"C:\Two",
            @"C:\Three",
            @"C:\Four",
        ]);

        var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);

        Assert.Equal(3, history.RecentPathList.Items.Count);
        Assert.True(history.RecentPathList.IsPagerVisible);

        appSettings.SidebarPageSize = 5;

        Assert.Equal(4, history.RecentPathList.Items.Count);
        Assert.False(history.RecentPathList.IsPagerVisible);
        Assert.Equal(5, settings.Current.SidebarPageSize);
    }
}
