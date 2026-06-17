using System;
using Microsoft.Win32;

namespace FileSearch.Gui.Services;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    public bool IsBackgroundStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath, writable: false);
            var command = key?.GetValue(StartupRegistration.ValueName) as string;
            return StartupRegistration.IsExpectedBackgroundStartupCommand(command, GetExecutablePath());
        }
        catch
        {
            return false;
        }
    }

    public void EnableBackgroundStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistration.RunKeyPath);
        if (key is null)
            throw new InvalidOperationException($"Could not create registry key: {StartupRegistration.RunKeyPath}");

        key.SetValue(
            StartupRegistration.ValueName,
            StartupRegistration.BuildBackgroundStartupCommand(GetExecutablePath()),
            RegistryValueKind.String);
    }

    public void DisableBackgroundStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath, writable: true);
        key?.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Could not resolve the FileSearch executable path.");

        return executablePath;
    }
}
