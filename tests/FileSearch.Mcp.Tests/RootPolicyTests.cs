using FileSearch.Core.Indexing;
using FileSearch.Mcp;
using ModelContextProtocol;

namespace FileSearch.Mcp.Tests;

public sealed class RootPolicyTests
{
    [Theory]
    [InlineData(@"C:\Allowed", @"C:\Allowed", true)]
    [InlineData(@"C:\Allowed\child\file.txt", @"C:\Allowed", true)]
    [InlineData(@"c:\allowed\CHILD", @"C:\Allowed", true)]
    [InlineData(@"C:\AllowedButLonger", @"C:\Allowed", false)]
    [InlineData(@"C:\Other\file.txt", @"C:\Allowed", false)]
    public void IsWithin_ComparesPathSegments(string path, string root, bool expected)
    {
        Assert.Equal(expected, RootPolicy.IsWithin(path, root));
    }

    [Fact]
    public async Task EnsureAllowed_AcceptsPathsUnderExplicitRoots()
    {
        var policy = CreatePolicy(explicitRoots: [@"C:\Allowed"]);

        var result = await policy.EnsureAllowedAsync(@"C:\Allowed\sub\notes.txt", "path", TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Allowed\sub\notes.txt", result);
    }

    [Fact]
    public async Task EnsureAllowed_NormalizesTraversalSegments()
    {
        var policy = CreatePolicy(explicitRoots: [@"C:\Allowed"]);

        await Assert.ThrowsAsync<McpException>(() =>
            policy.EnsureAllowedAsync(@"C:\Allowed\..\Secret\notes.txt", "path", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnsureAllowed_RejectsRelativePaths()
    {
        var policy = CreatePolicy(explicitRoots: [@"C:\Allowed"]);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            policy.EnsureAllowedAsync(@"relative\path.txt", "path", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAllowed_RejectsPathsOutsideRoots_AndNamesThem()
    {
        var policy = CreatePolicy(explicitRoots: [@"C:\Allowed"]);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            policy.EnsureAllowedAsync(@"C:\Windows\System32\config", "path", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(@"C:\Allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureAllowed_AllowAnyRoot_BypassesAllowList()
    {
        var policy = CreatePolicy(explicitRoots: [], allowAnyRoot: true);

        var result = await policy.EnsureAllowedAsync(@"C:\Anywhere\at\all.txt", "path", TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Anywhere\at\all.txt", result);
    }

    [Fact]
    public async Task DefaultAllowList_IsProfilePlusIndexedLocations()
    {
        var policy = CreatePolicy(
            explicitRoots: [],
            indexedRoots: [@"D:\Data\Docs"]);

        var allowed = await policy.GetAllowedRootsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            McpServerRails.CanonicalizeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            allowed);
        Assert.Contains(@"D:\Data\Docs", allowed);
    }

    [Fact]
    public async Task DefaultAllowList_SurvivesIndexReadFailure()
    {
        var policy = CreatePolicy(explicitRoots: [], throwOnGetLocations: true);

        var allowed = await policy.GetAllowedRootsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            McpServerRails.CanonicalizeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            allowed);
    }

    [Fact]
    public async Task ExplicitRoots_SkipIndexedLocations()
    {
        var policy = CreatePolicy(
            explicitRoots: [@"C:\Only"],
            indexedRoots: [@"D:\Data\Docs"]);

        var allowed = await policy.GetAllowedRootsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([@"C:\Only"], allowed);
    }

    private static RootPolicy CreatePolicy(
        IReadOnlyList<string>? explicitRoots = null,
        bool allowAnyRoot = false,
        IReadOnlyList<string>? indexedRoots = null,
        bool throwOnGetLocations = false)
    {
        var rails = new McpServerRails
        {
            ExplicitRoots = explicitRoots ?? [],
            AllowAnyRoot = allowAnyRoot,
        };
        return new RootPolicy(rails, new FakeIndexMaintenance(indexedRoots ?? [], throwOnGetLocations));
    }

    private sealed class FakeIndexMaintenance : IIndexMaintenance
    {
        private readonly IReadOnlyList<string> _roots;
        private readonly bool _throwOnGetLocations;

        public FakeIndexMaintenance(IReadOnlyList<string> roots, bool throwOnGetLocations)
        {
            _roots = roots;
            _throwOnGetLocations = throwOnGetLocations;
        }

        public string DatabasePath => @"C:\fake\filesearch.db";

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken)
        {
            if (_throwOnGetLocations)
                throw new IOException("database unavailable");

            IReadOnlyList<IndexedLocationInfo> locations = _roots
                .Select(root => new IndexedLocationInfo(root, 0, 0, null, "v1", Exists: true))
                .ToArray();
            return Task.FromResult(locations);
        }

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new IndexDatabaseInfo(
                DatabasePath, false, false, "0", 0, 0, 0, 0, 0, 0, 0, null));

        public Task CompactAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
