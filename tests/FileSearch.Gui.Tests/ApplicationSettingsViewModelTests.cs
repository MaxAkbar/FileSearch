using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ApplicationSettingsViewModelTests
{
    [Fact]
    public void RunInBackgroundLoadsFromSettings()
    {
        var settings = new FakeSettingsService();
        settings.Current.RunInBackground = true;

        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            new FakeStartupRegistrationService());

        Assert.True(appSettings.RunInBackground);
    }

    [Fact]
    public void RunInBackgroundTogglePersistsAndUpdatesStartupRegistration()
    {
        var settings = new FakeSettingsService();
        var startup = new FakeStartupRegistrationService();
        var appSettings = new ApplicationSettingsViewModel(
            settings,
            new StatusBarViewModel(),
            startup);

        appSettings.RunInBackground = true;

        Assert.True(settings.Current.RunInBackground);
        Assert.True(startup.IsEnabled);
        Assert.Equal(1, startup.EnableCallCount);
        Assert.Equal(0, startup.DisableCallCount);

        appSettings.RunInBackground = false;

        Assert.False(settings.Current.RunInBackground);
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

        appSettings.RunInBackground = true;

        Assert.True(settings.Current.RunInBackground);
        Assert.StartsWith("Settings saved, but startup registration failed", status.Text);
    }
}
