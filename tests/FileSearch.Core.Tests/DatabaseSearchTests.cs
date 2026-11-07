using System.Security.Cryptography;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Data.Sqlite;

namespace FileSearch.Core.Tests;

public sealed class DatabaseSearchTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseTests", Guid.NewGuid().ToString("N"));
    public DatabaseSearchTests() => Directory.CreateDirectory(_root);
    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, CancellationToken.None); }
        }
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task ScalarSearchPreservesExactSourceAndLocatesQuotedTableRowColumn(DatabaseKind kind)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "source.db", kind, 3, Token);
        var before = SourceState(path);
        var issues = new ListExtractionIssueSink();
        var lines = await ReadAsync(new DatabaseExtractor().ExtractAsync(path, issues, Token));
        var line = Assert.Single(lines, line => line.Content.Contains("needle", StringComparison.Ordinal));
        Assert.Equal(DatabaseTestFiles.TableName, line.Anchor!.Database!.Table);
        Assert.Equal(3, line.Anchor.Database.RowNumber);
        Assert.Equal("Notes", line.Anchor.Database.Column);
        Assert.Equal(kind, line.Anchor.Database.Engine);
        Assert.Contains("End of record", line.Content);
        Assert.Equal(before, SourceState(path));
        Assert.Empty(issues.Issues);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task PagesIncludeFullMatchingRecordAndNavigateBeyondInitialPage(DatabaseKind kind)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "source.db", kind, 275, Token);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        Assert.Equal(2, session.Tables.Count);
        var first = await session.ReadPageAsync(DatabaseTestFiles.TableName, 1, 250, Token);
        var second = await session.ReadPageAsync(DatabaseTestFiles.TableName, 251, 250, Token);
        Assert.True(first.HasMore); Assert.False(second.HasMore);
        Assert.Equal(25, second.Rows.Count);
        Assert.Equal("275", second.Rows[^1].Values[0]);
        Assert.Equal("Person 275", second.Rows[^1].Values[1]);
        Assert.Contains("needle 東京 O'Brien", second.Rows[^1].Values[2]);
        Assert.Null(second.Rows[^1].Values[3]);
        Assert.Empty((await session.ReadPageAsync("EmptyTable", 1, 250, Token)).Rows);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task LiveAndIndexedSearchPreserveDatabaseLocatorAndTypeFilter(DatabaseKind kind)
    {
        string data = Path.Combine(_root, "data");
        await DatabaseTestFiles.CreateAsync(data, "source.db", kind, 3, Token);
        var extractor = new DatabaseExtractor();
        var registry = new ExtractorRegistry([extractor]);
        var walker = new FileWalker();
        var walk = new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db" } };
        var request = new SearchRequest(new UnifiedQueryParser().Parse("type:database needle"), [data], walk);
        var live = await HitsAsync(new Searcher(walker, registry).SearchAsync(request, Token));
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(_root, "index.db") }, walker, registry);
        await index.BuildOrRefreshAsync(new IndexRequest(data, walk), Token);
        var indexed = await HitsAsync(index.SearchAsync(request with { UseIndex = true }, Token));
        Assert.Single(live); Assert.Single(indexed);
        Assert.Equal(live[0].Anchor!.Database, indexed[0].Anchor!.Database);
        Assert.Equal(live[0].Anchor!.Database, indexed[0].Locator!.Database);
        Assert.Equal(ContentUnitKind.DatabaseCell, ContentUnit.FromTextLine(1, 1, new TextLine(1, live[0].LineContent, live[0].Anchor), extractor.ExtractorId, "1").Kind);
    }

    [Fact]
    public async Task SqliteWithoutRowIdCompositeKeysBlobsAndGeneratedColumnsAreHandled()
    {
        string path = Path.Combine(_root, "without.sqlite3");
        await ExecuteSqlite(path, "CREATE TABLE Pair (A TEXT, B INTEGER, Value TEXT, Payload BLOB, Calculated TEXT GENERATED ALWAYS AS (upper(Value)) VIRTUAL, PRIMARY KEY(A,B)) WITHOUT ROWID; INSERT INTO Pair(A,B,Value,Payload) VALUES ('a',2,'needle',X'6E6565646C65'),('a',1,'first',NULL); CREATE VIRTUAL TABLE SearchIndex USING fts5(Text); INSERT INTO SearchIndex VALUES ('virtual-secret');");
        var lines = await ReadAsync(new DatabaseExtractor().ExtractAsync(path, Token));
        Assert.Single(lines, line => line.Content == "needle");
        Assert.DoesNotContain(lines, line => line.Content == "NEEDLE" || line.Content.Contains("virtual-secret", StringComparison.Ordinal));
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        Assert.Single(session.Tables);
        var page = await session.ReadPageAsync("Pair", 1, 250, Token);
        Assert.Equal("first", page.Rows[0].Values[2]);
        Assert.Contains("A=a", page.Rows[1].Key);
        Assert.Contains("B=2", page.Rows[1].Key);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task StaleHitAndChangesDuringPagingAreRejected(DatabaseKind kind)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "source.db", kind, 1, Token);
        string oldFingerprint = DatabaseFiles.Fingerprint(path);
        await using var session = await DatabaseReadSession.OpenAsync(path, oldFingerprint, Token);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadPageAsync(DatabaseTestFiles.TableName, 1, 250, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseReadSession.OpenAsync(path, oldFingerprint, Token));
    }

    [Fact]
    public async Task SqliteStableWalSnapshotReadsCommittedDataWithoutChangingSourceSidecars()
    {
        string writing = Path.Combine(_root, "writer.db"), source = Path.Combine(_root, "source.sqlite");
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = writing, Pooling = false }.ToString());
        await writer.OpenAsync(Token);
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('committed needle')";
        await command.ExecuteNonQueryAsync(Token);
        File.Copy(writing, source); File.Copy(writing + "-wal", source + "-wal");
        var before = SourceState(source);
        var lines = await ReadAsync(new DatabaseExtractor().ExtractAsync(source, Token));
        Assert.Contains(lines, line => line.Content == "committed needle");
        Assert.Equal(before, SourceState(source));
        Assert.False(File.Exists(source + "-shm"));
    }

    [Fact]
    public async Task CSharpDbStableWalIncludesCommittedRowsAndRemovesOnlyItsPrivateSnapshot()
    {
        string writerPath = Path.Combine(_root, "writer.csdb"), source = Path.Combine(_root, "source.csdb");
        await using var writer = await CSharpDB.Engine.Database.OpenAsync(writerPath, Token);
        await using (var query = await writer.ExecuteAsync("CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT)", Token)) { }
        await using (var query = await writer.ExecuteAsync("INSERT INTO Data VALUES (9,'committed needle')", Token)) { }
        File.Copy(writerPath, source); File.Copy(writerPath + ".wal", source + ".wal");
        var before = SourceState(source);
        string temporary;
        await using (var session = await DatabaseReadSession.OpenAsync(source, null, Token))
        {
            temporary = session.SnapshotPath;
            Assert.Contains("FileSearchDatabasePreview", temporary);
            Assert.Equal("committed needle", (await session.ReadPageAsync("Data", 1, 250, Token)).Rows[0].Values[1]);
        }
        Assert.False(File.Exists(temporary));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporary)));
        Assert.Equal(before, SourceState(source));
    }

    [Fact]
    public async Task TruncatedCellsAreReportedAndOversizedSourcesRejectedBeforeCopy()
    {
        string path = Path.Combine(_root, "large.sqlite");
        await ExecuteSqlite(path, "CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('" + new string('x', DatabaseFiles.MaxCellCharacters + 100) + "');");
        var issues = new ListExtractionIssueSink();
        var lines = await ReadAsync(new DatabaseExtractor().ExtractAsync(path, issues, Token));
        Assert.Equal(DatabaseFiles.MaxCellCharacters + 1, Assert.Single(lines).Content.Length);
        Assert.Contains(issues.Issues, issue => issue.Code == "database_value_truncated");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(DatabaseFiles.MaxSnapshotBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseReadSession.OpenAsync(path, null, Token));
    }

    [Fact]
    public async Task RefreshSeesWalOnlyUpdatesWhenMainFileTimestampIsUnchanged()
    {
        string data = Path.Combine(_root, "data"); Directory.CreateDirectory(data);
        string source = Path.Combine(data, "source.sqlite"), writing = Path.Combine(_root, "writer.sqlite");
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = writing, Pooling = false }.ToString());
        await writer.OpenAsync(Token);
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('before_marker')";
        await command.ExecuteNonQueryAsync(Token);
        File.Copy(writing, source); File.Copy(writing + "-wal", source + "-wal");
        DateTime mainTimestamp = File.GetLastWriteTimeUtc(source);
        var registry = new ExtractorRegistry([new DatabaseExtractor()]);
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(_root, "index.db") }, new FileWalker(), registry);
        var walk = new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".sqlite" } };
        await index.BuildOrRefreshAsync(new IndexRequest(data, walk), Token);
        command.CommandText = "UPDATE Data SET Value='after_marker'"; await command.ExecuteNonQueryAsync(Token);
        File.Copy(writing + "-wal", source + "-wal", overwrite: true);
        Assert.Equal(mainTimestamp, File.GetLastWriteTimeUtc(source));
        await index.BuildOrRefreshAsync(new IndexRequest(data, walk), Token);
        Assert.Single(await HitsAsync(index.SearchAsync(new SearchRequest(new TermQuery("after_marker"), [data], walk) with { UseIndex = true }, Token)));
        Assert.Empty(await HitsAsync(index.SearchAsync(new SearchRequest(new TermQuery("before_marker"), [data], walk) with { UseIndex = true }, Token)));
    }

    [Theory]
    [InlineData("SQLite format 3\0")]
    [InlineData("CSDB")]
    public async Task CorruptRecognizedDatabaseReturnsAUsefulError(string magic)
    {
        string path = Path.Combine(_root, "corrupt.db");
        var bytes = new byte[4096]; System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(path, bytes, Token);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseReadSession.OpenAsync(path, null, Token));
        Assert.Contains("database", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingUnsupportedCancelledAndLockedSourcesNeverCreateOrWriteDatabases()
    {
        string missing = Path.Combine(_root, "missing.db");
        await Assert.ThrowsAsync<FileNotFoundException>(() => DatabaseReadSession.OpenAsync(missing, null, Token));
        Assert.False(File.Exists(missing));
        string other = Path.Combine(_root, "other.db"); await File.WriteAllTextAsync(other, "Not a database", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseReadSession.OpenAsync(other, null, Token));
        string path = await DatabaseTestFiles.CreateAsync(_root, "locked.db", DatabaseKind.Sqlite, 1, Token);
        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => DatabaseReadSession.OpenAsync(path, null, Token));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseReadSession.OpenAsync(path, null, cancel.Token));
        var rows = await ReadAsync(new DatabaseExtractor(new FileIndexOptions { DatabasePath = path }).ExtractAsync(path, Token));
        Assert.Empty(rows);
    }

    private static async Task ExecuteSqlite(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(Token);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
    private static string[] SourceState(string path) => new[] { "", "-wal", ".wal", "-shm", ".shm", "-journal" }.Select(suffix =>
    {
        string member = path + suffix;
        return File.Exists(member) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(member))) + ":" + File.GetLastWriteTimeUtc(member).Ticks : "missing";
    }).ToArray();
    private static async Task<List<TextLine>> ReadAsync(IAsyncEnumerable<TextLine> source) { var rows = new List<TextLine>(); await foreach (var row in source) rows.Add(row); return rows; }
    private static async Task<List<Hit>> HitsAsync(IAsyncEnumerable<Hit> source) { var rows = new List<Hit>(); await foreach (var row in source) rows.Add(row); return rows; }
}
