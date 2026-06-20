using System.Diagnostics;
using System.IO;
using CSharpDB.Engine;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class FileIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly IExtractorRegistry _registry;
    private readonly Searcher _liveSearcher;
    private readonly CSharpDbFileIndex _index;
    private readonly IndexedSearcher _indexedSearcher;

    public FileIndexTests()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "filesearch-index-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(basePath, "root");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(basePath, "index", "filesearch.db");

        var plain = new PlainTextExtractor();
        _registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        var walker = new FileWalker();
        _liveSearcher = new Searcher(walker, _registry);
        _index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = _dbPath }, walker, _registry);
        _indexedSearcher = new IndexedSearcher(_liveSearcher, _index, new IndexCoverageService(_index));
    }

    public void Dispose()
    {
        try
        {
            var basePath = Directory.GetParent(_root)?.FullName;
            if (basePath is not null)
                Directory.Delete(basePath, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task IndexedSearchMatchesLiveSearch_ForPlainRegexAndBooleanQueries()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\nbeta match\nfoo and bar\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "beta match\nbar only\n");

        await BuildAsync();

        await AssertSameResultsAsync(new TermQuery("beta"));
        await AssertSameResultsAsync(new RegexQuery("b.t.\\s+match"));
        await AssertSameResultsAsync(new AndQuery(new Query[] { new TermQuery("foo"), new TermQuery("bar") }));
        await AssertSameResultsAsync(new AndQuery(new Query[]
        {
            new OrQuery(new Query[] { new TermQuery("foo"), new TermQuery("beta") }),
            new TermQuery("match"),
        }));
    }

    [Fact]
    public async Task RefreshUpdatesChangedFilesAndRemovesDeletedFiles()
    {
        var changed = Path.Combine(_root, "changed.txt");
        var deleted = Path.Combine(_root, "deleted.txt");
        File.WriteAllText(changed, "old needle\n");
        File.WriteAllText(deleted, "delete needle\n");

        await BuildAsync();

        File.WriteAllText(changed, "new needle\n");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(2));
        File.Delete(deleted);

        await BuildAsync();

        Assert.Empty(await IndexedSearchAsync(new TermQuery("old")));
        Assert.Empty(await IndexedSearchAsync(new TermQuery("delete")));

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("new")));
        Assert.EndsWith("changed.txt", hit.Path);
    }

    [Fact]
    public async Task IncrementalUpsertAddsAndUpdatesFileHits()
    {
        var file = Path.Combine(_root, "live.txt");

        File.WriteAllText(file, "first needle\n");
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        var first = Assert.Single(await RawIndexedSearchAsync(new TermQuery("first")));
        Assert.EndsWith("live.txt", first.Path);

        File.WriteAllText(file, "second needle\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(await RawIndexedSearchAsync(new TermQuery("first")));
        var second = Assert.Single(await RawIndexedSearchAsync(new TermQuery("second")));
        Assert.EndsWith("live.txt", second.Path);
    }

    [Fact]
    public async Task IndexedSearchPreservesSourceAnchors()
    {
        var file = Path.Combine(_root, "scan.anchor");
        File.WriteAllText(file, "placeholder\n");
        var extractor = new AnchoredTestExtractor(".anchor");
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "anchored.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { extractor }));

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        var hit = Assert.Single(await RawIndexedSearchAsync(index, new TermQuery("needle")));
        Assert.NotNull(hit.Anchor);
        Assert.Equal(SourceAnchorKind.ImageOcr, hit.Anchor.Kind);
        Assert.Equal(10, hit.Anchor.X);
        Assert.Equal(20, hit.Anchor.Y);
        Assert.Equal(30, hit.Anchor.Width);
        Assert.Equal(40, hit.Anchor.Height);
        Assert.Equal("OCR region x10 y20 30x40 of 100x200", hit.Anchor.DisplayText);
    }

    [Fact]
    public async Task RefreshReindexesUnchangedFileWhenExtractorVersionChanges()
    {
        var file = Path.Combine(_root, "document.vtxt");
        File.WriteAllText(file, "stable input\n");
        var extractor = new VersionedTestExtractor(".vtxt") { ExtractorVersion = "1" };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "versioned.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { extractor }));

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        Assert.Single(await RawIndexedSearchAsync(index, new TermQuery("extractor-version-1")));

        extractor.ExtractorVersion = "2";
        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        Assert.Empty(await RawIndexedSearchAsync(index, new TermQuery("extractor-version-1")));
        Assert.Single(await RawIndexedSearchAsync(index, new TermQuery("extractor-version-2")));
    }

    [Fact]
    public async Task CoverageRejectsIndexWhenExtractorVersionChanges()
    {
        var file = Path.Combine(_root, "document.vtxt");
        File.WriteAllText(file, "stable input\n");
        var extractor = new VersionedTestExtractor(".vtxt") { ExtractorVersion = "1" };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "coverage-versioned.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { extractor }));

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        var covered = await index.GetCoverageAsync(
            new SearchRequest(new TermQuery("stable"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexCoverageStatus.Covered, covered.Status);

        extractor.ExtractorVersion = "2";
        var stale = await index.GetCoverageAsync(
            new SearchRequest(new TermQuery("stable"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Incompatible, stale.Status);
        Assert.Contains("extractor versions", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CoverageRejectsIndexWhenOcrRequestedButBuildDisabled()
    {
        File.WriteAllText(Path.Combine(_root, "scan.pdf"), "placeholder\n");

        await BuildAsync(new WalkerOptions { EnableOcr = false });

        var covered = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("placeholder"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexCoverageStatus.Covered, covered.Status);

        var ocrRequest = await _index.GetCoverageAsync(
            new SearchRequest(
                new TermQuery("placeholder"),
                new[] { _root },
                new WalkerOptions { EnableOcr = true },
                UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Incompatible, ocrRequest.Status);
    }

    [Fact]
    public async Task ValidateRootRecordsCleanValidationSeparatelyFromFullScan()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\n");
        await BuildAsync();

        var validation = await _index.ValidateRootAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);
        var location = Assert.Single(await _index.GetLocationsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(IndexValidationStatus.Passed, validation.Status);
        Assert.Equal(1, validation.FilesChecked);
        Assert.Equal(1, validation.FilesMatched);
        Assert.False(validation.HasDrift);
        Assert.NotNull(location.LastFullScanUtc);
        Assert.NotNull(location.LastFullValidationUtc);
        Assert.Equal(IndexValidationStatus.Passed.ToString(), location.LastValidationStatus);
        Assert.Equal(1, location.LastValidationFilesChecked);
        Assert.Equal(0, location.LastValidationMissingFromIndexCount);
        Assert.Empty(validation.DriftDetails);
        Assert.Empty(await _index.GetValidationDriftAsync(_root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateRootDetectsFilesystemDriftWithoutRefreshingIndex()
    {
        var changed = Path.Combine(_root, "changed.txt");
        var deleted = Path.Combine(_root, "deleted.txt");
        var added = Path.Combine(_root, "added.txt");
        File.WriteAllText(changed, "old\n");
        File.WriteAllText(deleted, "delete\n");
        await BuildAsync();

        File.WriteAllText(changed, "new\n");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddSeconds(2));
        File.Delete(deleted);
        File.WriteAllText(added, "added\n");

        var validation = await _index.ValidateRootAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);
        var location = Assert.Single(await _index.GetLocationsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(IndexValidationStatus.DriftDetected, validation.Status);
        Assert.Equal(1, validation.MissingFromIndex);
        Assert.Equal(1, validation.ChangedSinceIndex);
        Assert.Equal(1, validation.MissingFromDisk);
        Assert.True(validation.HasDrift);
        Assert.Equal(IndexValidationStatus.DriftDetected.ToString(), location.LastValidationStatus);
        Assert.Equal(1, location.LastValidationMissingFromIndexCount);
        Assert.Equal(1, location.LastValidationChangedCount);
        Assert.Equal(1, location.LastValidationMissingFromDiskCount);
        Assert.Contains("Drift detected", location.LastValidationMessage);

        var drift = await _index.GetValidationDriftAsync(_root, TestContext.Current.CancellationToken);
        Assert.Contains(drift, item =>
            item.Kind == IndexValidationDriftKind.MissingFromIndex &&
            string.Equals(item.Path, IndexPath.NormalizeFile(added), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(drift, item =>
            item.Kind == IndexValidationDriftKind.ChangedSinceIndex &&
            string.Equals(item.Path, IndexPath.NormalizeFile(changed), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(drift, item =>
            item.Kind == IndexValidationDriftKind.MissingFromDisk &&
            string.Equals(item.Path, IndexPath.NormalizeFile(deleted), StringComparison.OrdinalIgnoreCase));

        await BuildAsync();
        var cleanValidation = await _index.ValidateRootAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexValidationStatus.Passed, cleanValidation.Status);
        Assert.Empty(await _index.GetValidationDriftAsync(_root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedExtractionRowsTrackAttemptsAndExportAsCsvAndJson()
    {
        var file = Path.Combine(_root, "broken.bad");
        File.WriteAllText(file, "stable input\n");
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "failures.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { new ThrowingTestExtractor(".bad") }));

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);
        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        var failure = Assert.Single(await index.GetFailedFilesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(IndexPath.NormalizeRoot(_root), failure.Root);
        Assert.Equal(IndexPath.NormalizeFile(file), failure.Path);
        Assert.Equal("test.throwing", failure.ExtractorId);
        Assert.Equal("1", failure.ExtractorVersion);
        Assert.Contains("broken parser", failure.Error);
        Assert.Equal(2, failure.ExtractionAttemptCount);
        Assert.Equal(1, failure.RetryCount);
        Assert.NotNull(failure.LastAttemptUtc);
        var info = await index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, info.FailedFileCount);

        var csvPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "failures.csv");
        var jsonPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "failures.json");
        await index.ExportFailedFilesAsync(csvPath, IndexFailureExportFormat.Csv, TestContext.Current.CancellationToken);
        await index.ExportFailedFilesAsync(jsonPath, IndexFailureExportFormat.Json, TestContext.Current.CancellationToken);

        var csv = await File.ReadAllTextAsync(csvPath, TestContext.Current.CancellationToken);
        Assert.Contains("root,path,member_path,kind,code,severity,extractor_id,extractor_version,error,retry_count,attempt_count,last_attempt_utc", csv);
        Assert.Contains("test.throwing", csv);
        Assert.Contains("broken parser", csv);

        var json = await File.ReadAllTextAsync(jsonPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"ExtractorId\": \"test.throwing\"", json);
        Assert.Contains("\"RetryCount\": 1", json);
    }

    [Fact]
    public async Task ZipArchiveMemberSkipsAreReportedAsExtractionIssues()
    {
        var archivePath = Path.Combine(_root, "archive.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            AddZipEntry(archive, "readme.txt", "needle\n");
            AddZipEntry(archive, "image.bin", "skipped binary member\n");
        }

        var zip = new ZipExtractor();
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "archive-issues.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { zip }));

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        var issue = Assert.Single(await index.GetFailedFilesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("extraction_issue", issue.FailureKind);
        Assert.Equal("image.bin", issue.MemberPath);
        Assert.Equal("archive_member_unsupported_type", issue.IssueCode);
        Assert.Equal("filesearch.zip", issue.ExtractorId);

        var info = await index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, info.FailedFileCount);
    }

    [Fact]
    public async Task IndexingCanUseOutOfProcessExtractionService()
    {
        var file = Path.Combine(_root, "hosted.host");
        File.WriteAllText(file, "in-process content\n");
        var extractor = new HostedTestExtractor();
        var hostedExtraction = new FakeOutOfProcessExtractionService
        {
            Result = new OutOfProcessExtractionResult(
                new[] { new TextLine(9, "hosted needle") },
                new[] { new ExtractionIssue("member.bin", "archive_member_unsupported_type", "skipped member") }),
        };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "hosted.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { extractor }),
            outOfProcessExtraction: hostedExtraction);

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        Assert.Equal(1, hostedExtraction.CallCount);
        Assert.Equal(file, hostedExtraction.Path);
        Assert.Equal("test.hosted", hostedExtraction.ExtractorId);
        Assert.Equal(
            new[] { "hosted.host:9:hosted needle" },
            Normalize(await RawIndexedSearchAsync(index, new TermQuery("needle"))));
        var issue = Assert.Single(await index.GetFailedFilesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("member.bin", issue.MemberPath);
        Assert.Equal("archive_member_unsupported_type", issue.IssueCode);
    }

    [Fact]
    public async Task IndexingUsesIFilterFallbackWhenPrimaryReturnsNoLines()
    {
        var file = Path.Combine(_root, "empty.empty");
        File.WriteAllText(file, "primary sees no content\n");
        var fallback = new FakeWindowsIFilterExtractionService
        {
            Lines = new[] { new TextLine(1, "ifilter fallback needle") },
        };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "ifilter-empty.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { new EmptyTestExtractor(".empty") }),
            windowsIFilterExtraction: fallback);

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);
        await index.RefreshRootAsync(new IndexRequest(_root, new WalkerOptions()), IndexRefreshMode.Incremental, TestContext.Current.CancellationToken);

        Assert.Equal(1, fallback.CallCount);
        Assert.Null(fallback.LastPrimaryFailure);
        Assert.Equal(0, fallback.LastPrimaryLineCount);
        Assert.Equal(
            new[] { "empty.empty:1:ifilter fallback needle" },
            Normalize(await RawIndexedSearchAsync(index, new TermQuery("needle"))));
        var info = await index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, info.FailedFileCount);
        var diagnostic = Assert.Single(await index.GetFailedFilesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ifilter_fallback_used", diagnostic.IssueCode);
        Assert.Equal("info", diagnostic.Severity);
    }

    [Fact]
    public async Task IndexingUsesIFilterFallbackWhenPrimaryThrows()
    {
        var file = Path.Combine(_root, "broken.bad");
        File.WriteAllText(file, "primary throws\n");
        var fallback = new FakeWindowsIFilterExtractionService
        {
            Lines = new[] { new TextLine(2, "ifilter recovered needle") },
        };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "ifilter-throw.db") },
            new FileWalker(),
            new ExtractorRegistry(new ITextExtractor[] { new ThrowingTestExtractor(".bad") }),
            windowsIFilterExtraction: fallback);

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        Assert.Equal(1, fallback.CallCount);
        Assert.NotNull(fallback.LastPrimaryFailure);
        var diagnostic = Assert.Single(await index.GetFailedFilesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ifilter_fallback_used", diagnostic.IssueCode);
        Assert.Equal("info", diagnostic.Severity);
        Assert.Equal(
            new[] { "broken.bad:2:ifilter recovered needle" },
            Normalize(await RawIndexedSearchAsync(index, new TermQuery("needle"))));
    }

    [Fact]
    public async Task IndexingUsesIFilterFallbackWhenNoExtractorIsRegistered()
    {
        var file = Path.Combine(_root, "unknown.custom");
        File.WriteAllText(file, "custom content\n");
        var fallback = new FakeWindowsIFilterExtractionService
        {
            Lines = new[] { new TextLine(5, "ifilter custom needle") },
        };
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "ifilter-missing.db") },
            new FileWalker(),
            new ExtractorRegistry(Array.Empty<ITextExtractor>()),
            windowsIFilterExtraction: fallback);

        await index.BuildOrRefreshAsync(new IndexRequest(_root, new WalkerOptions()), TestContext.Current.CancellationToken);

        Assert.Equal(1, fallback.CallCount);
        Assert.Null(fallback.LastPrimaryExtractor);
        Assert.Contains(
            await index.GetFailedFilesAsync(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.IssueCode == "ifilter_fallback_used" && diagnostic.Severity == "info");
        Assert.Equal(
            new[] { "unknown.custom:5:ifilter custom needle" },
            Normalize(await RawIndexedSearchAsync(index, new TermQuery("needle"))));
    }

    [Fact]
    public async Task IncrementalDeleteRemovesStaleHits()
    {
        var file = Path.Combine(_root, "gone.txt");
        File.WriteAllText(file, "delete needle\n");
        await BuildAsync();

        await _index.DeleteFileAsync(_root, file, TestContext.Current.CancellationToken);

        Assert.Empty(await IndexedSearchAsync(new TermQuery("delete")));
    }

    [Fact]
    public async Task OverlappingRootsIndexIndependently()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "shared.txt"), "overlap needle\n");

        await BuildAsync();
        await _index.BuildOrRefreshAsync(new IndexRequest(nested, new WalkerOptions()), TestContext.Current.CancellationToken);

        var outerStats = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        var nestedStats = await _index.GetStatsAsync(nested, TestContext.Current.CancellationToken);
        Assert.Equal(1, outerStats.FileCount);
        Assert.Equal(1, nestedStats.FileCount);

        var request = new SearchRequest(new TermQuery("needle"), new[] { nested }, new WalkerOptions(), UseIndex: true);
        var nestedHits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            nestedHits.Add(hit);

        var found = Assert.Single(nestedHits);
        Assert.EndsWith("shared.txt", found.Path);
    }

    [Fact]
    public async Task BuildStripsSearchOnlyFiltersSoCoverageStaysTruthful()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), "filtered needle\n");

        // Globs and size caps are per-search filters the index profile can't
        // record; a build must ignore them or later searches would claim
        // coverage while silently missing files.
        await BuildAsync(new WalkerOptions
        {
            IncludeGlobs = new[] { "*.md" },
            MaxFileSizeBytes = 1,
        });

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("needle")));
        Assert.EndsWith("big.txt", hit.Path);
    }

    [Fact]
    public async Task IndexedSearchAppliesDirectoryExcludesAtQueryTime()
    {
        var vendor = Directory.CreateDirectory(Path.Combine(_root, "vendor")).FullName;
        File.WriteAllText(Path.Combine(_root, "app.txt"), "dir needle\n");
        File.WriteAllText(Path.Combine(vendor, "lib.txt"), "dir needle\n");

        // Build with no directory excludes so both files are indexed; a
        // search that excludes more directories than the build is covered and
        // must filter the extra ones per query.
        var noDirExcludes = new WalkerOptions
        {
            ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        await BuildAsync(noDirExcludes);

        var both = await IndexedSearchAsync(new TermQuery("needle"), noDirExcludes);
        Assert.Equal(2, both.Count);

        var filtered = await IndexedSearchAsync(new TermQuery("needle"), noDirExcludes with
        {
            ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor" },
        });
        var hit = Assert.Single(filtered);
        Assert.EndsWith("app.txt", hit.Path);
    }

    [Fact]
    public async Task CoverageRequiresSearchToExcludeAtLeastIndexedDirectories()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "needle\n");
        await BuildAsync(); // default excludes (.git, .vs, node_modules)

        // A search that wants pruned directories back can't be served by the
        // index — those subtrees were never indexed.
        var coverage = await _index.GetCoverageAsync(
            new SearchRequest(
                new TermQuery("needle"),
                new[] { _root },
                new WalkerOptions { ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Incompatible, coverage.Status);
    }

    [Fact]
    public async Task CoverageRequiresSearchToStayWithinIndexedIncludeDirectories()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var docs = Directory.CreateDirectory(Path.Combine(_root, "docs")).FullName;
        File.WriteAllText(Path.Combine(src, "app.txt"), "needle\n");
        File.WriteAllText(Path.Combine(docs, "readme.txt"), "needle\n");

        var srcOnly = new WalkerOptions
        {
            IncludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
        };
        await BuildAsync(srcOnly);

        var covered = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("needle"), new[] { _root }, srcOnly, UseIndex: true),
            TestContext.Current.CancellationToken);
        var broad = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Covered, covered.Status);
        Assert.Equal(IndexCoverageStatus.Incompatible, broad.Status);
    }

    [Fact]
    public async Task MultiRootIndexReturnsOnlyMatchingRootResults()
    {
        var otherRoot = Path.Combine(Path.GetDirectoryName(_root)!, "other");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "shared needle\n");
        File.WriteAllText(Path.Combine(otherRoot, "b.txt"), "shared needle\n");

        await BuildAsync();
        await _index.BuildOrRefreshAsync(new IndexRequest(otherRoot, new WalkerOptions()), TestContext.Current.CancellationToken);

        var rootHits = await IndexedSearchAsync(new TermQuery("needle"));
        var hit = Assert.Single(rootHits);
        Assert.EndsWith("a.txt", hit.Path);
    }

    [Fact]
    public async Task IndexQueueCoalescesRepeatedFileChanges()
    {
        var queue = new IndexQueue(_index);
        var file = Path.Combine(_root, "queued.txt");
        var item = new IndexQueueItem(
            _root,
            file,
            new WalkerOptions(),
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow,
            Persisted: false);

        await queue.EnqueueAsync(item, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(item with { DueUtc = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        Assert.Equal(1, queue.Count);
        var dequeued = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IndexChangeKind.UpsertFile, dequeued.Kind);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task DequeuePrefersHigherPriorityAmongDueItems()
    {
        var queue = new IndexQueue(new RecordingFileIndex());
        var lowRoot = Path.Combine(Path.GetDirectoryName(_root)!, "low-priority");
        var highRoot = Path.Combine(Path.GetDirectoryName(_root)!, "high-priority");

        // The low item is OLDER — the previous due-time-first ordering would
        // pick it; priority-first among due items must pick High.
        await queue.EnqueueAsync(
            new IndexQueueItem(lowRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow.AddSeconds(-10), Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(highRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.High, DateTime.UtcNow.AddSeconds(-1), Persisted: false),
            TestContext.Current.CancellationToken);

        var first = await queue.DequeueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IndexQueuePriority.High, first.Priority);
        Assert.Equal(IndexPath.NormalizeRoot(highRoot), first.Root);
    }

    [Fact]
    public async Task DequeueReturnsDueItemInsteadOfWaitingForFutureHigherPriority()
    {
        var queue = new IndexQueue(new RecordingFileIndex());
        var lowRoot = Path.Combine(Path.GetDirectoryName(_root)!, "due-low");
        var highRoot = Path.Combine(Path.GetDirectoryName(_root)!, "future-high");

        await queue.EnqueueAsync(
            new IndexQueueItem(lowRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow.AddSeconds(-1), Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(highRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.High, DateTime.UtcNow.AddSeconds(30), Persisted: false),
            TestContext.Current.CancellationToken);

        // Priority only ranks DUE items; a due Low item must be served now,
        // not delayed until a future High item ripens (that would starve the
        // queue every time a refresh is deferred by foreground-search yield).
        var stopwatch = Stopwatch.StartNew();
        var first = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(IndexQueuePriority.Low, first.Priority);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Dequeue waited {stopwatch.Elapsed} for a future item.");
    }

    [Fact]
    public async Task DequeueReturnsOldestAmongSamePriorityDueItems()
    {
        var queue = new IndexQueue(new RecordingFileIndex());
        var olderRoot = Path.Combine(Path.GetDirectoryName(_root)!, "older");
        var newerRoot = Path.Combine(Path.GetDirectoryName(_root)!, "newer");

        await queue.EnqueueAsync(
            new IndexQueueItem(newerRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Normal, DateTime.UtcNow.AddSeconds(-1), Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(olderRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Normal, DateTime.UtcNow.AddSeconds(-10), Persisted: false),
            TestContext.Current.CancellationToken);

        var first = await queue.DequeueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IndexPath.NormalizeRoot(olderRoot), first.Root);
    }

    [Fact]
    public async Task IndexedSearchStreamsLargeResultSetsWithoutDuplicates()
    {
        // 600 matching lines forces the mid-loop FTS batch flush (500) plus
        // the tail flush — the production-dominant path for common terms.
        File.WriteAllText(
            Path.Combine(_root, "many.txt"),
            string.Concat(Enumerable.Range(0, 600).Select(i => $"needle line {i}\n")));

        await BuildAsync();

        var hits = await IndexedSearchAsync(new TermQuery("needle"));

        Assert.Equal(600, hits.Count);
        Assert.Equal(600, hits.Select(h => h.LineNumber).Distinct().Count());
    }

    [Fact]
    public async Task IndexedSearchDeduplicatesLinesMatchingMultipleOrBranches()
    {
        // "alpha beta" matches both FTS candidate queries of the OR — the
        // cross-query dedupe must keep it a single hit, same as live search.
        File.WriteAllText(Path.Combine(_root, "overlap.txt"), "alpha beta\nalpha only\nbeta only\n");

        await BuildAsync();

        await AssertSameResultsAsync(new OrQuery(new Query[] { new TermQuery("alpha"), new TermQuery("beta") }));
    }

    [Fact]
    public async Task IndexedSearchRejectsMultipleRoots()
    {
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root, Path.GetTempPath() },
            new WalkerOptions(),
            UseIndex: true);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in _index.SearchAsync(request, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task IndexQueueDropsFileChangesSubsumedByPendingRootRefresh()
    {
        var queue = new IndexQueue(_index);
        var refresh = new IndexQueueItem(
            _root,
            null,
            new WalkerOptions(),
            IndexChangeKind.RefreshRoot,
            IndexQueuePriority.Low,
            DateTime.UtcNow,
            Persisted: false);
        var upsert = new IndexQueueItem(
            _root,
            Path.Combine(_root, "subsumed.txt"),
            new WalkerOptions(),
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow,
            Persisted: true);

        await queue.EnqueueAsync(refresh, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(upsert, TestContext.Current.CancellationToken);

        // The pending refresh covers the file change, so the upsert is dropped
        // before it is queued or persisted.
        Assert.Equal(1, queue.Count);
        Assert.Empty(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));

        var dequeued = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(IndexChangeKind.RefreshRoot, dequeued.Kind);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task IndexQueuePurgesQueuedFileChangesWhenRootRefreshArrives()
    {
        var otherRoot = Path.Combine(Path.GetDirectoryName(_root)!, "other-root");
        var queue = new IndexQueue(_index);

        await queue.EnqueueAsync(
            new IndexQueueItem(_root, Path.Combine(_root, "a.txt"), new WalkerOptions(), IndexChangeKind.UpsertFile, IndexQueuePriority.Normal, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(_root, Path.Combine(_root, "b.txt"), new WalkerOptions(), IndexChangeKind.DeleteFile, IndexQueuePriority.Normal, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(otherRoot, Path.Combine(otherRoot, "c.txt"), new WalkerOptions(), IndexChangeKind.UpsertFile, IndexQueuePriority.Normal, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(
            new IndexQueueItem(_root, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);

        // The refresh replaces both file items under _root; the other root's
        // item is untouched.
        Assert.Equal(2, queue.Count);

        var dequeued = new List<IndexQueueItem>
        {
            await queue.DequeueAsync(TestContext.Current.CancellationToken),
            await queue.DequeueAsync(TestContext.Current.CancellationToken),
        };

        Assert.Contains(dequeued, item => item.Kind == IndexChangeKind.RefreshRoot && IndexPath.EqualsPath(item.Root, IndexPath.NormalizeRoot(_root)));
        Assert.Contains(dequeued, item => item.Kind == IndexChangeKind.UpsertFile && IndexPath.EqualsPath(item.Root, IndexPath.NormalizeRoot(otherRoot)));
    }

    [Fact]
    public async Task IndexQueueRemoveRootDropsQueuedWorkForThatLocation()
    {
        var otherRoot = Path.Combine(Path.GetDirectoryName(_root)!, "other-remove-root");
        var queue = new IndexQueue(_index);

        await queue.EnqueueAsync(
            new IndexQueueItem(_root, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(_root, Path.Combine(_root, "a.txt"), new WalkerOptions(), IndexChangeKind.UpsertFile, IndexQueuePriority.Normal, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(
            new IndexQueueItem(otherRoot, null, new WalkerOptions(), IndexChangeKind.RefreshRoot, IndexQueuePriority.Low, DateTime.UtcNow, Persisted: false),
            TestContext.Current.CancellationToken);

        queue.RemoveRoot(_root);

        var queued = queue.GetQueuedRootCounts();
        Assert.False(queued.ContainsKey(IndexPath.NormalizeRoot(_root)));
        Assert.Equal(1, queue.Count);

        var remaining = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        Assert.True(IndexPath.EqualsPath(otherRoot, remaining.Root));
    }

    [Fact]
    public async Task IndexQueuePersistsCoalescedFileChangeOnlyOnce()
    {
        var index = new RecordingFileIndex();
        var queue = new IndexQueue(index);
        var item = new IndexQueueItem(
            _root,
            Path.Combine(_root, "burst.txt"),
            new WalkerOptions(),
            IndexChangeKind.UpsertFile,
            IndexQueuePriority.Normal,
            DateTime.UtcNow,
            Persisted: true);

        await queue.EnqueueAsync(item, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(item with { DueUtc = DateTime.UtcNow.AddSeconds(1) }, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(item with { DueUtc = DateTime.UtcNow.AddSeconds(2) }, TestContext.Current.CancellationToken);

        Assert.Equal(1, queue.Count);
        Assert.Equal(1, index.SavedPendingChanges);
    }

    [Fact]
    public async Task RootRefreshPendingChange_IsDurableAndSubsumesFilePendingChanges()
    {
        var file = Path.Combine(_root, "pending.txt");

        await _index.SavePendingChangeAsync(_root, file, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);
        await _index.SavePendingChangeAsync(_root, null, IndexChangeKind.RefreshRoot, TestContext.Current.CancellationToken);

        var change = Assert.Single(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(IndexPath.NormalizeRoot(_root), change.Root);
        Assert.Null(change.Path);
        Assert.Equal(IndexChangeKind.RefreshRoot, change.Kind);
    }

    [Fact]
    public async Task HandlesHostileRootDirectoryNamesThroughoutTheSqlLayer()
    {
        // Real roots like C:\Users\O'Brien flow into every root_path hole —
        // a distinct statement set from the file-path holes, and one whose
        // failure degrades silently (lookups return null, searches go empty).
        var hostileRoot = Path.Combine(Path.GetDirectoryName(_root)!, "O'Brien's docs; -- 😀");
        Directory.CreateDirectory(hostileRoot);
        var file = Path.Combine(hostileRoot, "a.txt");
        File.WriteAllText(file, "rooted needle\n");

        await _index.BuildOrRefreshAsync(new IndexRequest(hostileRoot, new WalkerOptions()), TestContext.Current.CancellationToken);

        var request = new SearchRequest(new TermQuery("needle"), new[] { hostileRoot }, new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.Equal(IndexPath.NormalizeFile(file), found.Path);

        var stats = await _index.GetStatsAsync(hostileRoot, TestContext.Current.CancellationToken);
        Assert.True(stats.Exists);
        Assert.Equal(1, stats.FileCount);

        await _index.SavePendingChangeAsync(hostileRoot, file, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);
        var pending = Assert.Single(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(IndexPath.NormalizeRoot(hostileRoot), pending.Root);

        await _index.RemovePendingChangeAsync(hostileRoot, file, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);
        Assert.Empty(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));

        await _index.ClearAsync(hostileRoot, TestContext.Current.CancellationToken);
        Assert.False((await _index.GetStatsAsync(hostileRoot, TestContext.Current.CancellationToken)).Exists);
    }

    [Theory]
    [InlineData("O'Brien's notes.txt")]
    [InlineData("semi;colon -- dashes.txt")]
    [InlineData("double''apostrophe''.txt")]
    [InlineData("emoji 😀 файл.txt")]
    [InlineData("percent%under_score.txt")]
    public async Task HandlesHostileFileNamesThroughoutTheSqlLayer(string fileName)
    {
        // File names are attacker-influencable content that flows into every
        // SQL statement; each hostile name must survive the full round trip:
        // index, search, pending-change save/remove, and delete.
        var file = Path.Combine(_root, fileName);
        File.WriteAllText(file, "hostile needle\n");

        await BuildAsync();

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("needle")));
        Assert.Equal(IndexPath.NormalizeFile(file), hit.Path);

        await _index.SavePendingChangeAsync(_root, file, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);
        var pending = Assert.Single(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(IndexPath.NormalizeFile(file), pending.Path);

        await _index.RemovePendingChangeAsync(_root, file, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);
        Assert.Empty(await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken));

        await _index.DeleteFileAsync(_root, file, TestContext.Current.CancellationToken);
        Assert.Empty(await IndexedSearchAsync(new TermQuery("needle")));
    }

    [Fact]
    public async Task UpsertSkipsReextractionWhenSizeAndTimestampUnchanged()
    {
        var file = Path.Combine(_root, "stable.txt");
        File.WriteAllText(file, "first needle\n");
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);
        var indexedWriteTime = File.GetLastWriteTimeUtc(file);

        // Same byte length and timestamp, different content: the upsert must
        // treat the file as unchanged and keep serving the indexed lines.
        File.WriteAllText(file, "fresh needle\n");
        File.SetLastWriteTimeUtc(file, indexedWriteTime);
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        Assert.Single(await RawIndexedSearchAsync(new TermQuery("first")));
        Assert.Empty(await RawIndexedSearchAsync(new TermQuery("fresh")));

        // A newer timestamp is a real change and gets re-extracted.
        File.SetLastWriteTimeUtc(file, indexedWriteTime.AddSeconds(2));
        await _index.UpsertFileAsync(_root, file, new WalkerOptions(), TestContext.Current.CancellationToken);

        Assert.Single(await RawIndexedSearchAsync(new TermQuery("fresh")));
        Assert.Empty(await RawIndexedSearchAsync(new TermQuery("first")));
    }

    [Fact]
    public async Task IndexedSearcherFallsBackToLiveScan_WhenIndexDoesNotCoverSearch()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "a.txt"), "recursive needle\n");

        await BuildAsync(new WalkerOptions { Recursive = false });

        var status = string.Empty;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions { Recursive = true },
            UseIndex: true,
            Status: message => status = message);

        var hits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("a.txt", found.Path);
        Assert.Contains("using live scan", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexedSearcherFallsBackToLiveScan_WhenRootRefreshDidNotComplete()
    {
        File.WriteAllText(Path.Combine(_root, "empty.txt"), string.Empty);
        await _index.UpsertFileAsync(
            _root,
            Path.Combine(_root, "empty.txt"),
            new WalkerOptions(),
            TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_root, "live.txt"), "live needle\n");

        var coverage = await _index.GetCoverageAsync(
            new SearchRequest(
                new TermQuery("needle"),
                new[] { _root },
                new WalkerOptions(),
                UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Missing, coverage.Status);
        Assert.Contains("incomplete", coverage.Message, StringComparison.OrdinalIgnoreCase);

        var hits = await IndexedSearchAsync(new TermQuery("needle"));

        var hit = Assert.Single(hits);
        Assert.EndsWith("live.txt", hit.Path);
    }

    [Fact]
    public async Task IndexedSearcherReturnsLiveHitsWhileBackgroundIndexingIsQueued()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "fallback needle\n");
        var queue = new IndexQueue(_index);
        var indexingService = new IndexingService(
            _index,
            queue,
            new IndexWatcherService(queue));
        await indexingService.StartAsync(Array.Empty<IndexedLocation>(), TestContext.Current.CancellationToken);

        try
        {
            var searcher = new IndexedSearcher(_liveSearcher, _index, new IndexCoverageService(_index), indexingService);
            var request = new SearchRequest(
                new TermQuery("needle"),
                new[] { _root },
                new WalkerOptions(),
                UseIndex: true);

            var hits = new List<Hit>();
            await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
                hits.Add(hit);

            Assert.Single(hits);
            Assert.True(queue.Count > 0 || indexingService.CurrentStatus.QueueLength > 0 || indexingService.CurrentStatus.IsProcessing);
        }
        finally
        {
            await indexingService.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task IndexedSearcherUsesLiveScanWhileBackgroundIndexingIsProcessing()
    {
        File.WriteAllText(Path.Combine(_root, "processing.txt"), "processing needle\n");
        var index = new ThrowIfUsedFileIndex();
        var indexingService = new ProcessingIndexingService();
        var searcher = new IndexedSearcher(_liveSearcher, index, new IndexCoverageService(index), indexingService);
        var status = string.Empty;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions(),
            UseIndex: true,
            Status: message => status = message);

        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("processing.txt", found.Path);
        Assert.Contains("using live scan", status, StringComparison.OrdinalIgnoreCase);
        Assert.True(indexingService.ForegroundSearchWasSet);
    }

    [Fact]
    public async Task IncompatibleSchemaIsTreatedAsMissingUntilRebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using (var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken))
        {
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("INSERT INTO meta VALUES ('schema_version', '1')", TestContext.Current.CancellationToken);
            await db.CheckpointAsync(TestContext.Current.CancellationToken);
        }

        var stats = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        var pending = await _index.GetPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(stats.Exists);
        Assert.Empty(pending);

        File.WriteAllText(Path.Combine(_root, "rebuilt.txt"), "rebuilt needle\n");
        await BuildAsync();

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("rebuilt")));
        Assert.EndsWith("rebuilt.txt", hit.Path);
    }

    [Fact]
    public async Task CurrentSchemaWithMissingColumnsIsTreatedAsMissingUntilRebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using (var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken))
        {
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)", TestContext.Current.CancellationToken);
            await db.ExecuteAsync($"INSERT INTO meta VALUES ('schema_version', '{IndexDatabase.CurrentSchemaVersion}')", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_volumes (id INTEGER PRIMARY KEY, volume_key TEXT)", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS index_roots (id INTEGER PRIMARY KEY, root_path TEXT, indexed_utc_ticks INTEGER, options_hash TEXT)", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT)", TestContext.Current.CancellationToken);
            await db.CheckpointAsync(TestContext.Current.CancellationToken);
        }

        var info = await _index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.False(info.IsCompatible);

        File.WriteAllText(Path.Combine(_root, "shape-rebuilt.txt"), "shape rebuilt needle\n");
        await BuildAsync();

        info = await _index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.True(info.IsCompatible);
        Assert.Equal(IndexDatabase.CurrentSchemaVersion, info.SchemaVersion);
        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("shape")));
        Assert.EndsWith("shape-rebuilt.txt", hit.Path);
    }

    [Fact]
    public async Task IndexedSearchToleratesWalCleanupContention()
    {
        File.WriteAllText(Path.Combine(_root, "wal.txt"), "wal needle\n");
        await BuildAsync();

        var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken);
        try
        {
            var hits = await IndexedSearchAsync(new TermQuery("needle"));

            var hit = Assert.Single(hits);
            Assert.EndsWith("wal.txt", hit.Path);
        }
        finally
        {
            await SafeDisposeAsync(db);
        }
    }

    [Fact]
    public async Task IndexedSearchAppliesFileFiltersAtQueryTime()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "shared needle\n");
        File.WriteAllText(Path.Combine(_root, "b.md"), "shared needle\n");

        await BuildAsync();

        var hits = await IndexedSearchAsync(
            new TermQuery("needle"),
            new WalkerOptions { IncludeGlobs = new[] { "*.md" } });

        var hit = Assert.Single(hits);
        Assert.EndsWith("b.md", hit.Path);
    }

    [Fact]
    public async Task ClearRemovesCurrentRootFromIndex()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "needle\n");

        await BuildAsync();
        var before = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        Assert.True(before.Exists);
        Assert.Equal(1, before.FileCount);

        await _index.ClearAsync(_root, TestContext.Current.CancellationToken);

        var after = await _index.GetStatsAsync(_root, TestContext.Current.CancellationToken);
        Assert.False(after.Exists);

        var coverage = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(IndexCoverageStatus.Missing, coverage.Status);
    }

    [Fact]
    public async Task DatabaseInfoReportsFootprintAndIndexedContent()
    {
        var a = Path.Combine(_root, "a.txt");
        File.WriteAllText(a, "one\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "two\nthree\n");

        await BuildAsync();
        await _index.SavePendingChangeAsync(_root, a, IndexChangeKind.UpsertFile, TestContext.Current.CancellationToken);

        var info = await _index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_dbPath, info.DatabasePath);
        Assert.True(info.Exists);
        Assert.True(info.IsCompatible);
        Assert.Equal(IndexDatabase.CurrentSchemaVersion, info.SchemaVersion);
        Assert.True(info.DatabaseBytes > 0);
        Assert.True(info.TotalBytes >= info.DatabaseBytes);
        Assert.Equal(1, info.LocationCount);
        Assert.Equal(2, info.TotalFileCount);
        Assert.Equal(3, info.TotalLineCount);
        Assert.Equal(1, info.PendingChangeCount);
        Assert.NotNull(info.LastIndexedUtc);
    }

    [Fact]
    public async Task RefreshPersistsVolumeCheckpoint_WhenJournalIsAvailable()
    {
        File.WriteAllText(Path.Combine(_root, "checkpoint.txt"), "checkpoint needle\n");
        var volume = FakeVolume(_root);
        var resolver = new FakeVolumeResolver(volume);
        var journal = new FakeUsnJournalReader(new UsnJournalSnapshot(123, 1, 50));
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = _dbPath },
            new FileWalker(),
            _registry,
            searchOptions: null,
            logger: null,
            resolver,
            journal);

        await index.BuildOrRefreshAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);

        var checkpoint = await index.GetVolumeCheckpointCoreAsync(volume, TestContext.Current.CancellationToken);

        Assert.NotNull(checkpoint);
        Assert.Equal((ulong)123, checkpoint.JournalId);
        Assert.Equal(50, checkpoint.LastCommittedUsn);
        Assert.Equal("healthy", checkpoint.Health);

        var info = await index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        var health = Assert.Single(info.VolumeHealth!);
        Assert.Equal(volume.VolumeKey, health.VolumeKey);
        Assert.Equal(volume.FileSystemName, health.FileSystemName);
        Assert.True(health.UsnSupported);
        Assert.Equal((ulong)123, health.JournalId);
        Assert.Equal(50, health.LastCommittedUsn);
        Assert.Equal("healthy", health.Health);

        var references = await index.GetReplayReferencesCoreAsync(volume, TestContext.Current.CancellationToken);
        Assert.Contains("1", references.FileReferences);
        Assert.Contains("1", references.DirectoryReferences);

        var rootIdentity = await index.GetRootIdentityCoreAsync(_root, TestContext.Current.CancellationToken);
        Assert.NotNull(rootIdentity);
        Assert.Equal(volume.VolumeKey, rootIdentity.VolumeKey);
        Assert.Equal("1", rootIdentity.FileReferenceNumber);
    }

    [Fact]
    public async Task RefreshPersistsCloudRootStrategyAndSkipsUsnCheckpoint()
    {
        File.WriteAllText(Path.Combine(_root, "cloud.txt"), "cloud needle\n");
        var previousOneDrive = Environment.GetEnvironmentVariable("OneDrive");
        Environment.SetEnvironmentVariable("OneDrive", _root);
        try
        {
            var volume = FakeVolume(_root);
            var resolver = new FakeVolumeResolver(volume);
            var journal = new FakeUsnJournalReader(new UsnJournalSnapshot(123, 1, 50));
            using var index = new CSharpDbFileIndex(
                new FileIndexOptions { DatabasePath = _dbPath },
                new FileWalker(),
                _registry,
                searchOptions: null,
                logger: null,
                resolver,
                journal);

            await index.BuildOrRefreshAsync(
                new IndexRequest(_root, new WalkerOptions()),
                TestContext.Current.CancellationToken);

            var info = await index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
            var strategy = Assert.Single(info.RootStrategies!);
            Assert.Equal(IndexLocationKind.CloudBacked, strategy.LocationKind);
            Assert.Equal(IndexUpdateStrategy.SnapshotScanAndWatcher, strategy.UpdateStrategy);
            Assert.False(strategy.UsnCatchUpEnabled);

            var checkpoint = await index.GetVolumeCheckpointCoreAsync(volume, TestContext.Current.CancellationToken);
            Assert.NotNull(checkpoint);
            Assert.Null(checkpoint.JournalId);
            Assert.Equal(0, checkpoint.LastCommittedUsn);
            Assert.Equal(0, journal.QueryCallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDrive", previousOneDrive);
        }
    }

    [Fact]
    public async Task CoverageRejectsIndexWhenRootIdentityChanged()
    {
        File.WriteAllText(Path.Combine(_root, "identity.txt"), "identity needle\n");
        var volume = FakeVolume(_root);
        var resolver = new FakeVolumeResolver(volume);
        resolver.FileIdsByPath[IndexPath.NormalizeRoot(_root)] = "root-v1";
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = _dbPath },
            new FileWalker(),
            _registry,
            searchOptions: null,
            logger: null,
            resolver,
            journalReader: null);

        await index.BuildOrRefreshAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);

        resolver.FileIdsByPath[IndexPath.NormalizeRoot(_root)] = "root-v2";

        var coverage = await index.GetCoverageAsync(
            new SearchRequest(new TermQuery("identity"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Incompatible, coverage.Status);
        Assert.Contains("identity", coverage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CoverageRejectsIndexWhenContentVersionIsOutOfDate()
    {
        File.WriteAllText(Path.Combine(_root, "version.txt"), "version needle\n");
        await BuildAsync();

        await using (var db = await Database.OpenAsync(_dbPath, TestContext.Current.CancellationToken))
        {
            await db.ExecuteAsync("UPDATE index_roots SET content_version = 'old-content'", TestContext.Current.CancellationToken);
            await db.ExecuteAsync("UPDATE files SET content_version = 'old-content'", TestContext.Current.CancellationToken);
            await db.CheckpointAsync(TestContext.Current.CancellationToken);
        }

        var coverage = await _index.GetCoverageAsync(
            new SearchRequest(new TermQuery("version"), new[] { _root }, new WalkerOptions(), UseIndex: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IndexCoverageStatus.Incompatible, coverage.Status);
        Assert.Contains("content version", coverage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayBatchCanAdvanceCheckpointWithoutFileChanges()
    {
        File.WriteAllText(Path.Combine(_root, "checkpoint.txt"), "checkpoint needle\n");
        var volume = FakeVolume(_root);
        var resolver = new FakeVolumeResolver(volume);
        var journal = new FakeUsnJournalReader(new UsnJournalSnapshot(123, 1, 50));
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = _dbPath },
            new FileWalker(),
            _registry,
            searchOptions: null,
            logger: null,
            resolver,
            journal);

        await index.BuildOrRefreshAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);

        await ((IIndexReplayWriter)index).ApplyReplayBatchAsync(
            volume,
            new[] { new IndexedLocation(_root, new WalkerOptions(), WatchEnabled: false) },
            Array.Empty<IndexReplayChange>(),
            journalId: 123,
            lastCommittedUsn: 75,
            health: "healthy",
            error: null,
            TestContext.Current.CancellationToken);

        var checkpoint = await index.GetVolumeCheckpointCoreAsync(volume, TestContext.Current.CancellationToken);
        Assert.NotNull(checkpoint);
        Assert.Equal(75, checkpoint.LastCommittedUsn);
    }

    [Fact]
    public async Task ReplayBatchPersistsEnsuredDirectoryIdentity()
    {
        var volume = FakeVolume(_root);
        var resolver = new FakeVolumeResolver(volume);
        resolver.FileIdsByPath[IndexPath.NormalizeRoot(_root)] = "root-dir";
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = _dbPath },
            new FileWalker(),
            _registry,
            searchOptions: null,
            logger: null,
            resolver,
            journalReader: null);

        await index.BuildOrRefreshAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);

        var directory = Path.Combine(_root, "created");
        Directory.CreateDirectory(directory);
        resolver.FileIdsByPath[IndexPath.NormalizeRoot(directory)] = "created-dir";

        await ((IIndexReplayWriter)index).ApplyReplayBatchAsync(
            volume,
            new[] { new IndexedLocation(_root, new WalkerOptions(), WatchEnabled: false) },
            new[]
            {
                new IndexReplayChange(
                    IndexReplayChangeKind.EnsureDirectory,
                    IndexPath.NormalizeRoot(_root),
                    IndexPath.NormalizeFile(directory),
                    "created-dir"),
            },
            journalId: 123,
            lastCommittedUsn: 75,
            health: "healthy",
            error: null,
            TestContext.Current.CancellationToken);

        var references = await index.GetReplayReferencesCoreAsync(volume, TestContext.Current.CancellationToken);
        Assert.Contains("created-dir", references.DirectoryReferences);
        Assert.DoesNotContain("created-dir", references.FileReferences);
    }

    [Fact]
    public async Task UpsertUsesFileIdentityToMoveExistingRowAfterRename()
    {
        var oldPath = Path.Combine(_root, "old-name.txt");
        var newPath = Path.Combine(_root, "new-name.txt");
        File.WriteAllText(oldPath, "rename needle\n");
        var resolver = new FakeVolumeResolver(FakeVolume(_root), "42");
        using var index = new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = _dbPath },
            new FileWalker(),
            _registry,
            searchOptions: null,
            logger: null,
            resolver,
            journalReader: null);

        await index.BuildOrRefreshAsync(
            new IndexRequest(_root, new WalkerOptions()),
            TestContext.Current.CancellationToken);

        File.Move(oldPath, newPath);
        await index.UpsertFileAsync(_root, newPath, new WalkerOptions(), TestContext.Current.CancellationToken);

        var request = new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in index.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("new-name.txt", found.Path);
    }

    [Fact]
    public async Task CompactPreservesSearchableIndexContent()
    {
        File.WriteAllText(Path.Combine(_root, "compact.txt"), "compact needle\n");
        await BuildAsync();

        await _index.CompactAsync(TestContext.Current.CancellationToken);

        var info = await _index.GetDatabaseInfoAsync(TestContext.Current.CancellationToken);
        Assert.True(info.Exists);
        Assert.True(info.IsCompatible);

        var hit = Assert.Single(await IndexedSearchAsync(new TermQuery("compact")));
        Assert.EndsWith("compact.txt", hit.Path);
    }

    [Fact]
    public async Task MetadataSearchFindsFilenameWithoutContentMatch()
    {
        File.WriteAllText(Path.Combine(_root, "folder1-report.txt"), "unrelated content\n");
        await BuildAsync();

        var hits = await RawIndexedSearchAsync(
            new TermQuery("folder1"),
            rawQuery: "folder1",
            mode: QueryMode.Boolean);

        var hit = Assert.Single(hits);
        Assert.EndsWith("folder1-report.txt", hit.Path);
        Assert.Equal(HitKind.Metadata, hit.Kind);
        Assert.Equal(0, hit.LineNumber);
        Assert.Contains("folder1", hit.LineContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataSearchRanksExactFilenameBeforePrefixMatch()
    {
        File.WriteAllText(Path.Combine(_root, "folder1-extra.txt"), "unrelated\n");
        File.WriteAllText(Path.Combine(_root, "folder1.txt"), "unrelated\n");
        await BuildAsync();

        var hits = await RawIndexedSearchAsync(
            new TermQuery("folder1"),
            rawQuery: "folder1",
            mode: QueryMode.Boolean);

        Assert.True(hits.Count >= 2);
        Assert.EndsWith("folder1.txt", hits[0].Path);
        Assert.Equal(HitKind.Metadata, hits[0].Kind);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task MetadataSearchUsesPrefixTokensForFilenameLookup()
    {
        File.WriteAllText(Path.Combine(_root, "folder1-report.txt"), "unrelated\n");
        await BuildAsync();

        var hit = Assert.Single(await RawIndexedSearchAsync(
            new TermQuery("fold"),
            rawQuery: "fold",
            mode: QueryMode.Boolean));

        Assert.EndsWith("folder1-report.txt", hit.Path);
        Assert.Equal(HitKind.Metadata, hit.Kind);
    }

    [Fact]
    public async Task MetadataSearchRanksFrequentlyOpenedFilesHigher()
    {
        var alpha = Path.Combine(_root, "open-alpha.txt");
        var beta = Path.Combine(_root, "open-beta.txt");
        File.WriteAllText(alpha, "unrelated\n");
        File.WriteAllText(beta, "unrelated\n");
        await BuildAsync();

        await _index.RecordFileOpenedAsync(beta, TestContext.Current.CancellationToken);
        await _index.RecordFileOpenedAsync(beta, TestContext.Current.CancellationToken);

        var hits = await RawIndexedSearchAsync(
            new TermQuery("open"),
            rawQuery: "open",
            mode: QueryMode.Boolean);

        Assert.True(hits.Count >= 2);
        Assert.EndsWith("open-beta.txt", hits[0].Path);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task MetadataSearchStreamsContentMatchesAfterMetadataHits()
    {
        File.WriteAllText(Path.Combine(_root, "folder1.txt"), "unrelated\n");
        File.WriteAllText(Path.Combine(_root, "content.txt"), "folder1 content\n");
        await BuildAsync();

        var hits = await RawIndexedSearchAsync(
            new TermQuery("folder1"),
            rawQuery: "folder1",
            mode: QueryMode.Boolean);

        Assert.True(hits.Count >= 2);
        Assert.Equal(HitKind.Metadata, hits[0].Kind);
        Assert.Contains(hits, hit => hit.Kind == HitKind.Content && hit.Path.EndsWith("content.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetadataSearchFallsBackToContentWhenMetadataDoesNotMatch()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "needle content\n");
        await BuildAsync();

        var hit = Assert.Single(await RawIndexedSearchAsync(
            new TermQuery("needle"),
            rawQuery: "needle",
            mode: QueryMode.Boolean));

        Assert.Equal(HitKind.Content, hit.Kind);
        Assert.Equal(1, hit.LineNumber);
        Assert.Equal("needle content", hit.LineContent);
    }

    [Fact]
    public async Task RegexSearchDoesNotReturnFilenameMetadataMatches()
    {
        File.WriteAllText(Path.Combine(_root, "needle-name.txt"), "unrelated content\n");
        await BuildAsync();

        var hits = await RawIndexedSearchAsync(
            new RegexQuery("needle.*"),
            rawQuery: "needle.*",
            mode: QueryMode.Regex);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task UnifiedIndexedContentSearchFiltersByFileName()
    {
        File.WriteAllText(Path.Combine(_root, "QuarterlyReport.txt"), "needle\n");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "needle\n");
        await BuildAsync();

        var query = new UnifiedQueryParser().Parse("name:report content:needle");
        var hits = await RawIndexedSearchAsync(
            query,
            rawQuery: "name:report content:needle",
            mode: QueryMode.Unified);

        var hit = Assert.Single(hits);
        Assert.EndsWith("QuarterlyReport.txt", hit.Path);
        Assert.Equal(HitKind.Content, hit.Kind);
    }

    [Fact]
    public async Task UnifiedIndexedMetadataOnlySearchReturnsFileHits()
    {
        File.WriteAllText(Path.Combine(_root, "report.txt"), "body\n");
        File.WriteAllText(Path.Combine(_root, "report.md"), "body\n");
        await BuildAsync();

        var query = new UnifiedQueryParser().Parse("ext:md");
        var hits = await RawIndexedSearchAsync(
            query,
            rawQuery: "ext:md",
            mode: QueryMode.Unified);

        var hit = Assert.Single(hits);
        Assert.EndsWith("report.md", hit.Path);
        Assert.Equal(HitKind.Metadata, hit.Kind);
        Assert.Equal(0, hit.LineNumber);
    }

    private async Task BuildAsync(WalkerOptions? options = null)
    {
        await _index.BuildOrRefreshAsync(
            new IndexRequest(_root, options ?? new WalkerOptions()),
            TestContext.Current.CancellationToken);
    }

    private async Task AssertSameResultsAsync(Query query)
    {
        var live = await LiveSearchAsync(query);
        var indexed = await IndexedSearchAsync(query);

        Assert.Equal(
            Normalize(live),
            Normalize(indexed));
    }

    private async Task<List<Hit>> LiveSearchAsync(Query query, WalkerOptions? options = null)
    {
        var request = new SearchRequest(query, new[] { _root }, options ?? new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var hit in _liveSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private async Task<List<Hit>> IndexedSearchAsync(Query query, WalkerOptions? options = null)
    {
        var request = new SearchRequest(query, new[] { _root }, options ?? new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in _indexedSearcher.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private async Task<List<Hit>> RawIndexedSearchAsync(
        Query query,
        WalkerOptions? options = null,
        string? rawQuery = null,
        QueryMode? mode = null)
    {
        var request = new SearchRequest(
            query,
            new[] { _root },
            options ?? new WalkerOptions(),
            UseIndex: true,
            RawQuery: rawQuery,
            Mode: mode);
        var hits = new List<Hit>();
        await foreach (var hit in _index.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private async Task<List<Hit>> RawIndexedSearchAsync(CSharpDbFileIndex index, Query query)
    {
        var request = new SearchRequest(query, new[] { _root }, new WalkerOptions(), UseIndex: true);
        var hits = new List<Hit>();
        await foreach (var hit in index.SearchAsync(request, TestContext.Current.CancellationToken))
            hits.Add(hit);
        return hits;
    }

    private static List<string> Normalize(IEnumerable<Hit> hits) =>
        hits
            .Select(hit => $"{Path.GetFileName(hit.Path)}:{hit.LineNumber}:{hit.LineContent}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IndexVolumeInfo FakeVolume(
        string root,
        string filesystem = "NTFS",
        bool usnSupported = true,
        IndexVolumeDriveKind driveKind = IndexVolumeDriveKind.Fixed) =>
        new(
            "fake-volume",
            @"\\.\C:",
            Path.GetPathRoot(root) ?? root,
            "123",
            filesystem,
            IsRemote: false,
            usnSupported,
            driveKind);

    private static async ValueTask SafeDisposeAsync(Database db)
    {
        try
        {
            await db.DisposeAsync();
        }
        catch (Exception ex) when (ex.Message.Contains("WAL file", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void AddZipEntry(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class VersionedTestExtractor : ITextExtractor
    {
        public VersionedTestExtractor(string extension) => SupportedExtensions = new[] { extension };

        public string ExtractorId => "test.versioned";

        public string ExtractorVersion { get; set; } = "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextLine(1, $"extractor-version-{ExtractorVersion}");
        }
    }

    private sealed class AnchoredTestExtractor : ITextExtractor
    {
        public AnchoredTestExtractor(string extension) => SupportedExtensions = new[] { extension };

        public string ExtractorId => "test.anchored";

        public string ExtractorVersion => "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextLine(
                3,
                "anchored needle",
                SourceAnchor.ImageOcrRegion(10, 20, 30, 40, 100, 200));
        }
    }

    private sealed class ThrowingTestExtractor : ITextExtractor
    {
        public ThrowingTestExtractor(string extension) => SupportedExtensions = new[] { extension };

        public string ExtractorId => "test.throwing";

        public string ExtractorVersion => "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("broken parser");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class EmptyTestExtractor : ITextExtractor
    {
        public EmptyTestExtractor(string extension) => SupportedExtensions = new[] { extension };

        public string ExtractorId => "test.empty";

        public string ExtractorVersion => "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; }

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class HostedTestExtractor : ITextExtractor
    {
        public string ExtractorId => "test.hosted";

        public string ExtractorVersion => "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".host" };

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextLine(1, "in-process fallback");
        }
    }

    private sealed class FakeWindowsIFilterExtractionService : IWindowsIFilterExtractionService
    {
        public int CallCount { get; private set; }

        public ITextExtractor? LastPrimaryExtractor { get; private set; }

        public Exception? LastPrimaryFailure { get; private set; }

        public long LastPrimaryLineCount { get; private set; }

        public IReadOnlyList<TextLine> Lines { get; init; } = Array.Empty<TextLine>();

        public IReadOnlyList<ExtractionIssue> Issues { get; init; } = Array.Empty<ExtractionIssue>();

        public bool CanTryFallback(
            string path,
            ITextExtractor? primaryExtractor,
            Exception? primaryFailure,
            long primaryLineCount)
        {
            LastPrimaryExtractor = primaryExtractor;
            LastPrimaryFailure = primaryFailure;
            LastPrimaryLineCount = primaryLineCount;
            return true;
        }

        public Task<WindowsIFilterExtractionResult?> TryExtractAsync(
            string path,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<WindowsIFilterExtractionResult?>(new WindowsIFilterExtractionResult(Lines, Issues));
        }
    }

    private sealed class FakeOutOfProcessExtractionService : IOutOfProcessExtractionService
    {
        public int CallCount { get; private set; }

        public string? Path { get; private set; }

        public string? ExtractorId { get; private set; }

        public OutOfProcessExtractionResult Result { get; init; } = new(
            Array.Empty<TextLine>(),
            Array.Empty<ExtractionIssue>());

        public bool ShouldUse(ITextExtractor extractor) => true;

        public Task<OutOfProcessExtractionResult> ExtractAsync(
            string path,
            ITextExtractor extractor,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Path = path;
            ExtractorId = extractor.ExtractorId;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeVolumeResolver : IIndexVolumeResolver
    {
        private readonly IndexVolumeInfo _volume;
        private readonly string _fileId;

        public FakeVolumeResolver(IndexVolumeInfo volume, string fileId = "1")
        {
            _volume = volume;
            _fileId = fileId;
        }

        public Dictionary<string, string> FileIdsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason)
        {
            volume = _volume;
            fallbackReason = string.Empty;
            return true;
        }

        public bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity)
        {
            var normalizedPath = IndexPath.NormalizeRoot(path);
            var fileId = FileIdsByPath.TryGetValue(normalizedPath, out var resolvedFileId)
                ? resolvedFileId
                : _fileId;
            identity = new ResolvedFileIdentity(fileId, ParentFileReferenceNumber: null);
            return true;
        }

        public FileIdResolutionResult ResolvePathFromFileId(
            IndexVolumeInfo volume,
            string fileReferenceNumber)
        {
            return new FileIdResolutionResult(
                FileIdResolutionStatus.UnsupportedIdentifier,
                null,
                null,
                "Not used by this test.");
        }
    }

    private sealed class FakeUsnJournalReader : IUsnJournalReader
    {
        private readonly UsnJournalSnapshot _snapshot;

        public FakeUsnJournalReader(UsnJournalSnapshot snapshot) => _snapshot = snapshot;

        public int QueryCallCount { get; private set; }

        public Task<UsnJournalSnapshot> QueryAsync(
            IndexVolumeInfo volume,
            CancellationToken cancellationToken)
        {
            QueryCallCount++;
            return Task.FromResult(_snapshot);
        }

        public async IAsyncEnumerable<UsnChangeRecord> ReadChangesAsync(
            IndexVolumeInfo volume,
            long startUsn,
            long stopAtUsn,
            ulong journalId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ProcessingIndexingService : IIndexingService
    {
        public event EventHandler<IndexingStatus>? StatusChanged;

        public IndexingStatus CurrentStatus { get; private set; } = new(true, false, true, 1, "Indexing");

        public bool IsPaused => false;

        public IndexerResourceProfile ResourceProfile { get; private set; } = IndexerResourceProfile.Balanced;

        public IndexerRuntimeOptions RuntimeOptions { get; private set; } = IndexerRuntimeOptions.Default;

        public bool ForegroundSearchWasSet { get; private set; }

        public Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddOrUpdateLocationAsync(IndexedLocation location, bool queueInitialRefresh, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveLocationAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnqueueRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken) => Task.CompletedTask;

        public void SetForegroundSearchActive(bool isActive)
        {
            ForegroundSearchWasSet |= isActive;
            CurrentStatus = isActive
                ? CurrentStatus
                : CurrentStatus with { IsProcessing = false };
            StatusChanged?.Invoke(this, CurrentStatus);
        }

        public void SetResourceProfile(IndexerResourceProfile profile) => ResourceProfile = profile;

        public void SetRuntimeOptions(IndexerRuntimeOptions options) => RuntimeOptions = options.Normalize();

        public void Pause() { }

        public void Resume() { }
    }

    private sealed class RecordingFileIndex : IFileIndex
    {
        public int SavedPendingChanges { get; private set; }

        public string DatabasePath => string.Empty;

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexCoverage(IndexCoverageStatus.Missing, "Not indexed"));

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new IndexDatabaseInfo(DatabasePath, false, false, "5", 0, 0, 0, 0, 0, 0, 0, null));

        public Task CompactAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken)
        {
            SavedPendingChanges++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

        public Task RemovePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowIfUsedFileIndex : IFileIndex
    {
        public string DatabasePath => string.Empty;

        public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Indexed search should not be used while background indexing is processing.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Coverage should not be checked while background indexing is processing.");

        public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

        public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

        public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new IndexDatabaseInfo(DatabasePath, false, false, "5", 0, 0, 0, 0, 0, 0, 0, null));

        public Task CompactAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

        public Task RemovePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
