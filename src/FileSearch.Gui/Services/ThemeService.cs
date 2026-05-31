using System;
using System.Windows;
using FileSearch.Gui.Settings;
using ModernWpf;

namespace FileSearch.Gui.Services;

public sealed class ThemeService : IThemeService
{
    private static readonly Uri s_visualStudioTheme =
        new("Themes/VisualStudio.xaml", UriKind.Relative);

    private readonly ISettingsStore _settingsStore;

    /// <summary>
    /// Currently-merged overlay dictionary (e.g. VS theme).
    /// Tracked so we can remove it when switching to a different theme.
    /// </summary>
    private ResourceDictionary? _activeOverlay;

    public ThemeService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        // Pick the underlying ModernWpf base. VS theme is a Dark variant.
        ThemeManager.Current.ApplicationTheme = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark or AppTheme.VisualStudio => ApplicationTheme.Dark,
            _ => null, // System
        };

        // Layer (or remove) the theme-specific brush overrides.
        SetOverlay(theme switch
        {
            AppTheme.VisualStudio => s_visualStudioTheme,
            _ => null,
        });

        // Persist the choice immediately so it survives a crash.
        var settings = _settingsStore.Load();
        settings.Theme = theme;
        _settingsStore.Save(settings);
    }

    private void SetOverlay(Uri? source)
    {
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;

        if (_activeOverlay is not null)
        {
            resources.Remove(_activeOverlay);
            _activeOverlay = null;
        }

        if (source is not null)
        {
            _activeOverlay = new ResourceDictionary { Source = source };
            // Append at the end so it wins for any duplicate keys against
            // the underlying ModernWpf theme dictionaries.
            resources.Add(_activeOverlay);
        }
    }
}
