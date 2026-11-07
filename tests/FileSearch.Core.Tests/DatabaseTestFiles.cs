using CSharpDB.Engine;
using FileSearch.Core.Databases;
using Microsoft.Data.Sqlite;

namespace FileSearch.Core.Tests;

internal static class DatabaseTestFiles
{
    internal const string TableName = "Customer \"records\"";
    internal static async Task<string> CreateAsync(string root, string name, DatabaseKind kind, int rows, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        const string schema = "CREATE TABLE \"Customer \"\"records\"\"\" (\"Id\" INTEGER PRIMARY KEY, \"Display Name\" TEXT, \"Notes\" TEXT, \"Payload\" BLOB)";
        string Insert(int row) => $"INSERT INTO \"Customer \"\"records\"\"\" VALUES ({row},'Person {row}','" + (row == rows ? "needle 東京 O''Brien\nEnd of record" : "Ordinary record") + "',NULL)";
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = schema; await command.ExecuteNonQueryAsync(token);
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            for (int row = 1; row <= rows; row++) { command.CommandText = Insert(row); await command.ExecuteNonQueryAsync(token); }
            transaction.Commit();
            command.Transaction = null; command.CommandText = "CREATE TABLE EmptyTable (Value TEXT); CREATE VIEW UnqueriedView AS SELECT load_extension('do-not-load') AS Value";
            await command.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var database = await Database.OpenAsync(path, token);
            await using (var query = await database.ExecuteAsync(schema, token)) { }
            for (int row = 1; row <= rows; row++) { await using var query = await database.ExecuteAsync(Insert(row), token); }
            await using (var query = await database.ExecuteAsync("CREATE TABLE EmptyTable (Value TEXT)", token)) { }
        }
        return path;
    }
}
