using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FileSearch.Mcp.Tools;

[McpServerToolType]
internal sealed class SearchTools
{
    private const int MaxRootsPerCall = 8;

    private readonly ISearcher _searcher;
    private readonly IIndexSearch _indexSearch;
    private readonly IIndexMaintenance _indexMaintenance;
    private readonly IQueryFactory _queryFactory;
    private readonly RootPolicy _rootPolicy;

    public SearchTools(
        ISearcher searcher,
        IIndexSearch indexSearch,
        IIndexMaintenance indexMaintenance,
        IQueryFactory queryFactory,
        RootPolicy rootPolicy)
    {
        _searcher = searcher;
        _indexSearch = indexSearch;
        _indexMaintenance = indexMaintenance;
        _queryFactory = queryFactory;
        _rootPolicy = rootPolicy;
    }

    [McpServerTool(
        Name = "search_content",
        Title = "Search files (live scan)",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Live full-text search over files under the given folders. Works on any allowed folder, " +
        "indexed or not, but re-reads files on every call, so prefer search_index for folders that " +
        "index_status lists as indexed. Supports plain, regex, boolean (AND/OR/NOT, parentheses), " +
        "and unified query modes, and can match file or folder names instead of contents. Returns " +
        "matching lines with path, 1-based line number, and score.")]
    public async Task<string> SearchContentAsync(
        [Description("The search query. Interpreted per 'mode' (default: literal substring).")]
        string query,
        [Description("Absolute folder paths to search (1-8). Must lie under the server's allowed roots.")]
        string[] roots,
        [Description("Query mode: plain (default), regex, boolean, or unified.")]
        string? mode = null,
        [Description("Case-sensitive matching. Default false.")]
        bool caseSensitive = false,
        [Description("What to match: content (default), files, folders, or names.")]
        string? target = null,
        [Description("Only include files whose relative path matches one of these globs, e.g. **/*.cs.")]
        string[]? includeGlobs = null,
        [Description("Exclude files whose relative path matches one of these globs.")]
        string[]? excludeGlobs = null,
        [Description("Only include these file extensions, e.g. [\".cs\", \".md\"].")]
        string[]? includeExtensions = null,
        [Description("Exclude these file extensions.")]
        string[]? excludeExtensions = null,
        [Description("Include hidden files and folders. Default false.")]
        bool includeHidden = false,
        [Description("Only files modified at or after this ISO 8601 UTC time, e.g. 2026-06-09.")]
        string? modifiedAfter = null,
        [Description("Only files modified at or before this ISO 8601 UTC time.")]
        string? modifiedBefore = null,
        [Description("Maximum hits to return, 1-500. Default 50.")]
        int? maxResults = null,
        [Description("Maximum hits per file, 1-100. Default unlimited.")]
        int? maxResultsPerFile = null,
        [Description("Search deadline in seconds, 5-120. Default 30; partial results are returned on timeout.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var parsedMode = ToolArguments.ParseMode(mode);
        var parsedTarget = ToolArguments.ParseTarget(target);
        var expression = ToolArguments.BuildQuery(_queryFactory, query, parsedMode, caseSensitive);
        var allowedRoots = await ValidateSearchRootsAsync(roots, cancellationToken).ConfigureAwait(false);
        var walkerOptions = ToolArguments.BuildWalkerOptions(
            includeGlobs, excludeGlobs, includeExtensions, excludeExtensions,
            includeHidden, modifiedAfter, modifiedBefore,
            excludeImageFiles: true);

        var status = new ConcurrentQueue<string>();
        var request = new SearchRequest(
            expression,
            allowedRoots,
            walkerOptions,
            Progress: null,
            UseIndex: false,
            Status: status.Enqueue,
            RawQuery: query,
            Mode: parsedMode,
            SearchTarget: parsedTarget);

        var stopwatch = Stopwatch.StartNew();
        var collected = await ResultShaper.CollectAsync(
            _searcher.SearchAsync(request, cancellationToken),
            ResultShaper.ClampMaxResults(maxResults),
            ResultShaper.ClampMaxResultsPerFile(maxResultsPerFile),
            ResultShaper.ClampTimeout(timeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return McpJson.Serialize(BuildDocument(
            query, parsedMode, parsedTarget, "live", allowedRoots, collected,
            stopwatch.Elapsed, status, coverage: null));
    }

    [McpServerTool(
        Name = "search_index",
        Title = "Search indexed folders",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Content search over folders that FileSearch has already indexed — much faster than " +
        "search_content on large trees. Omit roots to search every indexed location. Roots whose " +
        "index is missing or stale are reported in 'coverage' and NOT searched; use search_content " +
        "for those. Only content queries are supported here (not file/folder name targets). Call " +
        "index_status to see which folders are indexed.")]
    public async Task<string> SearchIndexAsync(
        [Description("The search query. Interpreted per 'mode' (default: literal substring).")]
        string query,
        [Description("Absolute indexed folder paths to search (1-8). Omit to search all indexed locations.")]
        string[]? roots = null,
        [Description("Query mode: plain (default), regex, boolean, or unified.")]
        string? mode = null,
        [Description("Case-sensitive matching. Default false.")]
        bool caseSensitive = false,
        [Description("Only include these file extensions, e.g. [\".cs\", \".md\"].")]
        string[]? includeExtensions = null,
        [Description("Exclude these file extensions.")]
        string[]? excludeExtensions = null,
        [Description("Only files modified at or after this ISO 8601 UTC time.")]
        string? modifiedAfter = null,
        [Description("Only files modified at or before this ISO 8601 UTC time.")]
        string? modifiedBefore = null,
        [Description("Maximum hits to return, 1-500. Default 50.")]
        int? maxResults = null,
        [Description("Maximum hits per file, 1-100. Default unlimited.")]
        int? maxResultsPerFile = null,
        [Description("Search deadline in seconds, 5-120. Default 30; partial results are returned on timeout.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var parsedMode = ToolArguments.ParseMode(mode);
        var expression = ToolArguments.BuildQuery(_queryFactory, query, parsedMode, caseSensitive);
        var searchRoots = roots is { Length: > 0 }
            ? await ValidateSearchRootsAsync(roots, cancellationToken).ConfigureAwait(false)
            : await ResolveIndexedRootsAsync(cancellationToken).ConfigureAwait(false);
        var walkerOptions = ToolArguments.BuildWalkerOptions(
            includeGlobs: null, excludeGlobs: null, includeExtensions, excludeExtensions,
            includeHidden: false, modifiedAfter, modifiedBefore,
            excludeImageFiles: false);

        var status = new ConcurrentQueue<string>();
        var request = new SearchRequest(
            expression,
            searchRoots,
            walkerOptions,
            Progress: null,
            UseIndex: true,
            Status: status.Enqueue,
            RawQuery: query,
            Mode: parsedMode,
            SearchTarget: SearchTarget.Content);

        var profiles = await LoadIndexProfilesAsync(cancellationToken).ConfigureAwait(false);
        var coverage = new List<IndexCoverageDocument>(searchRoots.Count);
        var coveredRequests = new List<SearchRequest>(searchRoots.Count);
        foreach (var root in searchRoots)
        {
            var rootRequest = request with { Roots = new[] { root } };
            if (profiles.TryGetValue(root, out var profile))
            {
                rootRequest = rootRequest with
                {
                    WalkerOptions = ToolArguments.MergeBuildTimeExcludes(rootRequest.WalkerOptions, profile),
                };
            }

            var rootCoverage = await _indexSearch.GetCoverageAsync(rootRequest, cancellationToken).ConfigureAwait(false);
            coverage.Add(new IndexCoverageDocument(
                root,
                rootCoverage.IsCovered,
                rootCoverage.Status.ToString(),
                rootCoverage.Message));
            if (rootCoverage.IsCovered)
                coveredRequests.Add(rootRequest);
        }

        var stopwatch = Stopwatch.StartNew();
        var collected = await ResultShaper.CollectAsync(
            SearchCoveredRootsAsync(coveredRequests, cancellationToken),
            ResultShaper.ClampMaxResults(maxResults),
            ResultShaper.ClampMaxResultsPerFile(maxResultsPerFile),
            ResultShaper.ClampTimeout(timeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (coveredRequests.Count < searchRoots.Count)
            status.Enqueue("Some roots are not covered by the index; see 'coverage'. Use search_content for those.");

        return McpJson.Serialize(BuildDocument(
            query, parsedMode, SearchTarget.Content, "indexed", searchRoots, collected,
            stopwatch.Elapsed, status, coverage));
    }

    private async IAsyncEnumerable<Hit> SearchCoveredRootsAsync(
        IReadOnlyList<SearchRequest> coveredRequests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var rootRequest in coveredRequests)
        {
            await foreach (var hit in _indexSearch.SearchAsync(rootRequest, cancellationToken).ConfigureAwait(false))
                yield return hit;
        }
    }

    private async Task<Dictionary<string, IndexProfile>> LoadIndexProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = new Dictionary<string, IndexProfile>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IndexedLocationInfo> locations;
        try
        {
            locations = await _indexMaintenance.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No profiles just means no build-time-exclusion merging; the
            // coverage check still reports each root honestly.
            return profiles;
        }

        foreach (var location in locations)
        {
            if (!IndexProfile.TryParse(location.Profile, out var profile))
                continue;

            try
            {
                profiles[McpServerRails.CanonicalizeRoot(location.Root)] = profile;
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // Malformed stored root; skip.
            }
        }

        return profiles;
    }

    private async Task<IReadOnlyList<string>> ValidateSearchRootsAsync(
        string[] roots,
        CancellationToken cancellationToken)
    {
        if (roots is not { Length: > 0 })
            throw new McpException("roots must contain at least one absolute folder path.");
        if (roots.Length > MaxRootsPerCall)
            throw new McpException($"roots accepts at most {MaxRootsPerCall} folders per call.");

        var validated = new List<string>(roots.Length);
        foreach (var root in roots)
        {
            var full = await _rootPolicy.EnsureAllowedAsync(root, "roots", cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(full))
                throw new McpException($"Folder does not exist: {full}");
            validated.Add(full);
        }

        return validated;
    }

    private async Task<IReadOnlyList<string>> ResolveIndexedRootsAsync(CancellationToken cancellationToken)
    {
        var locations = await _indexMaintenance.GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        var roots = new List<string>(locations.Count);
        foreach (var location in locations)
        {
            if (!location.Exists)
                continue;

            string canonical;
            try
            {
                canonical = McpServerRails.CanonicalizeRoot(location.Root);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (await _rootPolicy.IsAllowedAsync(canonical, cancellationToken).ConfigureAwait(false))
                roots.Add(canonical);
        }

        if (roots.Count == 0)
        {
            throw new McpException(
                "No indexed locations are available within the allowed roots. " +
                "Call index_status to inspect the index, or use search_content with explicit roots.");
        }

        return roots;
    }

    private static SearchResultDocument BuildDocument(
        string query,
        QueryMode mode,
        SearchTarget target,
        string route,
        IReadOnlyList<string> roots,
        CollectedHits collected,
        TimeSpan elapsed,
        ConcurrentQueue<string> status,
        IReadOnlyList<IndexCoverageDocument>? coverage)
    {
        return new SearchResultDocument(
            query,
            mode.ToString(),
            target.ToString(),
            route,
            roots,
            collected.TotalMatches,
            collected.Hits.Count,
            collected.Truncated,
            collected.TimedOut,
            Math.Round(elapsed.TotalSeconds, 3),
            status.Distinct().ToArray(),
            collected.Hits.Select(ResultShaper.ToDocument).ToArray(),
            coverage);
    }
}
