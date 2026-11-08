using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class QuickLocationSettingsSelectionTests
{
    [Fact]
    public void EmptySelectionStaysEmptyWhenSettingsChoicesRefresh()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @"C:\Documents" }, new() { Root = @"D:\Archive" }];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        Assert.All(model.QuickIndexedLocationSelections, row => Assert.False(row.IsSelected));
        model.QuickIndexedLocationSelections[0].IsSelected = true;
        Assert.Equal([@"C:\Documents"], settings.Current.QuickSearchSelectedIndexedRoots);
        model.QuickIndexedLocationSelections[0].IsSelected = false;
        Assert.Empty(settings.Current.QuickSearchSelectedIndexedRoots);

        model.RefreshQuickIndexedLocationsCommand.Execute(null);

        Assert.Equal(2, model.QuickIndexedLocationSelections.Count);
        Assert.All(model.QuickIndexedLocationSelections, row => Assert.False(row.IsSelected));
        Assert.Empty(settings.Current.QuickSearchSelectedIndexedRoots);
    }

    [Fact]
    public void RemovedSelectedRootRemainsVisibleAndIsNotDroppedByOtherSelections()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @"C:\Documents" }];
        settings.Current.QuickSearchSelectedIndexedRoots = [@"D:\Removed"];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        var removed = Assert.Single(model.QuickIndexedLocationSelections, row => row.Root == @"D:\Removed");
        Assert.True(removed.IsSelected);
        Assert.Contains("No longer indexed", removed.DisplayName, StringComparison.Ordinal);
        model.QuickIndexedLocationSelections.Single(row => row.Root == @"C:\Documents").IsSelected = true;
        Assert.Contains(@"D:\Removed", settings.Current.QuickSearchSelectedIndexedRoots);

        model.RefreshQuickIndexedLocationsCommand.Execute(null);
        removed = Assert.Single(model.QuickIndexedLocationSelections, row => row.Root == @"D:\Removed");
        removed.IsSelected = false;

        Assert.Equal([@"C:\Documents"], settings.Current.QuickSearchSelectedIndexedRoots);
    }

    [Fact]
    public void WhitespaceAndDuplicateRowsDoNotCreateExtraChoices()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @" C:\Documents " }, new() { Root = @"c:\documents" }];
        settings.Current.QuickSearchSelectedIndexedRoots = ["", " ", @" C:\Documents ", @"c:\documents"];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        var row = Assert.Single(model.QuickIndexedLocationSelections);
        Assert.Equal(@"C:\Documents", row.Root);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public void UnrelatedSettingsSaveDoesNotOverwriteNewSharedSelection()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @"C:\Documents" }, new() { Root = @"D:\Archive" }];
        settings.Current.QuickSearchSelectedIndexedRoots = [@"C:\Documents"];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        settings.Current.QuickSearchSelectedIndexedRoots = [@"D:\Archive"];
        model.SidebarPageSize = 12;
        model.SaveSettings();

        Assert.Equal([@"D:\Archive"], settings.Current.QuickSearchSelectedIndexedRoots);
        model.RefreshQuickIndexedLocationsCommand.Execute(null);
        Assert.False(model.QuickIndexedLocationSelections.Single(row => row.Root == @"C:\Documents").IsSelected);
        Assert.True(model.QuickIndexedLocationSelections.Single(row => row.Root == @"D:\Archive").IsSelected);
    }

    [Fact]
    public void ExplicitRowChangePreservesAnotherRootSelectedElsewhere()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @"C:\Documents" }, new() { Root = @"D:\Archive" }];
        settings.Current.QuickSearchSelectedIndexedRoots = [@"C:\Documents"];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        settings.Current.QuickSearchSelectedIndexedRoots = [@"C:\Documents", @"D:\Archive"];
        model.QuickIndexedLocationSelections.Single(row => row.Root == @"C:\Documents").IsSelected = false;

        Assert.Equal([@"D:\Archive"], settings.Current.QuickSearchSelectedIndexedRoots);
    }

    [Fact]
    public void LegacyTrailingSeparatorAndCaseVariantUseOneRegisteredChoice()
    {
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations = [new() { Root = @"C:\Documents" }];
        settings.Current.QuickSearchSelectedIndexedRoots = [@"c:\documents\"];
        var model = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        var row = Assert.Single(model.QuickIndexedLocationSelections);
        Assert.Equal(@"C:\Documents", row.Root);
        Assert.True(row.IsSelected);
        Assert.DoesNotContain("No longer indexed", row.DisplayName, StringComparison.Ordinal);
        row.IsSelected = false;
        Assert.Empty(settings.Current.QuickSearchSelectedIndexedRoots);
    }
}
