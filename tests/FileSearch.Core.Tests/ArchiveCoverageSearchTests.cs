using System.IO.Compression;
using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class ArchiveCoverageSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveDiagnosticDispatchKeepsContextAndBoundsArchiveWarnings(bool enableOcr)
    {
        using var files = new ArchiveFiles();
        File.WriteAllText(Path.Combine(files.Root, "diagnostic.probe"), "fixture");
        var diagnostic = new DiagnosticProbe();
        var statuses = new List<string>();
        var searcher = new Searcher(new FileWalker(), new ExtractorRegistry([diagnostic]));
        var request = new SearchRequest(new TermQuery("needle"), [files.Root],
            new WalkerOptions { EnableOcr = enableOcr }, Status: statuses.Add);

        Assert.Single(await HitsAsync(searcher.SearchAsync(request, Token)));
        Assert.Equal(enableOcr, diagnostic.SeenEnableOcr);
        Assert.Equal(5, statuses.Count);
        Assert.All(statuses, message =>
        {
            Assert.StartsWith("Partial:", message);
            Assert.InRange(message.Length, 1, 480);
        });
        Assert.Contains("additional extraction warnings", statuses[^1]);
    }

    [Fact]
    public async Task LiveAndIndexedZipSkipsDeclarePartialCoverageEvenWithoutHits()
    {
        using var files = new ArchiveFiles();
        files.CreateArchive("partial.zip", includeSkipped: true);
        var registry = new ExtractorRegistry([new ZipExtractor()]);
        var walker = new FileWalker();
        using var index = new CSharpDbFileIndex(files.IndexOptions, walker, registry);
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        var live = new Searcher(walker, registry);
        foreach (var query in new[] { "needle", "absentword" })
        {
            foreach (var indexed in new[] { false, true })
            {
                var statuses = new List<string>();
                var request = files.Request(query, statuses.Add) with { UseIndex = indexed };
                var hits = await HitsAsync(indexed ? index.SearchAsync(request, Token) : live.SearchAsync(request, Token));
                Assert.Equal(query == "needle" ? 1 : 0, hits.Count);
                Assert.Contains(statuses, message => message.StartsWith("Partial:", StringComparison.Ordinal));
                if (indexed) Assert.Single(statuses);
            }
        }
        var failure = Assert.Single(await index.GetFailedFilesAsync(Token));
        Assert.Equal("archive_member_unsupported_type", failure.IssueCode);
        Assert.Equal("unsearched.bin", failure.MemberPath);
    }

    [Fact]
    public async Task IndexedWarningRespectsScopeFileFiltersAndNameTargets()
    {
        using var files = new ArchiveFiles();
        files.CreateArchive("clean.zip", includeSkipped: false);
        files.CreateArchive("private/partial.zip", includeSkipped: true);
        files.CreateArchive("otherroot/outside.zip", includeSkipped: true, outsideRoot: true);
        var registry = new ExtractorRegistry([new ZipExtractor()]);
        using var index = new CSharpDbFileIndex(files.IndexOptions, new FileWalker(), registry);
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        await index.BuildOrRefreshAsync(new IndexRequest(files.Sandbox, files.Walk with { IncludeGlobs = ["outside.zip"] }), Token);

        SearchRequest[] excluded =
        [
            files.Request("needle") with { WalkerOptions = files.Walk with { IncludeGlobs = ["clean.zip"] } },
            files.Request("needle") with { WalkerOptions = files.Walk with { Recursive = false } },
            files.Request("needle") with { WalkerOptions = files.Walk with { ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "private" } } },
            files.Request("needle") with { WalkerOptions = files.Walk with { MaxFileSizeBytes = 1 } },
            files.Request("needle") with { WalkerOptions = files.Walk with { ModifiedAfterUtc = DateTime.UtcNow.AddYears(1) } },
            files.Request("needle") with { WalkerOptions = files.Walk with { ExcludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip" } } },
            files.Request("needle") with { Expression = new UnifiedQueryParser().Parse("name:clean needle"), Mode = QueryMode.Unified },
            files.Request("needle") with { Expression = new UnifiedQueryParser().Parse("type:pdf needle"), Mode = QueryMode.Unified },
            files.Request("needle") with { Expression = new UnifiedQueryParser().Parse("type:email from:alice"), Mode = QueryMode.Unified },
            files.Request("needle") with { Expression = new UnifiedQueryParser().Parse("name:partial"), Mode = QueryMode.Unified },
            files.Request("partial") with { SearchTarget = SearchTarget.FileNames },
        ];
        foreach (var request in excluded)
        {
            var statuses = new List<string>();
            await HitsAsync(index.SearchAsync(request with { Status = statuses.Add }, Token));
            Assert.True(statuses.All(message => !message.StartsWith("Partial:", StringComparison.Ordinal)),
                $"Unexpected archive warning for {request.Expression.GetType().Name}, globs {string.Join(';', request.WalkerOptions.IncludeGlobs)}, recursive {request.WalkerOptions.Recursive}: {string.Join(" | ", statuses)}");
            Assert.DoesNotContain(statuses, message => message.Contains("outside.zip", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WarningDisappearsAfterArchiveRefreshRemovesSkippedMembers()
    {
        using var files = new ArchiveFiles();
        files.CreateArchive("partial.zip", includeSkipped: true);
        using var index = new CSharpDbFileIndex(files.IndexOptions, new FileWalker(), new ExtractorRegistry([new ZipExtractor()]));
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        var before = new List<string>();
        await HitsAsync(index.SearchAsync(files.Request("needle", before.Add), Token));
        Assert.Single(before);
        files.CreateArchive("partial.zip", includeSkipped: false);
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        var after = new List<string>();
        Assert.Single(await HitsAsync(index.SearchAsync(files.Request("needle", after.Add), Token)));
        var failures = await index.GetFailedFilesAsync(Token);
        Assert.True(after.All(message => !message.StartsWith("Partial:", StringComparison.Ordinal)),
            $"Unexpected post-refresh warning: {string.Join(" | ", after)}; current failures: {string.Join(" | ", failures.Select(failure => failure.IssueCode + ": " + failure.Error))}");
        Assert.Empty(failures);
    }

    [Fact]
    public async Task CorruptOuterArchiveReportsPartialOnBothRoutes()
    {
        using var files = new ArchiveFiles();
        File.WriteAllText(Path.Combine(files.Root, "broken.zip"), "not a zip archive");
        var registry = new ExtractorRegistry([new ZipExtractor()]);
        var walker = new FileWalker();
        using var index = new CSharpDbFileIndex(files.IndexOptions, walker, registry);
        await index.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        var live = new Searcher(walker, registry);
        foreach (var indexed in new[] { false, true })
        {
            var statuses = new List<string>();
            var request = files.Request("needle", statuses.Add);
            Assert.Empty(await HitsAsync(indexed ? index.SearchAsync(request, Token) : live.SearchAsync(request, Token)));
            Assert.Contains(statuses, message => message.StartsWith("Partial:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ZipVersionRefreshReextractsUnchangedArchive()
    {
        using var files = new ArchiveFiles();
        string path = files.CreateArchive("unchanged.zip", includeSkipped: false);
        byte[] source = await File.ReadAllBytesAsync(path, Token);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        var walker = new FileWalker();
        var request = files.Request("needle");
        using (var old = new CSharpDbFileIndex(files.IndexOptions, walker, new ExtractorRegistry([new LegacyZipExtractor()])))
        {
            await old.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
            Assert.Empty(await HitsAsync(old.SearchAsync(request, Token)));
        }

        using var current = new CSharpDbFileIndex(files.IndexOptions, walker, new ExtractorRegistry([new ZipExtractor()]));
        var stale = await current.GetCoverageAsync(request, Token);
        Assert.Equal(IndexCoverageStatus.Incompatible, stale.Status);
        Assert.Contains("extractor versions", stale.Message, StringComparison.OrdinalIgnoreCase);
        await current.BuildOrRefreshAsync(new IndexRequest(files.Root, files.Walk), Token);
        Assert.Equal(IndexCoverageStatus.Covered, (await current.GetCoverageAsync(request, Token)).Status);
        Assert.Single(await HitsAsync(current.SearchAsync(request, Token)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(source, await File.ReadAllBytesAsync(path, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<List<Hit>> HitsAsync(IAsyncEnumerable<Hit> source)
    {
        var hits = new List<Hit>();
        await foreach (var hit in source) hits.Add(hit);
        return hits;
    }

    private sealed class ArchiveFiles : IDisposable
    {
        public string Sandbox { get; } = Path.Combine(Path.GetTempPath(), "FileSearch.ArchiveCoverage", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Sandbox, "selected");
        public FileIndexOptions IndexOptions => new() { DatabasePath = Path.Combine(Sandbox, "index.db") };
        public WalkerOptions Walk { get; } = new() { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip" } };

        public ArchiveFiles() => Directory.CreateDirectory(Root);

        public string CreateArchive(string relative, bool includeSkipped, bool outsideRoot = false)
        {
            string path = Path.Combine(outsideRoot ? Sandbox : Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var text = new StreamWriter(archive.CreateEntry("readme.txt").Open())) text.Write("needle\n");
            if (includeSkipped)
                using (var binary = new StreamWriter(archive.CreateEntry("unsearched.bin").Open())) binary.Write("opaque member");
            return path;
        }

        public SearchRequest Request(string text, Action<string>? status = null) =>
            new(new TermQuery(text), [Root], Walk, UseIndex: true, Status: status, Mode: QueryMode.PlainText);

        public void Dispose()
        {
            try { Directory.Delete(Sandbox, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class LegacyZipExtractor : ITextExtractor
    {
        public string ExtractorId => "filesearch.zip";
        public string ExtractorVersion => "4";
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".zip"];
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextLine(1, "legacy content");
            await Task.CompletedTask;
        }
    }

    private sealed class DiagnosticProbe : IContextualDiagnosticTextExtractor
    {
        public bool? SeenEnableOcr { get; private set; }
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".probe"];
        public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Contextual diagnostic dispatch is required.");
        public IAsyncEnumerable<TextLine> ExtractAsync(string path, IExtractionIssueSink issues, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Contextual diagnostic dispatch is required.");
        public IAsyncEnumerable<TextLine> ExtractAsync(string path, TextExtractionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Contextual diagnostic dispatch is required.");
        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, TextExtractionContext context, IExtractionIssueSink issues,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SeenEnableOcr = context.EnableOcr;
            for (int i = 0; i < 12; i++)
                issues.Report(new ExtractionIssue("member.bin", "archive_member_unsupported_type", new string('x', 1_000)));
            yield return new TextLine(1, "needle");
            await Task.CompletedTask;
        }
    }
}
