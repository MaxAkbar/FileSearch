using System;
using System.Collections.Generic;

namespace FileSearch.Gui.Services;

public static class StartupFolderResolver
{
    public static string? ResolveFolderPath(
        IEnumerable<string> args,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(directoryExists);

        foreach (var arg in args)
        {
            var candidate = NormalizeArgument(arg);
            if (candidate.Length == 0)
                continue;

            if (directoryExists(candidate))
                return candidate;
        }

        return null;
    }

    private static string NormalizeArgument(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('"');
}
