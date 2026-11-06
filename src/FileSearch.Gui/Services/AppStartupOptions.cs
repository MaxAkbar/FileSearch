using System;
using System.Collections.Generic;
using FileSearch.Core.Engine;

namespace FileSearch.Gui.Services;

internal sealed record AppStartupOptions(
    bool StartInBackground,
    string? StartupFolder,
    string? QueryText = null,
    SearchTarget SearchTarget = SearchTarget.Content,
    string? SelectedPath = null)
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
        string? queryText = null;
        string? selectedPath = null;
        var target = SearchTarget.Content;
        var invalidSearchPath = false;

        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var candidate = NormalizeArgument(enumerator.Current);
            if (candidate.Length == 0)
                continue;

            if (candidate is "--query" or "--path" or "--target" or "--select")
            {
                if (!enumerator.MoveNext())
                    break;

                var value = enumerator.Current;
                switch (candidate)
                {
                    case "--query":
                        queryText = value;
                        break;
                    case "--path":
                        var folder = NormalizeArgument(value);
                        invalidSearchPath = !directoryExists(folder);
                        startupFolder = invalidSearchPath ? null : folder;
                        break;
                    case "--select":
                        selectedPath = NormalizeArgument(value);
                        break;
                    case "--target":
                        target = string.Equals(value, "files", StringComparison.OrdinalIgnoreCase)
                            ? SearchTarget.FileNames
                            : SearchTarget.Content;
                        break;
                }
                continue;
            }

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

        // A removed indexed folder must not silently redirect a launcher query
        // to the last-used folder or the application's working directory.
        return new AppStartupOptions(startInBackground, startupFolder,
            invalidSearchPath ? null : queryText, target, selectedPath);
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
