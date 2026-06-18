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
    string? CurrentCustomThemeFileName { get; }
    string CustomThemeFolderPath { get; }
    IReadOnlyList<CustomThemeInfo> GetCustomThemes();
    void SetTheme(AppTheme theme);
    bool TrySetCustomTheme(string fileName, out string error);
}

public sealed record CustomThemeInfo(
    string Name,
    string FileName,
    string Path,
    AppTheme BaseTheme)
{
    public string DisplayName => string.Equals(Name, FileName, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{Name} ({FileName})";
}
