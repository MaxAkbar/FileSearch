namespace FileSearch.Gui.Services;

public enum AppStyle
{
    Comfortable,
    Compact,
    Vela,
}

public interface IStyleService
{
    AppStyle CurrentStyle { get; }

    bool RequiresDarkApplicationTheme { get; }

    event EventHandler? EffectiveApplicationThemeChanged;

    void SetStyle(AppStyle style);

    void RefreshOverlay();
}
