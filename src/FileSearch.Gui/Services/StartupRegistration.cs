using System;

namespace FileSearch.Gui.Services;

public static class StartupRegistration
{
    public const string ValueName = "FileSearch";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    public static string BuildBackgroundStartupCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path is required.", nameof(executablePath));

        return $"{Quote(executablePath)} {BackgroundArgument}";
    }

    public static bool IsExpectedBackgroundStartupCommand(string? command, string executablePath) =>
        !string.IsNullOrWhiteSpace(command) &&
        string.Equals(
            command.Trim(),
            BuildBackgroundStartupCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
