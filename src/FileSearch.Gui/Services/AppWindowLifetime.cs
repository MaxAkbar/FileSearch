namespace FileSearch.Gui.Services;

internal static class AppWindowLifetime
{
    public static bool ShouldShowOnStartup(AppStartupOptions options) =>
        !options.StartInBackground || !string.IsNullOrWhiteSpace(options.QueryText);

    public static bool ShouldShowOnActivation(AppStartupOptions options) =>
        !options.StartInBackground || !string.IsNullOrWhiteSpace(options.StartupFolder) ||
        !string.IsNullOrWhiteSpace(options.QueryText);

    public static bool ShouldKeepResidentOnMainWindowClose(
        bool keepIndexUpdatedAfterClose,
        bool explicitExitRequested) =>
        keepIndexUpdatedAfterClose && !explicitExitRequested;
}
