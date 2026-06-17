using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class AppWindowLifetimeTests
{
    [Fact]
    public void StartupShowsWindowUnlessBackgroundFlagIsSet()
    {
        Assert.True(AppWindowLifetime.ShouldShowOnStartup(new AppStartupOptions(false, null)));
        Assert.False(AppWindowLifetime.ShouldShowOnStartup(new AppStartupOptions(true, null)));
    }

    [Fact]
    public void CloseHidesOnlyForBackgroundModeWithoutExplicitExit()
    {
        Assert.True(AppWindowLifetime.ShouldHideOnMainWindowClose(runInBackground: true, explicitExitRequested: false));
        Assert.False(AppWindowLifetime.ShouldHideOnMainWindowClose(runInBackground: true, explicitExitRequested: true));
        Assert.False(AppWindowLifetime.ShouldHideOnMainWindowClose(runInBackground: false, explicitExitRequested: false));
    }

    [Fact]
    public void ExistingInstanceShowsForVisibleOrFolderActivation()
    {
        Assert.True(AppWindowLifetime.ShouldShowOnActivation(new AppStartupOptions(false, null)));
        Assert.True(AppWindowLifetime.ShouldShowOnActivation(new AppStartupOptions(true, @"C:\Work")));
        Assert.False(AppWindowLifetime.ShouldShowOnActivation(new AppStartupOptions(true, null)));
    }
}
