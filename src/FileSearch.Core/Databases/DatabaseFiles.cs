using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Databases;

public enum DatabaseKind { Sqlite, CSharpDb }

public sealed record DatabaseLocation(DatabaseKind Engine, string Table, long RowNumber, string Column,
    string RowKey, string Fingerprint);
public sealed record DatabaseColumn(string Name, string Type, bool IsPrimaryKey = false);
public sealed record DatabaseTable(string Name, IReadOnlyList<DatabaseColumn> Columns, string OrderBy, string? RowId = null);
public sealed record DatabaseRow(long Number, string Key, IReadOnlyList<string?> Values);
public sealed record DatabasePage(IReadOnlyList<DatabaseRow> Rows, bool HasMore, bool ValuesTruncated);

public static class DatabaseFiles
{
    public static IReadOnlyList<string> Extensions { get; } = [".db", ".sqlite", ".sqlite3", ".db3", ".csharpdb", ".csdb", ".cdb"];
    public const long MaxSnapshotBytes = 256L * 1024 * 1024;
    public const int MaxTables = 256, MaxColumns = 512, MaxRows = 100_000, MaxCells = 250_000;
    public const int MaxCellCharacters = 16_384, MaxSearchCharacters = 16 * 1024 * 1024;

    public static bool IsDatabase(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool HasJournal(string path) => File.Exists(path + ".wal") || File.Exists(path + "-wal");
    public static string? GetJournalDatabase(string path)
    {
        string? parent = path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ? path[..^4] :
            path.EndsWith(".wal", StringComparison.OrdinalIgnoreCase) ? path[..^4] : null;
        return parent is not null && IsDatabase(parent) ? parent : null;
    }
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static DatabaseKind Detect(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        int length = file.Read(header);
        if (length == 16 && header.SequenceEqual("SQLite format 3\0"u8)) return DatabaseKind.Sqlite;
        if (length >= 4 && header[..4].SequenceEqual("CSDB"u8)) return DatabaseKind.CSharpDb;
        throw new InvalidDataException("This file is not a supported SQLite or CSharpDB database.");
    }

    public static string Fingerprint(string path)
    {
        var stamp = new StringBuilder();
        foreach (string member in new[] { path, path + "-wal", path + ".wal", path + "-journal" })
        {
            var info = new FileInfo(member);
            stamp.Append(info.Exists ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}") : "missing").Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
    }
}

/// <summary>The only source-file operations are read access, metadata reads, and bounded copies.</summary>
internal sealed class DatabaseSnapshot : IDisposable
{
    private readonly string _directory;
    private DatabaseSnapshot(string directory, string source, DatabaseKind kind)
    {
        _directory = directory; Source = source; Kind = kind;
        Path = System.IO.Path.Combine(directory, "snapshot.db");
    }
    public string Source { get; }
    public string Path { get; }
    public DatabaseKind Kind { get; }
    public string Fingerprint { get; private set; } = "";

    public static async Task<DatabaseSnapshot> CreateAsync(string path, string? expected, DatabaseOpenTimings? timings,
        IProgress<DatabasePreparationProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(path);
        if (string.Equals(fullPath, System.IO.Path.GetFullPath(new FileIndexOptions().DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FileSearch's own index database is excluded from database-content search and preview.");
        var kind = DatabaseFiles.Detect(fullPath);
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileSearchDatabasePreview", Guid.NewGuid().ToString("N"));
        var snapshot = new DatabaseSnapshot(directory, fullPath, kind);
        var handles = new List<FileStream>();
        try
        {
            string before = DatabaseFiles.Fingerprint(fullPath);
            if (expected is not null && expected != before) throw new InvalidDataException("The database changed since this hit was found. Refresh the index or rerun the search.");
            // Refuse hot rollback journals: copying an interrupted transaction is not a read-only recovery operation.
            if (File.Exists(fullPath + "-journal")) throw new InvalidDataException("The database has a rollback journal. Close its application cleanly or search a stable backup.");
            var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            handles.Add(source);
            string walSuffix = kind == DatabaseKind.Sqlite ? "-wal" : ".wal";
            if (File.Exists(fullPath + walSuffix)) handles.Add(new FileStream(fullPath + walSuffix, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous));
            long totalBytes = handles.Sum(file => file.Length);
            if (totalBytes > DatabaseFiles.MaxSnapshotBytes) throw new InvalidDataException("Database snapshots, including WAL data, are limited to 256 MB.");
            progress?.Report(new(DatabasePreparationStage.Snapshot, 0, totalBytes));
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            long copyStarted = DatabaseOpenTimings.Start(timings);
            long remaining = DatabaseFiles.MaxSnapshotBytes;
            long copied = 0, reported = 0;
            var buffer = new byte[64 * 1024];
            for (int index = 0; index < handles.Count; index++)
            {
                string target = snapshot.Path + (index == 0 ? "" : walSuffix);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                int read;
                while ((read = await handles[index].ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if ((remaining -= read) < 0) throw new InvalidDataException("The database grew past the 256 MB snapshot limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    copied += read;
                    if (progress is not null && (copied - reported >= 1024 * 1024 || copied == totalBytes))
                    {
                        progress.Report(new(DatabasePreparationStage.Snapshot, copied, totalBytes));
                        reported = copied;
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            if (timings is not null)
            {
                timings.CopyTicks = DatabaseOpenTimings.Elapsed(copyStarted);
                timings.MainBytes = handles[0].Position;
                timings.WalBytes = handles.Count > 1 ? handles[1].Position : 0;
            }
            snapshot.Fingerprint = DatabaseFiles.Fingerprint(fullPath);
            if (before != snapshot.Fingerprint) throw new InvalidDataException("The database changed while the snapshot was being read. Close its application or search a stable backup.");
            token.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
        finally { foreach (var handle in handles) await handle.DisposeAsync().ConfigureAwait(false); }
    }

    public void EnsureUnchanged()
    {
        if (DatabaseFiles.Fingerprint(Source) != Fingerprint) throw new InvalidDataException("The database changed. Refresh the index or rerun the search before previewing this record.");
    }
    public void Dispose()
    {
        // Engines may create these sidecars only beside our generated snapshot.
        foreach (string suffix in new[] { "-wal", "-shm", "-journal", ".wal", ".shm", "" })
            try { File.Delete(Path + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
