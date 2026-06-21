using System;
using System.Windows;
using FileSearch.Gui.Settings;
using Application = System.Windows.Application;

namespace FileSearch.Gui.Services;

public sealed class StyleService : IStyleService
{
    private static readonly Uri s_compactStyle = new("Styles/Compact.xaml", UriKind.Relative);

    private readonly ISettingsService _settingsService;
    private ResourceDictionary? _activeStyleOverlay;

    public StyleService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public AppStyle CurrentStyle { get; private set; } = AppStyle.Comfortable;

    public void SetStyle(AppStyle style)
    {
        if (!Enum.IsDefined(style))
            style = AppStyle.Comfortable;

        CurrentStyle = style;
        SetOverlay(style == AppStyle.Compact ? s_compactStyle : null);

        _settingsService.Update(settings => settings.Style = style);
    }

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
