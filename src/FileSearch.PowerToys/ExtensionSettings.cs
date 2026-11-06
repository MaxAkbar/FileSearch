using Microsoft.CommandPalette.Extensions.Toolkit;

namespace FileSearch.PowerToys;

internal sealed class ExtensionSettings : JsonSettingsManager
{
    private readonly TextSetting _appPath = new(
        "appPath", "FileSearch application", "Optional path to FileSearch.Gui.exe, including portable installations.", "");
    private readonly TextSetting _indexPath = new(
        "indexPath", "Custom FileSearch index (optional)",
        "Leave blank to use the standard FileSearch index. After changing this path, reload the extension.", "");

    public ExtensionSettings()
    {
        FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch", "PowerToys", "settings.json");
        Settings.Add(_appPath);
        Settings.Add(_indexPath);
        LoadSettings();
        Settings.SettingsChanged += (_, _) => SaveSettings();
    }

    public string AppPath => _appPath.Value ?? string.Empty;
    public string IndexPath => _indexPath.Value ?? string.Empty;

    public override void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            base.SaveSettings();
        }
        catch (IOException)
        {
            // The companion remains usable if its own settings cannot be saved.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
