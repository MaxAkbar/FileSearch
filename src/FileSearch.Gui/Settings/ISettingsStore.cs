namespace FileSearch.Gui.Settings;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
