using System;
using System.Collections.Generic;
using System.Linq;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

internal sealed record IndexProfile(
    bool Recursive,
    bool IncludeHidden,
    IReadOnlySet<string> IncludeExtensions,
    IReadOnlySet<string> ExcludeExtensions,
    IReadOnlySet<string> IncludeDirectories,
    IReadOnlySet<string> ExcludeDirectories)
{
    public const string Prefix = "v1";

    public static IndexProfile FromWalkerOptions(WalkerOptions options) =>
        new(
            options.Recursive,
            options.IncludeHidden,
            Normalize(options.IncludeExtensions),
            Normalize(options.ExcludeExtensions),
            NormalizeNames(options.IncludeDirectories),
            NormalizeNames(options.ExcludeDirectories));

    public string ToStorageString()
    {
        var include = string.Join(",", IncludeExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var exclude = string.Join(",", ExcludeExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var includeDirs = string.Join(",", IncludeDirectories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var excludeDirs = string.Join(",", ExcludeDirectories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"{Prefix}|recursive={(Recursive ? 1 : 0)}|hidden={(IncludeHidden ? 1 : 0)}|include={include}|exclude={exclude}|includeDirs={includeDirs}|excludeDirs={excludeDirs}";
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

        var requestedDirIncludes = NormalizeNames(request.IncludeDirectories);
        if (IncludeDirectories.Count > 0)
        {
            if (requestedDirIncludes.Count == 0)
                return false;

            foreach (var directory in requestedDirIncludes)
                if (!IncludeDirectories.Contains(directory))
                    return false;
        }

        // Files under directories excluded at build time are absent from the
        // index, so the search must exclude at least those directories too.
        // (The reverse — searching with extra excludes — is fine; they're
        // re-applied per query by IndexedFileFilter.)
        var requestedDirExcludes = NormalizeNames(request.ExcludeDirectories);
        foreach (var directory in ExcludeDirectories)
            if (!requestedDirExcludes.Contains(directory))
                return false;

        return true;
    }

    public static bool TryParse(string raw, out IndexProfile profile)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        profile = new IndexProfile(false, false, empty, empty, empty, empty);

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length < 5 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        bool recursive = false;
        bool hidden = false;
        IReadOnlySet<string> include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> includeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                case "includeDirs":
                    includeDirs = ParseNames(split[1]);
                    break;
                case "excludeDirs":
                    // Missing key (older profile) means the build excluded
                    // nothing, which the empty default already expresses.
                    excludeDirs = ParseNames(split[1]);
                    break;
            }
        }

        profile = new IndexProfile(recursive, hidden, include, exclude, includeDirs, excludeDirs);
        return true;
    }

    private static HashSet<string> Normalize(IEnumerable<string> extensions) =>
        extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Select(extension => extension.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> NormalizeNames(IEnumerable<string> names) =>
        names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ParseExtensions(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Normalize(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static HashSet<string> ParseNames(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : NormalizeNames(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
