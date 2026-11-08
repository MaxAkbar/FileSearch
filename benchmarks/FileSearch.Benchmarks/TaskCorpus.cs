using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharpDB.Engine;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using Microsoft.Data.Sqlite;

namespace FileSearch.Benchmarks;

// Generated, non-private files: no direct-seeded metadata and no external downloads.
internal static class TaskCorpus
{
    public const string Table = "Customer \"records\"";
    public const string DatabaseMarker = "renewal 東京 O'Brien";

    public static async Task<TaskManifest> CreateAsync(string root, BenchmarkProfile profile, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var tasks = new List<BaselineTask>();
        void TextTask(string id, string file, string text, string query, QueryMode mode = QueryMode.PlainText,
            SearchTarget target = SearchTarget.Content)
        {
            File.WriteAllText(Path.Combine(root, file), text, Encoding.UTF8);
            tasks.Add(new(id, query, file, mode, target));
        }

        TextTask("known-file", "renewal-agreement.txt", "Annual renewal agreement for the Northwind account.", "renewal-agreement", target: SearchTarget.FileNames);
        TextTask("phrase", "meeting.txt", "The annual renewal was approved after review.\nThe annual budget is separate.", "annual renewal");
        TextTask("unicode", "東京-café.txt", "Résumé approved in 東京 for café operations.", "東京");
        TextTask("boolean", "source.cs", "// invoice approval\n// renewal approved", "invoice AND approval", QueryMode.Boolean);
        BenchmarkCorpusGenerator.CreateDocx(Path.Combine(root, "contract.docx"), "The service contract includes a ninety day cancellation period.");
        tasks.Add(new("word", "ninety day cancellation", "contract.docx"));
        BenchmarkCorpusGenerator.CreateXlsx(Path.Combine(root, "budget.xlsx"), "regional forecast approval");
        tasks.Add(new("spreadsheet", "regional forecast", "budget.xlsx"));
        BenchmarkCorpusGenerator.CreateSimplePdf(Path.Combine(root, "policy.pdf"), "Travel policy permits refundable reservations.");
        tasks.Add(new("pdf", "refundable reservations", "policy.pdf", Page: 1));
        TextTask("mail", "message.eml", "From: alex@example.invalid\r\nTo: team@example.invalid\r\nSubject: Service renewal\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nThe supplier accepted the revised delivery schedule.\r\n", "revised delivery schedule");
        using (var zip = ZipFile.Open(Path.Combine(root, "policies.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("handbook/expenses.txt").Open());
            writer.Write("Reimbursement requires the original receipt.");
        }
        tasks.Add(new("archive", "original receipt", "policies.zip", Member: "handbook/expenses.txt"));
        TextTask("no-match", "unrelated.txt", "Nothing relevant in this note.", "unfindable-f309cd44");
        tasks[^1] = tasks[^1] with { ExpectedFile = null };

        int rows = profile.Name == "smoke" ? 1_000 : profile.Name == "standard" ? 10_000 : 50_000;
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            string file = kind == DatabaseKind.Sqlite ? "records.sqlite" : "records.csdb";
            await CreateDatabaseAsync(Path.Combine(root, file), kind, rows, token).ConfigureAwait(false);
            tasks.Add(new(kind.ToString().ToLowerInvariant(), DatabaseMarker, file, DatabaseRow: rows, DatabaseColumn: "Notes"));
        }

        string noise = Path.Combine(root, "ordinary");
        Directory.CreateDirectory(noise);
        for (int i = 0; i < profile.SmallTextFileCount; i++)
            await File.WriteAllTextAsync(Path.Combine(noise, $"note-{i:D6}.txt"), $"Routine status report {i.ToString(CultureInfo.InvariantCulture)}. No pending work.", token).ConfigureAwait(false);

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
            new CorpusFile(Path.GetRelativePath(root, path), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
        var manifest = new TaskManifest(1, "Generated controlled fixtures, not a customer corpus or held-out relevance set.", profile.Name, root, rows, tasks, files);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(root)!, "task-manifest.json"), JsonSerializer.Serialize(manifest, TaskBaselineJsonContext.Default.TaskManifest), token).ConfigureAwait(false);
        return manifest;
    }

    private static async Task CreateDatabaseAsync(string path, DatabaseKind kind, int rows, CancellationToken token)
    {
        string table = DatabaseFiles.Quote(Table);
        string schema = $"CREATE TABLE {table} (Id INTEGER PRIMARY KEY, Notes TEXT, Payload BLOB)";
        string Insert(int row) => string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO {table} VALUES ({row},'{(row == rows ? DatabaseMarker.Replace("'", "''", StringComparison.Ordinal) : "Ordinary record")}',NULL)");
        if (kind == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = schema;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            for (int row = 1; row <= rows; row++) { command.CommandText = Insert(row); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
            transaction.Commit();
        }
        else
        {
            await using var database = await Database.OpenAsync(path, token).ConfigureAwait(false);
            await using (var result = await database.ExecuteAsync(schema, token).ConfigureAwait(false)) { }
            for (int row = 1; row <= rows; row++)
            { await using var result = await database.ExecuteAsync(Insert(row), token).ConfigureAwait(false); }
        }
    }
}
