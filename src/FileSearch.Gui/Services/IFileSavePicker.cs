namespace FileSearch.Gui.Services;

public interface IFileSavePicker
{
    string? PickSaveFile(string title, string filter, string defaultFileName);
}
