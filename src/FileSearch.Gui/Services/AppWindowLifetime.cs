namespace FileSearch.Gui.Services;

internal static class AppWindowLifetime
{
    public static bool ShouldShowOnStartup(AppStartupOptions options) =>
        !options.StartInBackground;

    public static bool ShouldShowOnActivation(AppStartupOptions options) =>
        !options.StartInBackground || !string.IsNullOrWhiteSpace(options.StartupFolder);

    public static bool ShouldHideOnMainWindowClose(bool runInBackground, bool explicitExitRequested) =>
        runInBackground && !explicitExitRequested;
}
