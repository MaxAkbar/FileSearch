using FileSearch.Core.Indexing;
using ModelContextProtocol;

namespace FileSearch.Mcp;

/// <summary>
/// The path allow-list every tool argument passes through. Explicit
/// <c>--root</c> arguments win; otherwise the allow-list is the user profile
/// plus every indexed location (resolved once, on first use, so a missing or
/// locked index database degrades to profile-only instead of failing).
/// Comparison is ordinal-ignore-case on canonical full paths, which matches
/// Windows filesystem semantics (this product is Windows-first).
/// </summary>
internal sealed class RootPolicy
{
    private readonly McpServerRails _rails;
    private readonly IIndexMaintenance _indexMaintenance;
    private readonly Lazy<Task<IReadOnlyList<string>>> _allowedRoots;

    public RootPolicy(McpServerRails rails, IIndexMaintenance indexMaintenance)
    {
        _rails = rails;
        _indexMaintenance = indexMaintenance;
        _allowedRoots = new Lazy<Task<IReadOnlyList<string>>>(
            ResolveAllowedRootsAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool AllowsAnyRoot => _rails.AllowAnyRoot;

    public async ValueTask<IReadOnlyList<string>> GetAllowedRootsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _allowedRoots.Value.ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a tool-supplied path and returns its canonical form, or
    /// throws an <see cref="McpException"/> whose message tells the model how
    /// to recover (absolute paths only, and only under the allowed roots).
    /// </summary>
    public async ValueTask<string> EnsureAllowedAsync(
        string path,
        string parameterName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException($"{parameterName} must be a non-empty absolute path.");
        if (!Path.IsPathFullyQualified(path))
            throw new McpException($"{parameterName} must be an absolute path; got '{path}'.");

        string full;
        try
        {
            full = McpServerRails.CanonicalizeRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new McpException($"{parameterName} is not a valid path: {ex.Message}");
        }

        if (await IsAllowedAsync(full, cancellationToken).ConfigureAwait(false))
            return full;

        var allowed = await GetAllowedRootsAsync(cancellationToken).ConfigureAwait(false);
        throw new McpException(
            $"{parameterName} '{full}' is outside the allowed roots ({string.Join("; ", allowed)}). " +
            "Ask the user to restart the server with --root <folder> (or --allow-any-root) to widen access.");
    }

    /// <summary>Non-throwing check for filtering result sets (canonical input expected).</summary>
    public async ValueTask<bool> IsAllowedAsync(string canonicalPath, CancellationToken cancellationToken)
    {
        if (_rails.AllowAnyRoot)
            return true;

        var allowed = await GetAllowedRootsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in allowed)
        {
            if (IsWithin(canonicalPath, root))
                return true;
        }

        return false;
    }

    internal static bool IsWithin(string fullPath, string root) =>
        fullPath.Length >= root.Length &&
        fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        (fullPath.Length == root.Length ||
            fullPath[root.Length] == Path.DirectorySeparatorChar ||
            fullPath[root.Length] == Path.AltDirectorySeparatorChar);

    private async Task<IReadOnlyList<string>> ResolveAllowedRootsAsync()
    {
        if (_rails.ExplicitRoots.Count > 0)
            return _rails.ExplicitRoots;

        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            roots.Add(McpServerRails.CanonicalizeRoot(profile));

        try
        {
            var locations = await _indexMaintenance.GetLocationsAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var location in locations)
            {
                try
                {
                    roots.Add(McpServerRails.CanonicalizeRoot(location.Root));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    // Skip malformed stored roots; the rest of the allow-list stands.
                }
            }
        }
        catch (Exception)
        {
            // Index database missing or unreadable: profile-only allow-list.
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
