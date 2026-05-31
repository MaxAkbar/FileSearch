using System;
using System.Collections.Generic;

namespace FileSearch.Gui.Services;

public static class ShellIntegrationRegistration
{
    public const string MenuText = "Search with FileSearch";
    public const string FolderShellKeyPath = @"Software\Classes\Directory\shell\FileSearch";
    public const string FolderBackgroundShellKeyPath = @"Software\Classes\Directory\Background\shell\FileSearch";

    private const string FolderItemArgument = "%1";
    private const string FolderBackgroundArgument = "%V";

    public static string BuildFolderItemCommand(string executablePath) =>
        BuildCommand(executablePath, FolderItemArgument);

    public static string BuildFolderBackgroundCommand(string executablePath) =>
        BuildCommand(executablePath, FolderBackgroundArgument);

    private static string BuildCommand(string executablePath, string argument)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path is required.", nameof(executablePath));

        return $"{Quote(executablePath)} {Quote(argument)}";
    }

    public static IReadOnlyList<ShellVerbRegistration> BuildVerbRegistrations(string executablePath) =>
        new[]
        {
            new ShellVerbRegistration(
                FolderShellKeyPath,
                BuildFolderItemCommand(executablePath)),
            new ShellVerbRegistration(
                FolderBackgroundShellKeyPath,
                BuildFolderBackgroundCommand(executablePath)),
        };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

public sealed record ShellVerbRegistration(string ShellKeyPath, string Command);
