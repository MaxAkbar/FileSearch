namespace FileSearch.Gui.Services;

public interface IFileOpenPicker
{
    string? PickOpenFile(string title, string filter);
}
