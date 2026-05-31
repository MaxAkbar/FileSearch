namespace FileSearch.Gui.Services;

public enum AppTheme
{
    Light,
    Dark,
    /// <summary>Visual Studio Dark — sits on top of the Dark base with a VS-inspired palette.</summary>
    VisualStudio,
    System,
}

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    void SetTheme(AppTheme theme);
}
