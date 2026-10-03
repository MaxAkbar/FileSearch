using System.Globalization;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.Converters;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>View-model pieces added for the October 2026 main-window redesign.</summary>
public sealed class RedesignViewModelTests
{
    [Fact]
    public void PreviewParserReadsNumberedListingWithGapsAndHighlights()
    {
        var content = string.Join('\n',
            "      43  addition='header'",
            "\u25ba     46  not public until accepted",
            "---",
            "\u25ba     56  the public summary",
            "      57  p.write_bytes()",
            string.Empty);
        var hits = new[]
        {
            new Hit(@"C:\a.py", 46, "not public until accepted", [new MatchSpan(4, 6)]),
        };

        var lines = PreviewLineViewModel.Parse(content, hits);

        Assert.Equal(5, lines.Count);
        Assert.Equal(43, lines[0].LineNumber);
        Assert.False(lines[0].IsHit);
        Assert.Equal("addition='header'", lines[0].Text);

        Assert.True(lines[1].IsHit);
        Assert.Equal(46, lines[1].LineNumber);
        Assert.Equal(new MatchSpan(4, 6), Assert.Single(lines[1].DisplayHit.Highlights));

        Assert.True(lines[2].IsGap);
        Assert.Equal("Lines 47\u201355 hidden", lines[2].Text);

        Assert.True(lines[3].IsHit);
        Assert.Empty(lines[3].DisplayHit.Highlights);
        Assert.Equal(57, lines[4].LineNumber);
    }

    [Fact]
    public void PreviewParserKeepsUnnumberedSnippetsAsText()
    {
        var lines = PreviewLineViewModel.Parse("\u25ba [page 2] quarterly numbers\n\nsecond line", hits: null);

        Assert.Equal(3, lines.Count);
        Assert.True(lines[0].IsHit);
        Assert.Null(lines[0].LineNumber);
        Assert.Equal("[page 2] quarterly numbers", lines[0].Text);
        Assert.Equal(string.Empty, lines[1].Text);
        Assert.False(lines[2].IsHit);
    }

    [Fact]
    public void PreviewParserReturnsNothingForEmptyContent()
    {
        Assert.Empty(PreviewLineViewModel.Parse(string.Empty, hits: null));
        Assert.Empty(PreviewLineViewModel.Parse(null, hits: null));
    }

    [Fact]
    public void LocationsArePinnedOnceRemovedAndPersisted()
    {
        var settings = new FakeSettingsService();
        var status = new StatusBarViewModel();
        var history = new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status);

        Assert.True(history.LocationList.IsEmpty);

        history.AddLocation(@"C:\temp");
        history.AddLocation(@"c:\TEMP ");
        history.AddLocation(@"D:\Projects");

        Assert.Equal([@"C:\temp", @"D:\Projects"], history.Locations);
        Assert.Equal([@"C:\temp", @"D:\Projects"], settings.Current.Locations);
        Assert.True(history.LocationList.HasContent);

        history.RemoveLocationCommand.Execute(@"C:\temp");
        Assert.Equal([@"D:\Projects"], settings.Current.Locations);

        history.ClearLocationsCommand.Execute(null);
        Assert.Empty(settings.Current.Locations);
        Assert.True(history.LocationList.IsEmpty);
    }

    [Fact]
    public void LocationsLoadFromSettings()
    {
        var settings = new FakeSettingsService();
        settings.Current.Locations.AddRange([@"C:\One", "  ", @"C:\Two"]);
        var status = new StatusBarViewModel();

        var history = new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status);

        Assert.Equal([@"C:\One", @"C:\Two"], history.Locations);
    }

    [Fact]
    public void ActiveScopeTextNamesTheMatchingPresetOrPattern()
    {
        var settings = new FakeSettingsService();
        var status = new StatusBarViewModel();
        var history = new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status);

        history.UpdateActiveScope(string.Empty);
        Assert.Equal("All file types", history.ActiveScopeText);

        history.UpdateActiveScope("*.pdf");
        Assert.Equal("PDFs", history.ActiveScopeText);

        history.UpdateActiveScope("*.log");
        Assert.Equal("*.log", history.ActiveScopeText);
    }

    [Fact]
    public void SidebarCollapseIsPersisted()
    {
        var settings = new FakeSettingsService();
        var appSettings = new ApplicationSettingsViewModel(settings, new StatusBarViewModel());

        Assert.True(appSettings.IsSidebarExpanded);

        appSettings.IsSidebarCollapsed = true;

        Assert.True(settings.Current.IsSidebarCollapsed);
        Assert.False(appSettings.IsSidebarExpanded);
    }

    [Theory]
    [InlineData(@"C:\Users\maxim\source\viking\src", "leaf", "src")]
    [InlineData(@"C:\Users\maxim\source\viking\src", null, "\u2026\\source\\viking")]
    [InlineData(@"C:\temp", "leaf", "temp")]
    [InlineData(@"C:\temp", null, @"C:\")]
    [InlineData(@"C:\Temp\csharpdb", null, @"C:\Temp")]
    public void FolderPathPartConverterSplitsLeafAndShortParent(string path, string? part, string expected)
    {
        var converter = new FolderPathPartConverter();

        var actual = converter.Convert(path, typeof(string), part, CultureInfo.InvariantCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PathsEqualConverterIgnoresCaseAndTrailingSeparator()
    {
        var converter = new PathsEqualConverter();

        Assert.Equal(true, converter.Convert([@"C:\Temp\", @"c:\temp"], typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([@"C:\Temp", @"C:\Temp2"], typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
