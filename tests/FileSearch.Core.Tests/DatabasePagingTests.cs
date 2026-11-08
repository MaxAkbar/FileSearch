using System.Globalization;
using CSharpDB.Engine;
using FileSearch.Core.Databases;
using Microsoft.Data.Sqlite;

namespace FileSearch.Core.Tests;

public sealed class DatabasePagingTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchDatabasePagingTests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Table = "Data \"range\"";
    private static readonly long[] Keys = [long.MinValue, -900, -15, -1, 0, 2, 19, 87, 400, 5_000, 75_000, long.MaxValue];

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task SequentialPagesKeepExactOrdinalsAndSparseNativeKeys(DatabaseKind kind)
    {
        string path = await CreateKeyedAsync(kind);
        string fingerprint = DatabaseFiles.Fingerprint(path);
        await using var session = await DatabaseReadSession.OpenAsync(path, fingerprint, Token);
        for (int offset = 0; offset < Keys.Length; offset += 3)
        {
            var page = await session.ReadPageAsync(Table, offset + 1, 3, Token);
            Assert.Equal(Keys.Skip(offset).Take(3).Select(value => value.ToString(CultureInfo.InvariantCulture)), page.Rows.Select(row => row.Values[0]));
            Assert.Equal(Enumerable.Range(offset + 1, 3).Select(value => (long)value), page.Rows.Select(row => row.Number));
            Assert.Equal(offset + 3 < Keys.Length, page.HasMore);
            Assert.All(page.Rows, row => Assert.Contains(row.Values[0]!, row.Key));
        }
        Assert.Empty((await session.ReadPageAsync(Table, Keys.Length + 1, 3, Token)).Rows);
        Assert.Equal(fingerprint, DatabaseFiles.Fingerprint(path));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task ArbitraryJumpsThenNextPreviousAndDifferentPageSizesKeepExactRecords(DatabaseKind kind)
    {
        string path = await CreateKeyedAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        foreach (var (first, count) in new[] { (7, 2), (9, 2), (7, 2), (1, 3), (4, 3), (7, 3), (10, 3), (4, 2), (6, 4), (10, 2) })
        {
            var page = await session.ReadPageAsync(Table, first, count, Token);
            Assert.Equal(Keys.Skip(first - 1).Take(count).Select(value => value.ToString(CultureInfo.InvariantCulture)), page.Rows.Select(row => row.Values[0]));
            Assert.Equal(Enumerable.Range(first, Math.Min(count, Keys.Length - first + 1)).Select(value => (long)value), page.Rows.Select(row => row.Number));
            Assert.Equal(first - 1 + page.Rows.Count < Keys.Length, page.HasMore);
        }
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task NoKeyAndCompositeKeyTablesKeepTheSameOrderingAsAnUnpagedRead(DatabaseKind kind)
    {
        string path = await CreateAsync(kind,
            "CREATE TABLE NoKey (Value TEXT, Number INTEGER)",
            "INSERT INTO NoKey VALUES ('duplicate',9),('duplicate',2),('last',NULL),('first',-1)",
            "CREATE TABLE Composite (A TEXT, B INTEGER, Value TEXT, PRIMARY KEY(A,B))",
            "INSERT INTO Composite VALUES ('z',9,'last'),('a',2,'second'),('a',1,'first'),('m',3,'third')");
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        foreach (string table in new[] { "NoKey", "Composite" })
        {
            var full = await session.ReadPageAsync(table, 1, 10, Token);
            var rows = new List<DatabaseRow>();
            for (int first = 1; first <= 4; first += 2)
            {
                var page = await session.ReadPageAsync(table, first, 2, Token);
                rows.AddRange(page.Rows);
                Assert.Equal(first == 1, page.HasMore);
            }
            Assert.Equal(full.Rows.Select(Describe), rows.Select(Describe));
        }
    }

    [Fact]
    public async Task SqliteShadowedRowIdAliasesAndWithoutRowIdTablesRetainCorrectFallbackOrdering()
    {
        string path = await CreateAsync(DatabaseKind.Sqlite,
            "CREATE TABLE Shadowed (_rowid_ TEXT, rowid TEXT, oid TEXT, Id INTEGER PRIMARY KEY)",
            "INSERT INTO Shadowed VALUES ('native-looking','900','wrong',10),('same','1','different',-5),('last','2','value',99)",
            "CREATE TABLE Pair (A TEXT, B INTEGER, PRIMARY KEY(B,A)) WITHOUT ROWID",
            "INSERT INTO Pair VALUES ('z',2),('a',2),('last',1)");
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        Assert.All(session.Tables, table => Assert.Null(table.RowId));
        foreach (string table in new[] { "Shadowed", "Pair" })
        {
            var full = await session.ReadPageAsync(table, 1, 10, Token);
            var rows = new List<DatabaseRow>();
            for (int first = 1; first <= 3; first++) rows.AddRange((await session.ReadPageAsync(table, first, 1, Token)).Rows);
            Assert.Equal(full.Rows.Select(Describe), rows.Select(Describe));
        }
        Assert.Equal("-5", (await session.ReadPageAsync("Shadowed", 1, 1, Token)).Rows[0].Values[3]);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task CancellationAndSourceChangesCannotReturnBookmarkedRows(DatabaseKind kind)
    {
        string path = await CreateKeyedAsync(kind);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        await session.ReadPageAsync(Table, 1, 3, Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadPageAsync(Table, 4, 3, cancel.Token));
        Assert.Equal("-1", (await session.ReadPageAsync(Table, 4, 3, Token)).Rows[0].Values[0]);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadPageAsync(Table, 7, 3, Token));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.CSharpDb)]
    public async Task BookmarksAreScopedToTheSelectedTableAndEvictionPreservesRandomAccess(DatabaseKind kind)
    {
        var sql = new List<string> { "CREATE TABLE FirstTable (Id INTEGER PRIMARY KEY, Value TEXT)", "CREATE TABLE SecondTable (Id INTEGER PRIMARY KEY, Value TEXT)" };
        for (int row = 1; row <= 90; row++)
        {
            sql.Add($"INSERT INTO FirstTable VALUES ({row * 3},'first-{row}')");
            sql.Add($"INSERT INTO SecondTable VALUES ({row * 7},'second-{row}')");
        }
        string path = await CreateAsync(kind, [.. sql]);
        await using var session = await DatabaseReadSession.OpenAsync(path, null, Token);
        for (int row = 1; row <= 90; row++)
        {
            Assert.Equal($"first-{row}", (await session.ReadPageAsync("FirstTable", row, 1, Token)).Rows[0].Values[1]);
            Assert.Equal($"second-{row}", (await session.ReadPageAsync("SecondTable", row, 1, Token)).Rows[0].Values[1]);
        }
        foreach (int row in new[] { 2, 3, 76, 77, 1 })
            Assert.Equal($"first-{row}", (await session.ReadPageAsync("FirstTable", row, 1, Token)).Rows[0].Values[1]);
    }

    private Task<string> CreateKeyedAsync(DatabaseKind kind)
    {
        string keyType = kind == DatabaseKind.Sqlite ? "INTEGER" : "BIGINT";
        var sql = new List<string> { $"CREATE TABLE {DatabaseFiles.Quote(Table)} (Id {keyType} PRIMARY KEY, Value TEXT)" };
        foreach (long key in Keys.Reverse())
            sql.Add($"INSERT INTO {DatabaseFiles.Quote(Table)} VALUES ({key.ToString(CultureInfo.InvariantCulture)},'value')");
        return CreateAsync(kind, [.. sql]);
    }

    private async Task<string> CreateAsync(DatabaseKind kind, params string[] statements)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            foreach (string sql in statements) { command.CommandText = sql; await command.ExecuteNonQueryAsync(Token); }
        }
        else
        {
            await using var database = await Database.OpenAsync(path, Token);
            foreach (string sql in statements) { await using var result = await database.ExecuteAsync(sql, Token); }
        }
        return path;
    }

    private static string Describe(DatabaseRow row) => row.Number + ":" + row.Key + ":" + string.Join('|', row.Values);

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return ValueTask.CompletedTask;
    }
}
