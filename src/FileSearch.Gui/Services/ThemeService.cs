using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly ISettingsService _settingsService;
    private readonly IStyleService _styleService;

    /// <summary>Currently-merged built-in overlay dictionary; tracked so we can swap it.</summary>
    private ResourceDictionary? _activeBaseOverlay;

    /// <summary>Currently-merged JSON custom overlay dictionary; tracked so we can swap it.</summary>
    private ResourceDictionary? _activeCustomOverlay;

    /// <summary>True while we're subscribed to OS theme changes (System mode).</summary>
    private bool _listeningToOs;

    public ThemeService(ISettingsService settingsService, IStyleService styleService)
    {
        _settingsService = settingsService;
        _styleService = styleService;
        _styleService.EffectiveApplicationThemeChanged += OnEffectiveApplicationThemeChanged;
        CustomThemeFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FileSearch",
            "Themes");
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public string? CurrentCustomThemeFileName { get; private set; }

    public string CustomThemeFolderPath { get; }

    public IReadOnlyList<CustomThemeInfo> GetCustomThemes()
    {
        Directory.CreateDirectory(CustomThemeFolderPath);

        return Directory
            .EnumerateFiles(CustomThemeFolderPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(TryLoadInfo)
            .Where(static theme => theme is not null)
            .Select(static theme => theme!)
            .OrderBy(static theme => theme.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static theme => theme.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        CurrentCustomThemeFileName = null;

        // Pick the underlying ModernWpf base. Atlas is Light; Dark/VS are Dark;
        // System defers to the OS.
        ApplyApplicationTheme(theme);

        SetOverlays(ResolveOverlay(theme), customOverlay: null);

        // Only track OS theme flips while we're actually following the OS.
        SetOsListening(theme == AppTheme.System);

        // Persist the choice immediately so it survives a crash.
        _settingsService.Update(settings =>
        {
            settings.Theme = theme;
            settings.CustomThemeFileName = string.Empty;
        });
    }

    public bool TrySetCustomTheme(string fileName, out string error)
    {
        error = string.Empty;
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            error = "Choose a theme file.";
            return false;
        }

        var path = Path.Combine(CustomThemeFolderPath, safeFileName);
        if (!File.Exists(path))
        {
            error = $"Theme file was not found: {safeFileName}";
            return false;
        }

        CustomThemeDefinition definition;
        ResourceDictionary overlay;
        try
        {
            definition = CustomThemeJson.LoadDefinition(path);
            overlay = CustomThemeJson.CreateResourceDictionary(definition);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        CurrentTheme = definition.BaseTheme;
        CurrentCustomThemeFileName = safeFileName;
        ApplyApplicationTheme(definition.BaseTheme);
        SetOverlays(ResolveOverlay(definition.BaseTheme), overlay);
        SetOsListening(definition.BaseTheme == AppTheme.System);

        _settingsService.Update(settings =>
        {
            settings.Theme = definition.BaseTheme;
            settings.CustomThemeFileName = safeFileName;
        });

        return true;
    }

    private void OnEffectiveApplicationThemeChanged(object? sender, EventArgs e) =>
        ApplyApplicationTheme(CurrentTheme);

    private void ApplyApplicationTheme(AppTheme theme)
    {
        ThemeManager.Current.ApplicationTheme = _styleService.RequiresDarkApplicationTheme
            ? ApplicationTheme.Dark
            : ResolveApplicationTheme(theme);
    }

    private static ApplicationTheme? ResolveApplicationTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ApplicationTheme.Light,
        AppTheme.Dark or AppTheme.VisualStudio => ApplicationTheme.Dark,
        _ => null,
    };

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
            () => SetOverlays(ResolveOverlay(AppTheme.System), _activeCustomOverlay));
    }

    private void SetOverlays(Uri? baseSource, ResourceDictionary? customOverlay)
    {
        var resources = Application.Current.Resources.MergedDictionaries;

        if (_activeCustomOverlay is not null)
        {
            resources.Remove(_activeCustomOverlay);
            _activeCustomOverlay = null;
        }

        if (_activeBaseOverlay is not null)
        {
            resources.Remove(_activeBaseOverlay);
            _activeBaseOverlay = null;
        }

        if (baseSource is not null)
        {
            _activeBaseOverlay = new ResourceDictionary { Source = baseSource };
            // Append at the end so it wins for any duplicate keys against the
            // underlying ModernWpf theme dictionaries.
            resources.Add(_activeBaseOverlay);
        }

        if (customOverlay is not null)
        {
            _activeCustomOverlay = customOverlay;
            resources.Add(_activeCustomOverlay);
        }

        _styleService.RefreshOverlay();
        ApplyApplicationTheme(CurrentTheme);
    }

    private CustomThemeInfo? TryLoadInfo(string path)
    {
        try
        {
            return CustomThemeJson.LoadInfo(path);
        }
        catch
        {
            return null;
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
