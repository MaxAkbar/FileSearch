using System.Globalization;
using CSharpDB.Engine;
using Microsoft.Data.Sqlite;
using DbType = CSharpDB.Primitives.DbType;

namespace FileSearch.Core.Databases;

/// <summary>A bounded, forward-reading view of ordinary SQL tables in a private snapshot.</summary>
public sealed class DatabaseReadSession : IAsyncDisposable
{
    private static readonly string[] RowIdAliases = ["_rowid_", "rowid", "oid"];
    private readonly DatabaseSnapshot _snapshot;
    private SqliteConnection? _sqlite;
    private Database? _csharp;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private DatabaseReadSession(DatabaseSnapshot snapshot) => _snapshot = snapshot;
    public DatabaseKind Kind => _snapshot.Kind;
    public string Fingerprint => _snapshot.Fingerprint;
    internal string SnapshotPath => _snapshot.Path;
    public IReadOnlyList<DatabaseTable> Tables { get; private set; } = [];

    public static async Task<DatabaseReadSession> OpenAsync(string path, string? expectedFingerprint, CancellationToken token)
    {
        var session = new DatabaseReadSession(await DatabaseSnapshot.CreateAsync(path, expectedFingerprint, token).ConfigureAwait(false));
        try
        {
            if (session.Kind == DatabaseKind.Sqlite)
            {
                session._sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = session._snapshot.Path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2 }.ToString());
                await session._sqlite.OpenAsync(token).ConfigureAwait(false);
                using var settings = session._sqlite.CreateCommand();
                settings.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
                await settings.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                session.Tables = await session.ReadSqliteSchemaAsync(token).ConfigureAwait(false);
            }
            else
            {
                session._csharp = await Database.OpenAsync(session._snapshot.Path, token).ConfigureAwait(false);
                var tables = new List<DatabaseTable>();
                foreach (string name in session._csharp.GetTableNames().Where(name => !name.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if (tables.Count >= DatabaseFiles.MaxTables) throw new InvalidDataException("Database previews are limited to 256 user tables.");
                    var schema = session._csharp.GetTableSchema(name)!;
                    if (schema.Columns.Count > DatabaseFiles.MaxColumns) throw new InvalidDataException("Database previews are limited to 512 columns per table.");
                    var columns = schema.Columns.Select(column => new DatabaseColumn(column.Name, column.Type.ToString(), column.IsPrimaryKey)).ToArray();
                    string order = string.Join(',', columns.Where(column => column.IsPrimaryKey).Select(column => DatabaseFiles.Quote(column.Name)));
                    tables.Add(new DatabaseTable(name, columns, order));
                }
                session.Tables = tables;
            }
            session._snapshot.EnsureUnchanged();
            return session;
        }
        catch (Exception error)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (error is SqliteException or CSharpDB.Primitives.CSharpDbException)
                throw new InvalidDataException("The database could not be read. It may be encrypted, corrupt, or incompatible with this reader.", error);
            throw;
        }
    }

    private async Task<List<DatabaseTable>> ReadSqliteSchemaAsync(CancellationToken token)
    {
        var names = new List<(string Name, bool WithoutRowId)>();
        using (var command = _sqlite!.CreateCommand())
        {
            command.CommandText = "SELECT name,wr FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE BINARY";
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (names.Count >= DatabaseFiles.MaxTables) throw new InvalidDataException("Database previews are limited to 256 user tables.");
                names.Add((reader.GetString(0), reader.GetInt32(1) != 0));
            }
        }
        var tables = new List<DatabaseTable>();
        foreach (var (name, withoutRowId) in names)
        {
            using var command = _sqlite!.CreateCommand();
            command.CommandText = "SELECT name,type,pk FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid";
            command.Parameters.AddWithValue("$table", name);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var columns = new List<DatabaseColumn>();
            var keys = new SortedDictionary<int, string>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (columns.Count >= DatabaseFiles.MaxColumns) throw new InvalidDataException("Database previews are limited to 512 columns per table.");
                string column = reader.GetString(0);
                int key = reader.GetInt32(2);
                columns.Add(new DatabaseColumn(column, reader.GetString(1), key > 0));
                if (key > 0) keys.Add(key, column);
            }
            string? rowId = withoutRowId ? null : RowIdAliases.FirstOrDefault(alias => columns.All(column => !column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)));
            string order = rowId is not null ? DatabaseFiles.Quote(rowId) : string.Join(',', keys.Values.Select(DatabaseFiles.Quote));
            tables.Add(new DatabaseTable(name, columns, order, rowId));
        }
        return tables;
    }

    public async Task<DatabasePage> ReadPageAsync(string tableName, long firstRow, int count, CancellationToken token)
    {
        if (firstRow is < 1 or > DatabaseFiles.MaxRows || count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(firstRow));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _snapshot.EnsureUnchanged();
            var table = Tables.FirstOrDefault(table => table.Name == tableName) ?? throw new InvalidDataException("The selected table does not exist.");
            count = (int)Math.Min(count, DatabaseFiles.MaxRows - firstRow + 1);
            if (table.Columns.Count == 0) return new DatabasePage([], false, false);
            var page = Kind == DatabaseKind.Sqlite ? await ReadSqlitePageAsync(table, firstRow, count, token).ConfigureAwait(false) :
                await ReadCSharpPageAsync(table, firstRow, count, token).ConfigureAwait(false);
            _snapshot.EnsureUnchanged();
            return page;
        }
        catch (Exception error) when (error is SqliteException or CSharpDB.Primitives.CSharpDbException)
        {
            throw new InvalidDataException("The database table could not be read. Use a valid stable backup or a compatible database file.", error);
        }
        finally { _gate.Release(); }
    }

    private async Task<DatabasePage> ReadSqlitePageAsync(DatabaseTable table, long firstRow, int count, CancellationToken token)
    {
        using var command = _sqlite!.CreateCommand();
        string Projection(DatabaseColumn column)
        {
            string name = DatabaseFiles.Quote(column.Name);
            return $"CASE WHEN typeof({name})='blob' THEN NULL ELSE substr(CAST({name} AS TEXT),1,{DatabaseFiles.MaxCellCharacters + 1}) END";
        }
        string columns = string.Join(',', table.Columns.Select(Projection));
        if (table.RowId is not null) columns = DatabaseFiles.Quote(table.RowId) + "," + columns;
        command.CommandText = $"SELECT {columns} FROM {DatabaseFiles.Quote(table.Name)}" + (table.OrderBy.Length == 0 ? "" : " ORDER BY " + table.OrderBy) + " LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", Math.Min(count, DatabaseFiles.MaxRows - firstRow + 1) + 1);
        command.Parameters.AddWithValue("$offset", firstRow - 1);
        using var cancellation = token.Register(command.Cancel);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<DatabaseRow>();
        bool truncated = false;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (rows.Count == count) return new DatabasePage(rows, true, truncated);
            var values = new string?[table.Columns.Count];
            for (int index = 0; index < values.Length; index++)
            {
                int field = index + (table.RowId is null ? 0 : 1);
                string? value = reader.IsDBNull(field) ? null : reader.GetString(field);
                if (value?.Length > DatabaseFiles.MaxCellCharacters) { value = value[..DatabaseFiles.MaxCellCharacters] + "…"; truncated = true; }
                values[index] = value;
            }
            string key = table.RowId is null ? PrimaryKey(table, values) : "rowid=" + reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
            rows.Add(new DatabaseRow(firstRow + rows.Count, key, values));
        }
        return new DatabasePage(rows, false, truncated);
    }

    private async Task<DatabasePage> ReadCSharpPageAsync(DatabaseTable table, long firstRow, int count, CancellationToken token)
    {
        string projection = string.Join(',', table.Columns.Select(column => column.Type == nameof(DbType.Blob) ? "NULL" : DatabaseFiles.Quote(column.Name)));
        string sql = $"SELECT {projection} FROM {DatabaseFiles.Quote(table.Name)}" + (table.OrderBy.Length == 0 ? "" : " ORDER BY " + table.OrderBy) +
            string.Create(CultureInfo.InvariantCulture, $" LIMIT {Math.Min(count, DatabaseFiles.MaxRows - firstRow + 1) + 1} OFFSET {firstRow - 1}");
        await using var reader = await _csharp!.ExecuteAsync(sql, token).ConfigureAwait(false);
        var rows = new List<DatabaseRow>();
        bool truncated = false;
        while (await reader.MoveNextAsync(token).ConfigureAwait(false))
        {
            if (rows.Count == count) return new DatabasePage(rows, true, truncated);
            var values = new string?[table.Columns.Count];
            for (int index = 0; index < values.Length; index++)
            {
                var field = reader.Current[index];
                string? value = field.IsNull || field.Type == DbType.Blob ? null : field.ToString();
                if (value?.Length > DatabaseFiles.MaxCellCharacters) { value = value[..DatabaseFiles.MaxCellCharacters] + "…"; truncated = true; }
                values[index] = value;
            }
            rows.Add(new DatabaseRow(firstRow + rows.Count, PrimaryKey(table, values), values));
        }
        return new DatabasePage(rows, false, truncated);
    }

    private static string PrimaryKey(DatabaseTable table, string?[] values) => string.Join(", ", table.Columns.Select((column, index) =>
        column.IsPrimaryKey ? column.Name + "=" + (values[index] ?? "NULL") : "").Where(value => value.Length > 0));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_sqlite is not null) await _sqlite.DisposeAsync().ConfigureAwait(false);
            if (_csharp is not null) await _csharp.DisposeAsync().ConfigureAwait(false);
        }
        finally { _snapshot.Dispose(); _gate.Release(); }
    }
}
