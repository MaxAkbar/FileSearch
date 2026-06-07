using System;
using System.Windows;
using FileSearch.Gui.Settings;
using Microsoft.Win32;
using ModernWpf;
using Application = System.Windows.Application;

namespace FileSearch.Gui.Services;

public sealed class ThemeService : IThemeService
{
    // Each theme is a brush overlay merged on top of the ModernWpf base
    // dictionaries. Atlas (the warm "paper" look) is the Light theme; the
    // others layer their own palette on a Dark base.
    private static readonly Uri s_atlasLightTheme = new("Themes/Atlas.xaml", UriKind.Relative);
    private static readonly Uri s_atlasDarkTheme = new("Themes/AtlasDark.xaml", UriKind.Relative);
    private static readonly Uri s_visualStudioTheme = new("Themes/VisualStudio.xaml", UriKind.Relative);

    private readonly ISettingsStore _settingsStore;

    /// <summary>Currently-merged overlay dictionary; tracked so we can swap it.</summary>
    private ResourceDictionary? _activeOverlay;

    /// <summary>True while we're subscribed to OS theme changes (System mode).</summary>
    private bool _listeningToOs;

    public ThemeService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        // Pick the underlying ModernWpf base. Atlas is Light; Dark/VS are Dark;
        // System defers to the OS.
        ThemeManager.Current.ApplicationTheme = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark or AppTheme.VisualStudio => ApplicationTheme.Dark,
            _ => null, // System
        };

        SetOverlay(ResolveOverlay(theme));

        // Only track OS theme flips while we're actually following the OS.
        SetOsListening(theme == AppTheme.System);

        // Persist the choice immediately so it survives a crash.
        var settings = _settingsStore.Load();
        settings.Theme = theme;
        _settingsStore.Save(settings);
    }

    private static Uri ResolveOverlay(AppTheme theme) => theme switch
    {
        AppTheme.Light => s_atlasLightTheme,
        AppTheme.Dark => s_atlasDarkTheme,
        AppTheme.VisualStudio => s_visualStudioTheme,
        _ => IsOsDark() ? s_atlasDarkTheme : s_atlasLightTheme, // System
    };

    private void SetOsListening(bool listen)
    {
        if (listen == _listeningToOs)
            return;

        if (listen)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        else
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _listeningToOs = listen;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || CurrentTheme != AppTheme.System)
            return;

        // SystemEvents fires off the UI thread; marshal the swap back.
        Application.Current?.Dispatcher.Invoke(
            () => SetOverlay(ResolveOverlay(AppTheme.System)));
    }

    private void SetOverlay(Uri? source)
    {
        var resources = Application.Current.Resources.MergedDictionaries;

        if (_activeOverlay is not null)
        {
            resources.Remove(_activeOverlay);
            _activeOverlay = null;
        }

        if (source is not null)
        {
            _activeOverlay = new ResourceDictionary { Source = source };
            // Append at the end so it wins for any duplicate keys against the
            // underlying ModernWpf theme dictionaries.
            resources.Add(_activeOverlay);
        }
    }

    private static bool IsOsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme)
                return appsUseLightTheme == 0;
        }
        catch
        {
            // Registry shape varies across builds; default to light on any miss.
        }

        return false;
    }
}
