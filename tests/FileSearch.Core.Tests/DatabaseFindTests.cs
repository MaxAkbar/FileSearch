using System.Security.Cryptography;
using System.Text;
using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Databases;
using Microsoft.Data.Sqlite;

namespace FileSearch.Core.Tests;

public sealed class DatabaseFindTests : IAsyncDisposable
{
    private const string Table = "Find \"cells\"", ValueColumn = "Value \"text\"";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearch.DatabaseFind", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DatabaseFindTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50, CancellationToken.None); }
        }
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task FindBeyondLoadedPagePreservesTheExactQuotedSourceLocation(DatabaseKind kind)
    {
        string path = await DatabaseTestFiles.CreateAsync(_root, "rows.db", kind, 750, Token);
        string source = SourceState(path);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        Assert.Equal(250, (await session.ReadPageAsync(DatabaseTestFiles.TableName, 1, 250, Token)).Rows.Count);
        var result = await session.FindAsync(DatabaseTestFiles.TableName, new("東京 O'Brien"), Token);
        var match = Assert.Single(result.Matches);
        Assert.True(result.IsComplete);
        Assert.Equal(750, result.RowsScanned);
        Assert.Equal(750, match.Location.RowNumber);
        Assert.Equal(DatabaseTestFiles.TableName, match.Location.Table);
        Assert.Equal("Notes", match.Location.Column);
        Assert.Equal(kind, match.Location.Engine);
        Assert.Equal(session.Fingerprint, match.Location.Fingerprint);
        var page = await session.ReadPageAsync(DatabaseTestFiles.TableName, 501, 250, Token);
        Assert.Equal(page.Rows[^1].Key, match.Location.RowKey);
        Assert.Equal(new[] { "東京", "O'Brien" }, match.Highlights.Select(span => page.Rows[^1].Values[2]!.Substring(span.Start, span.Length)));
        Assert.Equal(source, SourceState(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task MatchingModesStayWithinOneScalarCellAndRespectColumnCaseAndLiteralText(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind);
        string before = SourceState(path);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var all = await session.FindAsync(Table, new("john smith"), Token);
        Assert.Equal(new long[] { 1, 3 }, all.Matches.Select(match => match.Location.RowNumber));
        Assert.All(all.Matches, match => Assert.Equal(ValueColumn, match.Location.Column));
        var any = await session.FindAsync(Table, new("john smith", DatabaseFindMode.AnyTerm), Token);
        Assert.Equal(4, any.Matches.Count);
        Assert.Contains(any.Matches, match => match.Location.RowNumber == 2 && match.Location.Column == "Other");
        var phrase = await session.FindAsync(Table, new("john smith", DatabaseFindMode.Phrase), Token);
        Assert.Equal(1, Assert.Single(phrase.Matches).Location.RowNumber);
        Assert.Empty((await session.FindAsync(Table, new("\"john smith\"", DatabaseFindMode.Phrase), Token)).Matches);
        Assert.Equal(1, Assert.Single((await session.FindAsync(Table, new("john smith", MatchCase: true), Token)).Matches).Location.RowNumber);
        Assert.Empty((await session.FindAsync(Table, new("john smith", Column: "Other"), Token)).Matches);
        Assert.Single((await session.FindAsync(Table, new("smith", Column: "Other"), Token)).Matches);
        Assert.Single((await session.FindAsync(Table, new("OR AND", DatabaseFindMode.Phrase), Token)).Matches);
        Assert.Empty((await session.FindAsync(Table, new("john OR smith"), Token)).Matches);
        Assert.True(all.IsComplete);
        Assert.Equal(before, SourceState(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task HighlightSpansMergeOverlapsUseUtf16CoordinatesAndPreservePreviewKeys(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var banana = Assert.Single((await session.FindAsync(Table, new("ana", Column: ValueColumn), Token)).Matches);
        var row = (await session.ReadPageAsync(Table, banana.Location.RowNumber, 1, Token)).Rows[0];
        Assert.Equal("anana", row.Values[1]!.Substring(banana.Highlights[0].Start, banana.Highlights[0].Length));
        Assert.Single(banana.Highlights);
        Assert.Equal(row.Key, banana.Location.RowKey);
        var unicode = Assert.Single((await session.FindAsync(Table, new("東京", Column: ValueColumn), Token)).Matches);
        row = (await session.ReadPageAsync(Table, unicode.Location.RowNumber, 1, Token)).Rows[0];
        Assert.Equal("東京", row.Values[1]!.Substring(unicode.Highlights[0].Start, unicode.Highlights[0].Length));
        Assert.Equal(row.Key, unicode.Location.RowKey);
        Assert.Contains("10", row.Key);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task RowCellCharacterAndMatchBudgetsAreExplicitAndNeverClaimFullCoverage(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var rows = await session.FindAsync(Table, new("never-present", MaxRows: 1), Token);
        Assert.False(rows.IsComplete); Assert.Equal(1, rows.RowsScanned); Assert.Contains("Row scan limit", rows.LimitReason);
        var cells = await session.FindAsync(Table, new("never-present", Column: ValueColumn, MaxCells: 1), Token);
        Assert.False(cells.IsComplete); Assert.Equal(1, cells.CellsScanned); Assert.Contains("Cell scan limit", cells.LimitReason);
        var chars = await session.FindAsync(Table, new("john", Column: ValueColumn, MaxCharacters: 3), Token);
        Assert.False(chars.IsComplete); Assert.Equal(3, chars.CharactersScanned); Assert.Empty(chars.Matches);
        Assert.Contains("Character scan limit", chars.LimitReason);
        var matches = await session.FindAsync(Table, new("john", MaxMatches: 1), Token);
        Assert.False(matches.IsComplete); Assert.Single(matches.Matches); Assert.Contains("Match limit", matches.LimitReason);
        var exactRows = await session.FindAsync(Table, new("never-present", MaxRows: 5), Token);
        Assert.True(exactRows.IsComplete); Assert.Null(exactRows.LimitReason);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task LongCellSuffixesAreNotSearchedAndSyntheticPreviewEllipsesDoNotMatch(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind, new string('x', DatabaseFiles.MaxCellCharacters) + "unsearched-suffix");
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var result = await session.FindAsync(Table, new("unsearched-suffix", Column: ValueColumn), Token);
        Assert.Empty(result.Matches); Assert.False(result.IsComplete); Assert.True(result.ValuesTruncated);
        Assert.Contains("Cell text is limited", result.LimitReason);
        Assert.Empty((await session.FindAsync(Table, new("…", Column: ValueColumn), Token)).Matches);
        var prefix = await session.FindAsync(Table, new("xxx", Column: ValueColumn), Token);
        var match = Assert.Single(prefix.Matches);
        Assert.Single(match.Highlights);
        Assert.Equal(0, match.Highlights[0].Start);
        Assert.Equal(DatabaseFiles.MaxCellCharacters, match.Highlights[0].Length);
        Assert.False(match.HighlightsTruncated);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task UnselectedLargeValuesDoNotConsumeTheSelectedColumnBudgetOrMarkItsCoveragePartial(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind, new string('x', DatabaseFiles.MaxCellCharacters * 4));
        string before = SourceState(path);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var result = await session.FindAsync(Table, new("alpha", Column: "Other", MaxCharacters: 64), Token);
        Assert.True(result.IsComplete);
        Assert.False(result.ValuesTruncated);
        Assert.Null(result.LimitReason);
        Assert.Equal(5, result.CellsScanned);
        Assert.InRange(result.CharactersScanned, 1, 64);
        var match = Assert.Single(result.Matches);
        Assert.Equal("Other", match.Location.Column);
        Assert.Equal((await session.ReadPageAsync(Table, 1, 1, Token)).Rows[0].Key, match.Location.RowKey);
        Assert.Equal(before, SourceState(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task HighlightStorageIsBoundedWithoutChangingWhetherTheCellMatches(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind, string.Concat(Enumerable.Repeat("x ", 300)));
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var result = await session.FindAsync(Table, new("x", Column: ValueColumn), Token);
        var match = Assert.Single(result.Matches);
        Assert.True(result.IsComplete);
        Assert.True(match.HighlightsTruncated);
        Assert.Equal(256, match.Highlights.Count);
        Assert.All(match.Highlights, span => Assert.Equal(1, span.Length));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task EmptyMissingAndInvalidFindRequestsDoNotDamageTheReadSession(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var empty = await session.FindAsync("EmptyTable", new("value"), Token);
        Assert.True(empty.IsComplete); Assert.Empty(empty.Matches); Assert.Equal(0, empty.RowsScanned);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.FindAsync("not-a-table\"; DROP TABLE Data;--", new("value"), Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.FindAsync(Table, new("value", Column: "not-a-column\""), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => session.FindAsync(Table, new("  "), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => session.FindAsync(Table, new(new string('x', 2_049)), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => session.FindAsync(Table, new(string.Join(" ", Enumerable.Range(0, 65))), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.FindAsync(Table, new("value", MaxRows: 0), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.FindAsync(Table, new("value", MaxMatches: 1_001), Token));
        Assert.Equal(5, (await session.ReadPageAsync(Table, 1, 250, Token)).Rows.Count);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task CancellationAndChangedSourcesRejectResultsAndLeaveTheSessionUsable(DatabaseKind kind)
    {
        string path = await CreateCellsAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FindAsync(Table, new("john"), cancellation.Token));
        Assert.NotEmpty((await session.FindAsync(Table, new("john"), Token)).Matches);
        Assert.Equal(5, (await session.ReadPageAsync(Table, 1, 250, Token)).Rows.Count);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.FindAsync(Table, new("john"), Token));
    }

    [Fact]
    public async Task SqliteCompositeKeysAndWithoutRowIdKeepPageAndFindOrdering()
    {
        string path = Path.Combine(_root, "keys.sqlite");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Pair (A TEXT, B INTEGER, Value TEXT, PRIMARY KEY(A,B)) WITHOUT ROWID; INSERT INTO Pair VALUES ('a',2,'needle'),('a',1,'first');";
            await command.ExecuteNonQueryAsync(Token);
        }
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        var match = Assert.Single((await session.FindAsync("Pair", new("needle", Column: "Value"), Token)).Matches);
        Assert.Equal(2, match.Location.RowNumber);
        Assert.Equal((await session.ReadPageAsync("Pair", 2, 1, Token)).Rows[0].Key, match.Location.RowKey);
        Assert.Contains("A=a", match.Location.RowKey); Assert.Contains("B=2", match.Location.RowKey);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task CommittedWalRowsAreFoundWithoutChangingOriginalBytesOrSidecars(DatabaseKind kind)
    {
        string writerPath = Path.Combine(_root, "writer.db"), source = Path.Combine(_root, "source.db");
        if (kind == DatabaseKind.Sqlite)
        {
            await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = writerPath, Pooling = false }.ToString());
            await writer.OpenAsync(Token);
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Data(Value TEXT); INSERT INTO Data VALUES ('committed needle');";
            await command.ExecuteNonQueryAsync(Token);
            File.Copy(writerPath, source); File.Copy(writerPath + "-wal", source + "-wal");
            await AssertWalFindAsync(source);
        }
        else
        {
            await using var writer = await Database.OpenAsync(writerPath, Token);
            await using (var query = await writer.ExecuteAsync("CREATE TABLE Data(Id INTEGER PRIMARY KEY, Value TEXT)", Token)) { }
            await using (var query = await writer.ExecuteAsync("INSERT INTO Data VALUES (9,'committed needle')", Token)) { }
            File.Copy(writerPath, source); File.Copy(writerPath + ".wal", source + ".wal");
            await AssertWalFindAsync(source);
        }
    }

    private static async Task AssertWalFindAsync(string path)
    {
        string before = SourceState(path);
        string snapshot;
        await using (var session = await DatabaseReadSession.OpenAsync(path, null, Token))
        {
            snapshot = session.SnapshotPath;
            var match = Assert.Single((await session.FindAsync("Data", new("needle"), Token)).Matches);
            Assert.Equal("Value", match.Location.Column);
            Assert.Equal(session.Fingerprint, match.Location.Fingerprint);
        }
        Assert.False(File.Exists(snapshot));
        Assert.Equal(before, SourceState(path));
    }

    private async Task<string> CreateCellsAsync(DatabaseKind kind, string? firstValue = null)
    {
        string path = Path.Combine(_root, "cells.db");
        const string schema = "CREATE TABLE \"Find \"\"cells\"\"\" (Id INTEGER PRIMARY KEY, \"Value \"\"text\"\"\" TEXT, Other TEXT, Payload BLOB)";
        (long Id, string? Value, string Other)[] rows =
        [
            (10, firstValue ?? "😀 john smith 東京", "alpha"), (20, "john", "smith"),
            (30, "SMITH JOHN", "literal OR AND"), (40, null, "notfound"), (50, "banana ababa", "ordinary"),
        ];
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = schema + "; CREATE TABLE EmptyTable(Value TEXT);";
            await command.ExecuteNonQueryAsync(Token);
            foreach (var row in rows)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO \"Find \"\"cells\"\"\" VALUES ($id,$value,$other,$blob)";
                command.Parameters.AddWithValue("$id", row.Id); command.Parameters.AddWithValue("$value", (object?)row.Value ?? DBNull.Value);
                command.Parameters.AddWithValue("$other", row.Other); command.Parameters.AddWithValue("$blob", Encoding.UTF8.GetBytes("john smith 東京"));
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            await using (var query = await database.ExecuteAsync(schema, Token)) { }
            await using (var query = await database.ExecuteAsync("CREATE TABLE EmptyTable(Value TEXT)", Token)) { }
            var batch = database.PrepareInsertBatch(Table);
            foreach (var row in rows)
                batch.AddRow(DbValue.FromInteger(row.Id), row.Value is null ? DbValue.Null : DbValue.FromText(row.Value),
                    DbValue.FromText(row.Other), DbValue.FromBlob(Encoding.UTF8.GetBytes("john smith 東京")));
            await batch.ExecuteAsync(Token);
        }
        return path;
    }

    private static string SourceState(string path) => string.Join('|', new[] { "", "-wal", "-shm", "-journal", ".wal", ".shm" }.Select(suffix =>
    {
        string member = path + suffix;
        return File.Exists(member) ? suffix + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(member))) : suffix + ":missing";
    }));
}
