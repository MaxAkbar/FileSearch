using System;
using System.Collections.Generic;

namespace FileSearch.Gui.Services;

internal sealed record AppStartupOptions(bool StartInBackground, string? StartupFolder)
{
    public static AppStartupOptions Empty { get; } = new(false, null);

    public static AppStartupOptions Parse(
        IEnumerable<string> args,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var startInBackground = false;
        string? startupFolder = null;

        foreach (var arg in args)
        {
            var candidate = NormalizeArgument(arg);
            if (candidate.Length == 0)
                continue;

            if (IsBackgroundArgument(candidate))
            {
                startInBackground = true;
                continue;
            }

            if (IsUnknownOption(candidate))
                continue;

            if (startupFolder is null && directoryExists(candidate))
                startupFolder = candidate;
        }

        return new AppStartupOptions(startInBackground, startupFolder);
    }

    private static bool IsBackgroundArgument(string value) =>
        string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "/background", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknownOption(string value) =>
        value.StartsWith('-') ||
        value.StartsWith('/');

    private static string NormalizeArgument(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('"');
}
