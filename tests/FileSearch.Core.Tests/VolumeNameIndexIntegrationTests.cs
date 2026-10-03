using System.Diagnostics;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

/// <summary>
/// Runs the drive name index against a real NTFS folder: the directory-walk
/// scanner builds a table for a temp tree, the service treats that folder as
/// a volume, and results are compared with the live searcher.
/// </summary>
public sealed class VolumeNameIndexIntegrationTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "filesearch-volume-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "root");
    private VolumeNameIndexService? _service;
    private VolumeIndexState? _state;
    private Searcher? _live;
    private bool _available;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows())
            return;

        var resolver = new WindowsIndexVolumeResolver();
        if (!resolver.TryResolveVolume(Root, out var volume, out _) || !volume.UsnSupported || volume.IsRemote)
            return;

        var journal = new WindowsUsnJournalReader();
        UsnJournalSnapshot checkpoint;
        try
        {
            checkpoint = await journal.QueryAsync(volume, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        CreateTree();
        var table = DirectoryWalkScanner.Scan(Root, progress: null, CancellationToken.None);
        _service = new VolumeNameIndexService(
            resolver,
            journal,
            new WindowsVolumeScanner(),
            new WindowsVolumeFileIdResolverFactory(),
            launcher: null,
            new VolumeNameIndexOptions { SnapshotDirectory = Path.Combine(_base, "snapshots") });
        _state = _service.AttachForTesting(Root, table, volume, checkpoint.JournalId, checkpoint.NextUsn);
        var plain = new PlainTextExtractor();
        _live = new Searcher(new FileWalker(), new ExtractorRegistry(new ITextExtractor[] { plain }, plain));
        _available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync();

        if (Directory.Exists(Root))
        {
            foreach (var link in Directory.GetDirectories(Root, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link);
            }
        }

        try
        {
            ClearAttributes(_base);
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("report", SearchTarget.FileNames, false, true)]
    [InlineData("report", SearchTarget.FileNames, true, true)]
    [InlineData("report", SearchTarget.FileNames, false, false)]
    [InlineData("docs", SearchTarget.FolderNames, false, true)]
    [InlineData("docs", SearchTarget.FolderNames, true, true)]
    [InlineData("o", SearchTarget.FileAndFolderNames, false, true)]
    [InlineData(@"docs\2026", SearchTarget.FolderNames, false, true)]
    [InlineData("e", SearchTarget.FileAndFolderNames, true, false)]
    public async Task MatchesTheLiveNameSearch(string text, SearchTarget target, bool includeHidden, bool recursive)
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");
        var options = new WalkerOptions { IncludeHidden = includeHidden, Recursive = recursive };

        await AssertParityAsync(new TermQuery(text), target, options);
    }

    [Fact]
    public async Task MatchesLiveSearchForRegexBooleanAndFileFilters()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");

        await AssertParityAsync(new RegexQuery(@"^report.*\.(md|txt)$"), SearchTarget.FileNames, new WalkerOptions());
        await AssertParityAsync(new QueryParser(false).Parse("report AND NOT draft"), SearchTarget.FileNames, new WalkerOptions());
        await AssertParityAsync(new TermQuery("report"), SearchTarget.FileNames, new WalkerOptions
        {
            IncludeGlobs = ["*.md"],
            ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        });
        await AssertParityAsync(new TermQuery("report"), SearchTarget.FileNames, new WalkerOptions
        {
            MinFileSizeBytes = 100,
            IncludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs" },
        });
        await AssertParityAsync(new TermQuery("e"), SearchTarget.FileNames, new WalkerOptions
        {
            ExcludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
        });
    }

    [Fact]
    public async Task ScopedRootsAndRelativeFolderMatchesAgree()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");

        var docs = Path.Combine(Root, "docs");
        await AssertParityAsync(new TermQuery("2026"), SearchTarget.FolderNames, new WalkerOptions(), docs);
        await AssertParityAsync(new TermQuery("report"), SearchTarget.FileAndFolderNames, new WalkerOptions(), docs);
    }

    [Fact]
    public async Task CatchesUpCreatesRenamesAndDeletesFromTheJournal()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");

        var created = Path.Combine(Root, "docs", "fresh-journal-entry.txt");
        var newFolder = Path.Combine(Root, "new-folder");
        File.WriteAllText(created, "x");
        Directory.CreateDirectory(newFolder);
        File.WriteAllText(Path.Combine(newFolder, "nested-journal.txt"), "x");
        File.Move(Path.Combine(Root, "docs", "report-old.txt"), Path.Combine(Root, "docs", "report-renamed.txt"));
        File.Delete(Path.Combine(Root, "notes.txt"));

        var hits = await SearchViaIndexAsync(new TermQuery("journal"), SearchTarget.FileNames, new WalkerOptions(), Root);
        Assert.Equal(
            new[] { created, Path.Combine(newFolder, "nested-journal.txt") }.Order(StringComparer.Ordinal),
            hits.Select(hit => hit.Path).Order(StringComparer.Ordinal));
        Assert.All(hits, hit => Assert.Equal(HitRoute.Indexed, hit.Route));

        await AssertParityAsync(new TermQuery("report"), SearchTarget.FileNames, new WalkerOptions());
        await AssertParityAsync(new TermQuery("notes"), SearchTarget.FileNames, new WalkerOptions());
    }

    [Fact]
    public async Task FallsBackToTheLiveWalkWhenAFileSearchCanReachAJunction()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");

        var target = Path.Combine(_base, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "report-through-link.txt"), "x");
        var link = Path.Combine(Root, "linked");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.SkipUnless(process.ExitCode == 0, "Could not create a junction.");
        }

        var statuses = new List<string>();
        var request = new SearchRequest(new TermQuery("report"), [Root], new WalkerOptions(),
            UseIndex: true, Status: statuses.Add, SearchTarget: SearchTarget.FileNames);
        var hits = await CollectAsync(new VolumeNameSearcher(_live!, _service!), request);

        Assert.Contains(hits, hit => hit.Path == Path.Combine(link, "report-through-link.txt"));
        Assert.Contains(statuses, status => status.Contains("folder links", StringComparison.Ordinal));

        // Folder searches never enter reparse points, so they stay on the index.
        var folderHits = await SearchViaIndexAsync(new TermQuery("linked"), SearchTarget.FolderNames, new WalkerOptions(), Root);
        Assert.Empty(folderHits);
    }

    [Fact]
    public async Task TermSearchRanksResultsAndReportsUncoveredRoots()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");

        var result = await _service!.SearchAsync(
            new VolumeTermSearchRequest("docs report", ScopeRoots: [Root, @"Z:\does-not-exist"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(Root, "docs", "report.md"), result.Matches[0].Path);
        Assert.Contains(@"Z:\does-not-exist", result.UncoveredRoots);
        Assert.DoesNotContain(result.Matches, match => match.Path.Contains("hidden-dir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DelegatesWhenTheIndexIsNotRequested()
    {
        Assert.SkipUnless(_available, "Requires a local NTFS volume with a change journal.");
        var request = new SearchRequest(new TermQuery("report"), [Root], new WalkerOptions(),
            UseIndex: false, SearchTarget: SearchTarget.FileNames);

        var hits = await CollectAsync(new VolumeNameSearcher(_live!, _service!), request);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal(HitRoute.Live, hit.Route));
    }

    private void CreateTree()
    {
        Write("report.md", 10);
        Write("notes.txt", 10);
        Write(Path.Combine("docs", "report.md"), 500);
        Write(Path.Combine("docs", "report-old.txt"), 10);
        Write(Path.Combine("docs", "report-draft.md"), 10);
        Write(Path.Combine("docs", "2026", "q1-report.txt"), 200);
        Write(Path.Combine("docs", "2026", "summary.md"), 10);
        Write(Path.Combine("docs", "archive-docs", "old-report.md"), 10);
        Write(Path.Combine("bin", "report.dll"), 10);
        Write(Path.Combine("src", "node_modules", "report", "index.js"), 10);
        Write(Path.Combine("hidden-dir", "report-hidden.txt"), 10);
        Write(Path.Combine("hidden-dir", "docs-inside-hidden", "x.txt"), 10);
        Write("hidden-report.txt", 10);
        File.SetAttributes(Path.Combine(Root, "hidden-dir"), FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(Root, "hidden-report.txt"), FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(Root, "empty-docs"));

        void Write(string relative, int length)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('x', length));
        }
    }

    private async Task AssertParityAsync(Query query, SearchTarget target, WalkerOptions options, string? root = null)
    {
        root ??= Root;
        var live = await CollectAsync(_live!, new SearchRequest(query, [root], options, SearchTarget: target));
        var indexed = await SearchViaIndexAsync(query, target, options, root);

        Assert.All(indexed, hit => Assert.Equal(HitRoute.Indexed, hit.Route));
        Assert.Equal(Describe(live), Describe(indexed));
    }

    private async Task<List<Hit>> SearchViaIndexAsync(Query query, SearchTarget target, WalkerOptions options, string root)
    {
        var statuses = new List<string>();
        var request = new SearchRequest(query, [root], options, UseIndex: true, Status: statuses.Add, SearchTarget: target);
        var hits = await CollectAsync(new VolumeNameSearcher(new ThrowingSearcher(), _service!), request);
        Assert.Contains(statuses, status => status.StartsWith("Searching drive name index", StringComparison.Ordinal));
        return hits;
    }

    private static string[] Describe(IEnumerable<Hit> hits) =>
        hits.Select(hit => $"{hit.Path}|{hit.LineContent}|{hit.Score}|{hit.SizeBytes}|{string.Join(',', hit.Highlights.Select(span => $"{span.Start}:{span.Length}"))}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<List<Hit>> CollectAsync(ISearcher searcher, SearchRequest request)
    {
        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private static void ClearAttributes(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
        };

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                File.SetAttributes(entry, attributes & ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System));
        }
    }

    private sealed class ThrowingSearcher : ISearcher
    {
        public IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The drive name index should have answered this request.");
    }
}
