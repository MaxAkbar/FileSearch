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

    /// <summary>
    /// Set by the theme service to the effective light/dark preference. Styles
    /// that own their palette (Vela) use it to pick their light or dark token
    /// set instead of forcing one base theme.
    /// </summary>
    bool PrefersLightPalette { get; set; }

    /// <summary>
    /// Raised after the style overlay set changes. The theme service listens
    /// and re-asserts its overlays: freshly re-merged theme dictionaries
    /// resolve their values under the new layering, and the effective
    /// application theme is re-resolved for styles that require a dark
    /// ModernWpf base (Vela).
    /// </summary>
    event EventHandler? OverlayChanged;

    void SetStyle(AppStyle style);

    void RefreshOverlay();
}
