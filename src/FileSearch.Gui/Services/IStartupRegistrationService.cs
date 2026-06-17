namespace FileSearch.Gui.Services;

public interface IStartupRegistrationService
{
    bool IsBackgroundStartupEnabled();

    void EnableBackgroundStartup();

    void DisableBackgroundStartup();
}
