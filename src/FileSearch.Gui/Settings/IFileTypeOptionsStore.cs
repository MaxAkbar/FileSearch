namespace FileSearch.Gui.Settings;

public interface IFileTypeOptionsStore
{
    FileTypeOptions Load();

    void Save(FileTypeOptions options);
}
