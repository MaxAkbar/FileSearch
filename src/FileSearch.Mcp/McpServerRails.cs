namespace FileSearch.Mcp;

/// <summary>
/// Startup configuration for the server's safety rails. Roots come from
/// repeated <c>--root</c> arguments, or the <c>FILESEARCH_MCP_ROOTS</c>
/// environment variable (path-separator delimited) when no arguments are
/// given. With neither, <see cref="RootPolicy"/> falls back to the user
/// profile plus all indexed locations.
/// </summary>
internal sealed record McpServerRails
{
    public const string RootsEnvironmentVariable = "FILESEARCH_MCP_ROOTS";

    /// <summary>Canonical allow-list roots; empty when using defaults.</summary>
    public IReadOnlyList<string> ExplicitRoots { get; init; } = [];

    /// <summary>Disables the root allow-list entirely (<c>--allow-any-root</c>).</summary>
    public bool AllowAnyRoot { get; init; }

    public static McpServerRails Parse(IReadOnlyList<string> args, string? rootsEnvironment)
    {
        var roots = new List<string>();
        var allowAny = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException("--root requires a folder path value.");
                roots.Add(args[++i]);
            }
            else if (string.Equals(arg, "--allow-any-root", StringComparison.OrdinalIgnoreCase))
            {
                allowAny = true;
            }
            else
            {
                throw new ArgumentException(
                    $"Unknown option: {arg}. Supported options: --root <folder> (repeatable), --allow-any-root.");
            }
        }

        if (roots.Count == 0 && !string.IsNullOrWhiteSpace(rootsEnvironment))
        {
            roots.AddRange(rootsEnvironment.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new McpServerRails
        {
            // Configured roots may be relative (".mcp.json" launches with the
            // project as the working directory); tool arguments may not.
            ExplicitRoots = roots
                .Select(CanonicalizeRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AllowAnyRoot = allowAny,
        };
    }

    internal static string CanonicalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}
