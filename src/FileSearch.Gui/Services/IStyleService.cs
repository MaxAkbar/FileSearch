namespace FileSearch.Gui.Services;

public enum AppStyle
{
    Comfortable,
    Compact,
}

public interface IStyleService
{
    AppStyle CurrentStyle { get; }

    void SetStyle(AppStyle style);
}
