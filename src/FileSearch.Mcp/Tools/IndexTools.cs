using System.ComponentModel;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using ModelContextProtocol.Server;

namespace FileSearch.Mcp.Tools;

[McpServerToolType]
internal sealed class IndexTools
{
    private readonly IIndexMaintenance _indexMaintenance;
    private readonly IExtractorRegistry _extractorRegistry;
    private readonly RootPolicy _rootPolicy;
    private readonly IEmbeddingModelPackStore? _modelPacks;
    private readonly ISemanticIndexStatusService? _semanticStatus;
    private readonly EmbeddingModelPackOptions? _modelOptions;

    public IndexTools(
        IIndexMaintenance indexMaintenance,
        IExtractorRegistry extractorRegistry,
        RootPolicy rootPolicy,
        IEmbeddingModelPackStore? modelPacks = null,
        ISemanticIndexStatusService? semanticStatus = null,
        EmbeddingModelPackOptions? modelOptions = null)
    {
        _indexMaintenance = indexMaintenance;
        _extractorRegistry = extractorRegistry;
        _rootPolicy = rootPolicy;
        _modelPacks = modelPacks;
        _semanticStatus = semanticStatus;
        _modelOptions = modelOptions;
    }

    [McpServerTool(
        Name = "index_status",
        Title = "Index and server status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Report the local search index (database health and size, indexed locations with file/line " +
        "counts and freshness, optional per-root stats) plus this server's capabilities: allowed " +
        "roots and supported file extensions. Call this first to learn what search_index can cover " +
        "and where searches are permitted. Locations outside the allowed roots are counted but not listed.")]
    public async Task<string> IndexStatusAsync(
        [Description("Optional absolute folder path: also return index stats for exactly this root.")]
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        var database = await _indexMaintenance.GetDatabaseInfoAsync(cancellationToken).ConfigureAwait(false);
        var locations = await _indexMaintenance.GetLocationsAsync(cancellationToken).ConfigureAwait(false);

        var visible = new List<IndexLocationDocument>(locations.Count);
        var hidden = 0;
        foreach (var location in locations)
        {
            if (await IsLocationVisibleAsync(location.Root, cancellationToken).ConfigureAwait(false))
            {
                // The stored profile string is an options hash; models get
                // the parsed facts that change search behavior instead.
                var hasProfile = IndexProfile.TryParse(location.Profile, out var profile);
                visible.Add(new IndexLocationDocument(
                    location.Root,
                    location.Exists,
                    location.FileCount,
                    location.LineCount,
                    location.IndexedUtc,
                    string.IsNullOrEmpty(location.LastValidationStatus) ? null : location.LastValidationStatus,
                    hasProfile ? profile.EnableOcr : null,
                    hasProfile ? profile.ExcludeDirectories.Order(StringComparer.OrdinalIgnoreCase).ToArray() : null,
                    hasProfile ? profile.ExcludeExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray() : null,
                    _semanticStatus is null ? null : await _semanticStatus.GetRootStatusAsync(location.Root, cancellationToken).ConfigureAwait(false)));
            }
            else
            {
                hidden++;
            }
        }

        IndexRootStatsDocument? rootStats = null;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var full = await _rootPolicy.EnsureAllowedAsync(root, "root", cancellationToken).ConfigureAwait(false);
            var stats = await _indexMaintenance.GetStatsAsync(full, cancellationToken).ConfigureAwait(false);
            rootStats = new IndexRootStatsDocument(
                stats.Root,
                stats.Exists,
                stats.FileCount,
                stats.LineCount,
                stats.IndexedUtc);
        }

        var allowedRoots = _rootPolicy.AllowsAnyRoot
            ? null
            : await _rootPolicy.GetAllowedRootsAsync(cancellationToken).ConfigureAwait(false);

        DocumentModelsDocument? models = null;
        if (_modelPacks is not null && _modelOptions is not null)
        {
            var options = _modelOptions.SettingsFilePath is { Length: > 0 } path ? EmbeddingModelSettings.Load(path) : _modelOptions;
            var installed = await _modelPacks.GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
            models = new DocumentModelsDocument(options.IsEnabled, options.SelectedModelPackId,
                installed.Select(pack => new DocumentModelDocument(pack.Manifest.Id, pack.Manifest.DisplayName,
                    pack.Manifest.Dimension, pack.Manifest.ToModelInfo().ModelVersion, pack.Manifest.QuantizationVersion,
                    pack.IsUsable, pack.Status)).ToArray());
        }
        return McpJson.Serialize(new IndexStatusDocument(
            new IndexDatabaseDocument(
                database.DatabasePath,
                database.Exists,
                database.IsCompatible,
                database.SchemaVersion,
                database.TotalBytes,
                database.LocationCount,
                database.TotalFileCount,
                database.TotalLineCount,
                database.PendingChangeCount,
                database.FailedFileCount,
                database.LastIndexedUtc),
            visible,
            hidden,
            rootStats,
            new ServerInfoDocument(
                ServerVersion,
                ReadOnly: true,
                _rootPolicy.AllowsAnyRoot,
                allowedRoots,
                _extractorRegistry.SupportedExtensions.Order(StringComparer.Ordinal).ToArray()), models));
    }

    [McpServerTool(
        Name = "index_failures",
        Title = "List index extraction failures",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "List files that failed text extraction while being indexed, with the extractor, error " +
        "message, and attempt count for each. Useful for explaining why an indexed search misses a " +
        "file. Failures outside the allowed roots are excluded.")]
    public async Task<string> IndexFailuresAsync(
        [Description("Optional absolute folder path: only failures under this root.")]
        string? root = null,
        [Description("Maximum failures to return, 1-500. Default 50.")]
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        string? filterRoot = null;
        if (!string.IsNullOrWhiteSpace(root))
            filterRoot = await _rootPolicy.EnsureAllowedAsync(root, "root", cancellationToken).ConfigureAwait(false);

        var failures = await _indexMaintenance.GetFailedFilesAsync(cancellationToken).ConfigureAwait(false);
        var matching = new List<IndexFailureDocument>();
        foreach (var failure in failures)
        {
            if (!await IsLocationVisibleAsync(failure.Root, cancellationToken).ConfigureAwait(false))
                continue;
            if (filterRoot is not null && !IsUnderFilter(failure.Root, filterRoot))
                continue;

            matching.Add(new IndexFailureDocument(
                failure.Root,
                failure.Path,
                failure.MemberPath,
                failure.FailureKind,
                failure.IssueCode,
                failure.Severity,
                failure.ExtractorId,
                failure.ExtractorVersion,
                failure.ExtractionAttemptCount,
                failure.RetryCount,
                failure.LastAttemptUtc,
                failure.Error));
        }

        var cap = ResultShaper.ClampMaxResults(maxResults);
        var returned = matching.Count > cap ? matching.GetRange(0, cap) : matching;
        return McpJson.Serialize(new IndexFailuresDocument(matching.Count, returned.Count, returned));
    }

    private static string ServerVersion =>
        typeof(IndexTools).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private async ValueTask<bool> IsLocationVisibleAsync(string root, CancellationToken cancellationToken)
    {
        string canonical;
        try
        {
            canonical = McpServerRails.CanonicalizeRoot(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return await _rootPolicy.IsAllowedAsync(canonical, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUnderFilter(string root, string filterRoot)
    {
        string canonical;
        try
        {
            canonical = McpServerRails.CanonicalizeRoot(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return RootPolicy.IsWithin(canonical, filterRoot);
    }
}
