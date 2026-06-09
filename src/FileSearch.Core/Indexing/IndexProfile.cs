using System;
using System.Collections.Generic;
using System.Linq;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

internal sealed record IndexProfile(
    bool Recursive,
    bool IncludeHidden,
    IReadOnlySet<string> IncludeExtensions,
    IReadOnlySet<string> ExcludeExtensions)
{
    public const string Prefix = "v1";

    public static IndexProfile FromWalkerOptions(WalkerOptions options) =>
        new(
            options.Recursive,
            options.IncludeHidden,
            Normalize(options.IncludeExtensions),
            Normalize(options.ExcludeExtensions));

    public string ToStorageString()
    {
        var include = string.Join(",", IncludeExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var exclude = string.Join(",", ExcludeExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"{Prefix}|recursive={(Recursive ? 1 : 0)}|hidden={(IncludeHidden ? 1 : 0)}|include={include}|exclude={exclude}";
    }

    public bool Covers(WalkerOptions request)
    {
        if (!Recursive && request.Recursive)
            return false;

        if (!IncludeHidden && request.IncludeHidden)
            return false;

        var requestedIncludes = Normalize(request.IncludeExtensions);
        if (IncludeExtensions.Count > 0)
        {
            if (requestedIncludes.Count == 0)
                return false;

            foreach (var extension in requestedIncludes)
                if (!IncludeExtensions.Contains(extension))
                    return false;
        }

        var requestedExcludes = Normalize(request.ExcludeExtensions);
        foreach (var extension in ExcludeExtensions)
            if (!requestedExcludes.Contains(extension))
                return false;

        return true;
    }

    public static bool TryParse(string raw, out IndexProfile profile)
    {
        profile = new IndexProfile(false, false, new HashSet<string>(), new HashSet<string>());

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length < 5 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        bool recursive = false;
        bool hidden = false;
        IReadOnlySet<string> include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts.Skip(1))
        {
            var split = part.Split('=', 2);
            if (split.Length != 2)
                continue;

            switch (split[0])
            {
                case "recursive":
                    recursive = split[1] == "1";
                    break;
                case "hidden":
                    hidden = split[1] == "1";
                    break;
                case "include":
                    include = ParseExtensions(split[1]);
                    break;
                case "exclude":
                    exclude = ParseExtensions(split[1]);
                    break;
            }
        }

        profile = new IndexProfile(recursive, hidden, include, exclude);
        return true;
    }

    private static IReadOnlySet<string> Normalize(IEnumerable<string> extensions) =>
        extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension)
            .Select(extension => extension.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ParseExtensions(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Normalize(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
