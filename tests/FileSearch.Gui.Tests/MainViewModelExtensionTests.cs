using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class MainViewModelExtensionTests
{
    [Fact]
    public void ParseExtensions_NormalizesSeparatorsDotsAndDuplicates()
    {
        var extensions = SearchViewModel.ParseExtensions("liquid; *.tmpl, .FOO\nfoo  bar");

        Assert.Equal(new[] { ".liquid", ".tmpl", ".foo", ".bar" }, extensions);
    }

    [Fact]
    public void SidebarSelectionFlagsFollowThemeAndStyleChanges()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.Style = AppStyle.Compact;
        var styleService = new FakeStyleService();
        var themeService = new FakeThemeService();
        var settings = new ApplicationSettingsViewModel(
            settingsService,
            new StatusBarViewModel(),
            styleService: styleService);
        var vm = new MainViewModel(
            search: null!,
            index: null!,
            history: null!,
            settings,
            new StatusBarViewModel(),
            workflows: null!,
            themeService,
            styleService,
            new FakeShellIntegrationService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.IsSystemThemeSelected);
        Assert.True(vm.IsCompactStyleSelected);

        vm.ApplyThemeCommand.Execute("Dark");

        Assert.True(vm.IsDarkThemeSelected);
        Assert.False(vm.IsSystemThemeSelected);
        Assert.Contains(nameof(MainViewModel.IsDarkThemeSelected), changed);
        Assert.Contains(nameof(MainViewModel.IsSystemThemeSelected), changed);

        changed.Clear();
        vm.ApplyStyleCommand.Execute("Vela");

        Assert.True(vm.IsVelaStyleSelected);
        Assert.False(vm.IsCompactStyleSelected);
        Assert.Equal(AppStyle.Vela, settings.SelectedStyle.Value);
        Assert.Equal(AppStyle.Vela, styleService.CurrentStyle);
        Assert.Contains(nameof(MainViewModel.IsVelaStyleSelected), changed);
        Assert.Contains(nameof(MainViewModel.IsCompactStyleSelected), changed);

        changed.Clear();
        settings.SelectedStyle = settings.StyleOptions.Single(option => option.Value == AppStyle.Comfortable);

        Assert.True(vm.IsComfortableStyleSelected);
        Assert.False(vm.IsVelaStyleSelected);
        Assert.Contains(nameof(MainViewModel.IsComfortableStyleSelected), changed);
        Assert.Contains(nameof(MainViewModel.IsVelaStyleSelected), changed);
    }
}
