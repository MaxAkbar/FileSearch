using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using DbType = CSharpDB.Primitives.DbType;

namespace FileSearch.Core.Databases;

public sealed partial class DatabaseReadSession
{
    /// <summary>
    /// Scans the selected snapshot table once, using the same row ordering as
    /// preview pages. Terms match within one scalar cell, never across columns.
    /// </summary>
    public async Task<DatabaseFindResult> FindAsync(string tableName, DatabaseFindOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var matcher = new DatabaseFindMatcher(options);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _snapshot.EnsureUnchanged();
            var table = Tables.FirstOrDefault(table => table.Name == tableName) ?? throw new InvalidDataException("The selected table does not exist.");
            int selectedColumn = options.Column is null ? -1 : table.Columns.ToList().FindIndex(column => column.Name == options.Column);
            if (options.Column is not null && selectedColumn < 0) throw new InvalidDataException("The selected column does not exist.");
            var matches = new List<DatabaseCellMatch>();
            var reasons = new List<string>();
            int rowsScanned = 0, cellsScanned = 0, charactersScanned = 0;
            bool valuesTruncated = false, stopped = false;
            if (table.Columns.Count > 0)
            {
                await foreach (var row in ReadFindRowsAsync(table, options.MaxRows + 1, selectedColumn, token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (rowsScanned == options.MaxRows) { reasons.Add($"Row scan limit reached ({options.MaxRows:n0})."); break; }
                    rowsScanned++;
                    if ((rowsScanned & 255) == 0) _snapshot.EnsureUnchanged();
                    int firstColumn = selectedColumn < 0 ? 0 : selectedColumn;
                    int lastColumn = selectedColumn < 0 ? row.Values.Length : selectedColumn + 1;
                    for (int columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.Values[columnIndex] is not { } value) continue;
                        if (cellsScanned == options.MaxCells) { reasons.Add($"Cell scan limit reached ({options.MaxCells:n0})."); stopped = true; break; }
                        if (value.Length > DatabaseFiles.MaxCellCharacters)
                        {
                            value = value[..DatabaseFiles.MaxCellCharacters];
                            valuesTruncated = true;
                        }
                        int available = options.MaxCharacters - charactersScanned;
                        bool characterLimit = value.Length > available;
                        if (characterLimit) value = value[..available];
                        if (value.Length > 0 || !characterLimit)
                        {
                            cellsScanned++;
                            charactersScanned += value.Length;
                            if (matcher.Match(value, token) is { } match)
                            {
                                matches.Add(new DatabaseCellMatch(new DatabaseLocation(Kind, table.Name, row.Number,
                                    table.Columns[columnIndex].Name, row.Key, Fingerprint), match.Spans, match.Truncated));
                                if (matches.Count == options.MaxMatches)
                                { reasons.Add($"Match limit reached ({options.MaxMatches:n0})."); stopped = true; }
                            }
                        }
                        if (characterLimit) { reasons.Add($"Character scan limit reached ({options.MaxCharacters:n0})."); stopped = true; }
                        if (stopped) break;
                    }
                    if (stopped) break;
                }
            }
            if (valuesTruncated) reasons.Add($"Cell text is limited to {DatabaseFiles.MaxCellCharacters:n0} characters; longer suffixes were not searched.");
            token.ThrowIfCancellationRequested();
            _snapshot.EnsureUnchanged();
            return new DatabaseFindResult(matches, rowsScanned, cellsScanned, charactersScanned, reasons.Count == 0,
                reasons.Count == 0 ? null : string.Join(" ", reasons), valuesTruncated);
        }
        catch (Exception error) when (error is SqliteException or CSharpDB.Primitives.CSharpDbException)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException("The database table could not be searched. Use a valid stable backup or a compatible database file.", error);
        }
        finally { _gate.Release(); }
    }

    private async IAsyncEnumerable<FindRow> ReadFindRowsAsync(DatabaseTable table, int count, int selectedColumn,
        [EnumeratorCancellation] CancellationToken token)
    {
        // A selected-column search still needs keys, but not other table values.
        // Keep original schema indexes so locations and preview-column mapping agree.
        int[] projectedColumns = Enumerable.Range(0, table.Columns.Count)
            .Where(index => selectedColumn < 0 || index == selectedColumn || table.Columns[index].IsPrimaryKey).ToArray();
        if (Kind == DatabaseKind.Sqlite)
        {
            using var command = _sqlite!.CreateCommand();
            string Projection(DatabaseColumn column)
            {
                string name = DatabaseFiles.Quote(column.Name);
                return $"CASE WHEN typeof({name})='blob' THEN NULL ELSE substr(CAST({name} AS TEXT),1,{DatabaseFiles.MaxCellCharacters + 1}) END";
            }
            string columns = string.Join(',', projectedColumns.Select(index => Projection(table.Columns[index])));
            if (table.RowId is not null) columns = DatabaseFiles.Quote(table.RowId) + "," + columns;
            command.CommandText = $"SELECT {columns} FROM {DatabaseFiles.Quote(table.Name)}" +
                (table.OrderBy.Length == 0 ? "" : " ORDER BY " + table.OrderBy) + " LIMIT $limit";
            command.Parameters.AddWithValue("$limit", count);
            using var cancellation = token.Register(command.Cancel);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            long number = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var values = new string?[table.Columns.Count];
                for (int index = 0; index < projectedColumns.Length; index++)
                {
                    int field = index + (table.RowId is null ? 0 : 1);
                    values[projectedColumns[index]] = reader.IsDBNull(field) ? null : reader.GetString(field);
                }
                string key = table.RowId is null ? FindPrimaryKey(table, values) : "rowid=" + reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
                yield return new FindRow(++number, key, values);
            }
        }
        else
        {
            string projection = string.Join(',', projectedColumns.Select(index => table.Columns[index].Type == nameof(DbType.Blob)
                ? "NULL" : DatabaseFiles.Quote(table.Columns[index].Name)));
            string sql = $"SELECT {projection} FROM {DatabaseFiles.Quote(table.Name)}" +
                (table.OrderBy.Length == 0 ? "" : " ORDER BY " + table.OrderBy) +
                string.Create(CultureInfo.InvariantCulture, $" LIMIT {count}");
            await using var reader = await _csharp!.ExecuteAsync(sql, token).ConfigureAwait(false);
            long number = 0;
            while (await reader.MoveNextAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var values = new string?[table.Columns.Count];
                for (int index = 0; index < projectedColumns.Length; index++)
                {
                    var field = reader.Current[index];
                    string? value = field.IsNull || field.Type == DbType.Blob ? null : field.ToString();
                    values[projectedColumns[index]] = value?.Length > DatabaseFiles.MaxCellCharacters + 1 ? value[..(DatabaseFiles.MaxCellCharacters + 1)] : value;
                }
                yield return new FindRow(++number, FindPrimaryKey(table, values), values);
            }
        }
    }

    private static string FindPrimaryKey(DatabaseTable table, string?[] values) => PrimaryKey(table,
        values.Select(value => value?.Length > DatabaseFiles.MaxCellCharacters ? value[..DatabaseFiles.MaxCellCharacters] + "…" : value).ToArray());

    private sealed record FindRow(long Number, string Key, string?[] Values);
}
