using System.Runtime.CompilerServices;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

public sealed class IndexedFullScanTests : IAsyncDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "FileSearch.FullScan", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public IndexedFullScanTests() => Directory.CreateDirectory(_work);

    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_work, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, CancellationToken.None); }
        }
    }

    [Fact]
    public async Task FullScanIncludesShortAsciiAndUnicodeBeyondTheFormerFileBatchBoundary()
    {
        await using var db = await CreateDatabaseAsync();
        await SeedFilesAsync(db, Enumerable.Range(1, 603).Select(id => new FileRow(id, 1, Path.Combine(_work, $"file-{id:D4}.txt"))));
        await SeedLinesAsync(db, Enumerable.Range(1, 603).Select(id => new LineRow(id, id, id, 1,
            id == 603 ? "東京 ab" : "ordinary document")));

        var rows = await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 1, Token));
        Assert.Equal(603, rows.Count);
        Assert.Equal(603, rows.Select(row => row.Path).Distinct().Count());
        foreach (string query in new[] { "東京", "ab" })
        {
            var expression = new TermQuery(query);
            var matching = rows.Where(row => expression.TryCollectHighlights(row.Content, new List<MatchSpan>()));
            Assert.EndsWith("file-0603.txt", Assert.Single(matching).Path);
        }
    }

    [Fact]
    public async Task FullScanChoosesLatestCaseInsensitiveVersionBeforeFilteringStatusAndRoot()
    {
        await using var db = await CreateDatabaseAsync();
        string current = Path.Combine(_work, "current.txt"), deleted = Path.Combine(_work, "deleted.txt"),
            failed = Path.Combine(_work, "failed.txt"), skipped = Path.Combine(_work, "skipped.txt"), pending = Path.Combine(_work, "pending.txt");
        FileRow[] files =
        [
            new(1, 1, current), new(2, 1, current.ToUpperInvariant()), new(3, 1, current, FileStatus.Indexing),
            new(4, 1, deleted), new(5, 1, deleted.ToUpperInvariant(), FileStatus.Stale),
            new(6, 1, failed), new(7, 1, failed.ToUpperInvariant(), FileStatus.Error),
            new(8, 1, skipped), new(9, 1, skipped.ToUpperInvariant(), FileStatus.Skipped),
            new(10, 1, pending, FileStatus.Indexing), new(11, 2, current), new(12, 1, Path.Combine(_work, "retained.txt")),
        ];
        await SeedFilesAsync(db, files);
        await SeedLinesAsync(db, files.Select(file => new LineRow(file.Id, file.Id, file.Id, 1, "東京 " + file.Id))
            .Append(new LineRow(99, 999, 99, 1, "東京 orphan")));
        await db.ExecuteAsync("INSERT INTO lines (id, file_id, line_number, content) VALUES (100, NULL, 1, '東京 orphan without owner')", Token);

        var rows = await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 1, Token));
        Assert.Equal(new[] { "東京 2", "東京 12" }, rows.Select(row => row.Content).OrderBy(text => int.Parse(text.AsSpan(3))).ToArray());
        Assert.Contains(rows, row => row.Path == current.ToUpperInvariant());
        Assert.All(rows, row => Assert.Equal(FileStatus.Ok, row.Status));
        Assert.Empty(await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 3, Token)));
    }

    [Fact]
    public async Task FullScanPreservesAnchorsLocatorsMetadataAndLegacyNullContentUnits()
    {
        await using var db = await CreateDatabaseAsync();
        string path = Path.Combine(_work, "report.txt");
        await SeedFilesAsync(db, [new FileRow(17, 1, path)]);
        var pdf = SourceAnchor.PdfPage(7);
        var database = new SourceAnchor(SourceAnchorKind.Database, "record 751 Notes", Database:
            new DatabaseLocation(DatabaseKind.Sqlite, "Customer \"records\"", 751, "Notes", "751", "source-fingerprint"));
        await SeedLinesAsync(db,
        [
            new(91, 17, 101, 2, "東京 page", pdf),
            new(7, 17, 102, 3, "東京 record", database),
            new(55, 17, null, 4, "東京 legacy"),
        ]);

        var rows = await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 1, Token));
        var cached = await ReadAsync(IndexTables.ReadCachedLinesAsync(db, 1, Token));
        Assert.Equal(new long[] { 91, 7, 55 }, cached.Select(row => row.Id).ToArray());
        Assert.All(cached, row => Assert.Equal(17, row.FileId));
        Assert.Equal(rows, cached.Select(row => row.Line).ToList());
        Assert.Equal(3, rows.Count);
        Assert.Equal(pdf, rows[0].Anchor);
        Assert.Equal(SourceLocator.FromAnchor(pdf, 2), rows[0].Locator);
        Assert.Equal(101, rows[0].ContentUnitId);
        Assert.Equal(database, rows[1].Anchor);
        Assert.Equal(database.Database, rows[1].Locator!.Database);
        Assert.Equal(102, rows[1].ContentUnitId);
        Assert.Null(rows[2].Anchor);
        Assert.Null(rows[2].ContentUnitId);
        Assert.Equal(SourceLocator.FromAnchor(null, 4), rows[2].Locator);
        Assert.All(rows, row =>
        {
            Assert.Equal(path, row.Path);
            Assert.Equal("report.txt", row.FileName);
            Assert.Equal(".txt", row.Extension);
            Assert.Equal(101, row.SizeBytes);
            Assert.Equal(123, row.CreatedUtcTicks);
            Assert.Equal(456, row.ModifiedUtcTicks);
            Assert.Equal("fixture.extractor", row.ExtractorId);
            Assert.Equal("document", row.FileTypeCategory);
        });
    }

    [Fact]
    public async Task FullScanPreservesLegacyDefaultsForNullableFileMetadata()
    {
        await using var db = await CreateDatabaseAsync();
        string path = Path.Combine(_work, "legacy.txt");
        await SeedFilesAsync(db, [new FileRow(17, 1, path)]);
        await SeedLinesAsync(db, [new(91, 17, null, 2, "東京 legacy")]);
        await db.ExecuteAsync("UPDATE files SET directory_path = NULL, created_utc_ticks = NULL, extractor_id = NULL, file_type_category = NULL, attributes = NULL, open_count = NULL, last_opened_utc_ticks = NULL WHERE id = 17", Token);

        var metadata = Assert.Single(await ReadAsync(IndexTables.ReadFileMetadataAsync(db, 1, Token)));
        Assert.Equal(string.Empty, metadata.DirectoryPath);
        Assert.Equal(0, metadata.CreatedUtcTicks);
        Assert.Equal(string.Empty, metadata.ExtractorId);
        Assert.Equal(string.Empty, metadata.FileTypeCategory);
        Assert.Equal(0, metadata.Attributes);
        Assert.Equal(0, metadata.OpenCount);
        Assert.Equal(0, metadata.LastOpenedUtcTicks);

        var legacy = Assert.Single(await ReadAsync(IndexTables.ReadLinesAsync(db, IndexTables.SelectLinesSql(1), Token)));
        var current = Assert.Single(await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 1, Token)));
        var cached = Assert.Single(await ReadAsync(IndexTables.ReadCachedLinesAsync(db, 1, Token)));
        Assert.Equal(legacy, current);
        Assert.Equal(legacy, cached.Line);
        Assert.Equal(91, cached.Id);
        Assert.Equal(17, cached.FileId);
        Assert.Equal(0, current.CreatedUtcTicks);
        Assert.Equal(string.Empty, current.ExtractorId);
        Assert.Equal(string.Empty, current.FileTypeCategory);
    }

    [Fact]
    public async Task FullScanObservesCancellationBetweenRowsAndReleasesTheReader()
    {
        await using var db = await CreateDatabaseAsync();
        await SeedFilesAsync(db, [new FileRow(1, 1, Path.Combine(_work, "report.txt"))]);
        await SeedLinesAsync(db, [new(1, 1, 1, 1, "東京 first"), new(2, 1, 2, 2, "東京 last")]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using (var rows = IndexTables.ReadCurrentLinesAsync(db, 1, cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await rows.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rows.MoveNextAsync().AsTask());
        }
        Assert.Equal(2, (await ReadAsync(IndexTables.ReadCurrentLinesAsync(db, 1, Token))).Count);
    }

    [Fact]
    public async Task ShortQueryHitCapKeepsTheEarliestLineNumbersWhenExtractorEmitsOutOfOrder()
    {
        string root = Path.Combine(_work, "documents");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report.order");
        await File.WriteAllTextAsync(path, "fixture", Token);
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(_work, "real-index.db") },
            new FileWalker(), new ExtractorRegistry([new OutOfOrderExtractor()]), new SearchOptions { MaxHitsPerFile = 2 });
        await index.BuildOrRefreshAsync(new IndexRequest(root, new WalkerOptions()), Token);
        IndexSearchTimings? timings = null;
        index.SearchTimingsCallback = value => timings = value;
        var request = new SearchRequest(new TermQuery("東京"), [root], new WalkerOptions(), UseIndex: true);
        var hits = await ReadAsync(index.SearchAsync(request, Token));

        Assert.Equal(new[] { 10, 20 }, hits.Select(hit => hit.LineNumber).ToArray());
        Assert.All(hits, hit => Assert.Equal(path, hit.Path));
        Assert.True(Assert.IsType<IndexSearchTimings>(timings).UsedFullScan);
    }

    private async Task<Database> CreateDatabaseAsync()
    {
        var db = await Database.OpenAsync(Path.Combine(_work, "scan.db"), Token);
        await db.ExecuteAsync("CREATE TABLE files (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT, directory_path TEXT, file_name TEXT, extension TEXT, size_bytes INTEGER, created_utc_ticks INTEGER, modified_utc_ticks INTEGER, attributes INTEGER, file_type_category TEXT, open_count INTEGER, last_opened_utc_ticks INTEGER, status TEXT, extractor_id TEXT)", Token);
        await db.ExecuteAsync("CREATE TABLE lines (id INTEGER PRIMARY KEY, file_id INTEGER, content_unit_id INTEGER, line_number INTEGER, content TEXT, anchor_json TEXT)", Token);
        await db.ExecuteAsync("CREATE INDEX idx_lines_file_line ON lines(file_id, line_number)", Token);
        return db;
    }

    private static async Task SeedFilesAsync(Database db, IEnumerable<FileRow> files)
    {
        var batch = db.PrepareInsertBatch("files");
        foreach (var file in files)
        {
            batch.AddRow(DbValue.FromInteger(file.Id), DbValue.FromInteger(file.RootId), DbValue.FromText(file.Path),
                DbValue.FromText(Path.GetDirectoryName(file.Path)!), DbValue.FromText(Path.GetFileName(file.Path)), DbValue.FromText(".txt"),
                DbValue.FromInteger(101), DbValue.FromInteger(123), DbValue.FromInteger(456), DbValue.FromInteger(0),
                DbValue.FromText("document"), DbValue.FromInteger(0), DbValue.FromInteger(0), DbValue.FromText(file.Status), DbValue.FromText("fixture.extractor"));
        }
        await batch.ExecuteAsync(Token);
    }

    private static async Task SeedLinesAsync(Database db, IEnumerable<LineRow> lines)
    {
        var batch = db.PrepareInsertBatch("lines");
        foreach (var line in lines)
        {
            batch.AddRow(DbValue.FromInteger(line.Id), DbValue.FromInteger(line.FileId),
                line.ContentUnitId.HasValue ? DbValue.FromInteger(line.ContentUnitId.Value) : DbValue.Null,
                DbValue.FromInteger(line.Number), DbValue.FromText(line.Content), IndexTables.SerializeAnchor(line.Anchor));
        }
        await batch.ExecuteAsync(Token);
    }

    private static async Task<List<T>> ReadAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (var value in source.WithCancellation(Token)) values.Add(value);
        return values;
    }

    private sealed record FileRow(long Id, long RootId, string Path, string Status = FileStatus.Ok);
    private sealed record LineRow(long Id, long FileId, long? ContentUnitId, int Number, string Content, SourceAnchor? Anchor = null);

    private sealed class OutOfOrderExtractor : ITextExtractor
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".order"];

        public async IAsyncEnumerable<TextLine> ExtractAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (int number in new[] { 30, 10, 20 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TextLine(number, "東京 line " + number);
            }
        }
    }
}
