using System;
using System.Linq;

namespace FileSearch.Core;

/// <summary>
/// The single parser for user-entered file-extension lists such as
/// "cs; *.md, .TXT". Normalized form is a leading dot, lowercase.
/// </summary>
public static class ExtensionList
{
    private static readonly char[] s_separators = { ';', ',', ' ', '\r', '\n', '\t' };

    public static string[] Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw.Split(s_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(extension => extension.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Normalize(string value)
    {
        var extension = value.Trim();
        if (extension.StartsWith("*.", StringComparison.Ordinal))
            extension = extension[1..];
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return extension.ToLowerInvariant();
    }
}
