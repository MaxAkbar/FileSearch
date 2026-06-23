using System;
using System.Windows;
using FileSearch.Gui.Settings;
using Application = System.Windows.Application;

namespace FileSearch.Gui.Services;

public sealed class StyleService : IStyleService
{
    private static readonly Uri s_compactStyle = new("Styles/Compact.xaml", UriKind.Relative);
    private static readonly Uri s_velaStyle = new("Styles/Vela.xaml", UriKind.Relative);

    private readonly ISettingsService _settingsService;
    private ResourceDictionary? _activeStyleOverlay;

    public StyleService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public AppStyle CurrentStyle { get; private set; } = AppStyle.Comfortable;

    public bool RequiresDarkApplicationTheme => CurrentStyle == AppStyle.Vela;

    public event EventHandler? EffectiveApplicationThemeChanged;

    public void SetStyle(AppStyle style)
    {
        if (!Enum.IsDefined(style))
            style = AppStyle.Comfortable;

        var previouslyRequiredDarkBase = RequiresDarkApplicationTheme;
        CurrentStyle = style;
        SetOverlay(ResolveOverlay(style));

        _settingsService.Update(settings => settings.Style = style);

        if (previouslyRequiredDarkBase != RequiresDarkApplicationTheme)
            EffectiveApplicationThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshOverlay() => SetOverlay(ResolveOverlay(CurrentStyle));

    private static Uri? ResolveOverlay(AppStyle style) => style switch
    {
        AppStyle.Compact => s_compactStyle,
        AppStyle.Vela => s_velaStyle,
        _ => null,
    };

    private void SetOverlay(Uri? source)
    {
        var resources = Application.Current.Resources.MergedDictionaries;

        if (_activeStyleOverlay is not null)
        {
            resources.Remove(_activeStyleOverlay);
            _activeStyleOverlay = null;
        }

        if (source is null)
            return;

        _activeStyleOverlay = new ResourceDictionary { Source = source };
        resources.Add(_activeStyleOverlay);
    }
}
