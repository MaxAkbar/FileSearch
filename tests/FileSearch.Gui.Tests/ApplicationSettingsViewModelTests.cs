using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ApplicationSettingsViewModelTests
{
    [Fact]
    public void BackgroundSettingsLoadFromSettings()
    {
        var settings = new FakeSettingsService();
        settings.Current.KeepIndexUpdatedAfterClose = true;
        settings.Current.StartBackgroundIndexerAtSignIn = true;

        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            new FakeStartupRegistrationService());

        Assert.True(appSettings.KeepIndexUpdatedAfterClose);
        Assert.True(appSettings.StartBackgroundIndexerAtSignIn);
    }

    [Fact]
    public void KeepIndexUpdatedAfterCloseTogglePersistsWithoutStartupRegistration()
    {
        var settings = new FakeSettingsService();
        var startup = new FakeStartupRegistrationService();
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            startup);

        appSettings.KeepIndexUpdatedAfterClose = true;

        Assert.True(settings.Current.KeepIndexUpdatedAfterClose);
        Assert.Equal(0, startup.EnableCallCount);
        Assert.Equal(0, startup.DisableCallCount);
    }

    [Fact]
    public void StartBackgroundIndexerAtSignInTogglePersistsAndUpdatesStartupRegistration()
    {
        var settings = new FakeSettingsService();
        var startup = new FakeStartupRegistrationService();
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            startup);

        appSettings.StartBackgroundIndexerAtSignIn = true;

        Assert.True(settings.Current.StartBackgroundIndexerAtSignIn);
        Assert.True(startup.IsEnabled);
        Assert.Equal(1, startup.EnableCallCount);
        Assert.Equal(0, startup.DisableCallCount);

        appSettings.StartBackgroundIndexerAtSignIn = false;

        Assert.False(settings.Current.StartBackgroundIndexerAtSignIn);
        Assert.False(startup.IsEnabled);
        Assert.Equal(1, startup.EnableCallCount);
        Assert.Equal(1, startup.DisableCallCount);
    }

    [Fact]
    public void RunInBackgroundRegistrationFailureDoesNotDiscardPersistedSetting()
    {
        var settings = new FakeSettingsService();
        var status = new StatusBarViewModel();
        var startup = new FakeStartupRegistrationService
        {
            ThrowOnEnable = true,
        };
        var appSettings = new ApplicationSettingsViewModel(settings, status, startup);

        appSettings.StartBackgroundIndexerAtSignIn = true;

        Assert.True(settings.Current.StartBackgroundIndexerAtSignIn);
        Assert.StartsWith("Settings saved, but startup registration failed", status.Text);
    }

    [Fact]
    public void RuntimeLimitSettingsPersistWithoutStartupRegistration()
    {
        var settings = new FakeSettingsService();
        var startup = new FakeStartupRegistrationService();
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            startup);

        appSettings.PauseIndexingOnBattery = true;
        appSettings.IndexOnlyWhenIdle = true;
        appSettings.IndexerCpuLimitPercent = 25;
        appSettings.IndexerDiskPauseMilliseconds = 100;

        Assert.True(settings.Current.PauseIndexingOnBattery);
        Assert.True(settings.Current.IndexOnlyWhenIdle);
        Assert.Equal(25, settings.Current.IndexerCpuLimitPercent);
        Assert.Equal(100, settings.Current.IndexerDiskPauseMilliseconds);
        Assert.Equal(0, startup.EnableCallCount);
        Assert.Equal(0, startup.DisableCallCount);
    }

    [Fact]
    public void CustomThemeSelectionLoadsFromSettingsAndAppliesTheme()
    {
        var settings = new FakeSettingsService();
        settings.Current.CustomThemeFileName = "nord.json";
        var themeService = new FakeThemeService
        {
            Themes =
            [
                new CustomThemeInfo("Nord", "nord.json", @"C:\Themes\nord.json", AppTheme.Dark),
                new CustomThemeInfo("Gruvbox", "gruvbox.json", @"C:\Themes\gruvbox.json", AppTheme.Dark),
            ],
        };
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            themeService: themeService);

        Assert.Equal("nord.json", appSettings.SelectedCustomTheme?.FileName);

        appSettings.SelectedCustomTheme = appSettings.CustomThemes.Single(theme => theme.FileName == "gruvbox.json");

        Assert.Equal(1, themeService.SetCustomThemeCallCount);
        Assert.Equal("gruvbox.json", themeService.CurrentCustomThemeFileName);
    }

    [Fact]
    public void UseBuiltInThemeClearsCustomThemeSelection()
    {
        var settings = new FakeSettingsService();
        settings.Current.Theme = AppTheme.VisualStudio;
        settings.Current.CustomThemeFileName = "nord.json";
        var themeService = new FakeThemeService
        {
            Themes =
            [
                new CustomThemeInfo("Nord", "nord.json", @"C:\Themes\nord.json", AppTheme.Dark),
            ],
        };
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            themeService: themeService);

        appSettings.UseBuiltInThemeCommand.Execute(null);

        Assert.Null(appSettings.SelectedCustomTheme);
        Assert.Equal(1, themeService.SetThemeCallCount);
        Assert.Equal(AppTheme.VisualStudio, themeService.CurrentTheme);
    }
}
