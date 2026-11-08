using System.Globalization;
using System.Text.Json;
using FileSearch.Benchmarks;
using FileSearch.Core.Databases;
using Microsoft.Data.Sqlite;

namespace FileSearch.Benchmarks.Tests;

public sealed class DatabasePhaseProfileTests
{
    [Fact]
    public void ScheduleCounterbalancesEngineOrderAndKeepsWarmupsSeparate()
    {
        var slots = DatabasePhaseProfileRunner.BuildSchedule(100);
        Assert.Equal(212, slots.Count);
        Assert.Equal(12, slots.Count(slot => slot.Warmup));
        var pairs = slots.Where(slot => !slot.Warmup).GroupBy(slot => slot.Round).ToArray();
        Assert.Equal(100, pairs.Length);
        Assert.All(pairs, pair => Assert.Equal(2, pair.Select(slot => slot.Engine).Distinct().Count()));
        Assert.Equal(DatabaseKind.Sqlite, pairs[0].First().Engine);
        Assert.Equal(DatabaseKind.CSharpDb, pairs[1].First().Engine);
        foreach (var engine in Enum.GetValues<DatabaseKind>())
            foreach (int position in new[] { 1, 2 })
                Assert.Equal(50, slots.Count(slot => !slot.Warmup && slot.Engine == engine && slot.Position == position));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(101)]
    [InlineData(1002)]
    public void UnbalancedOrInvalidRoundCountsAreRejected(int rounds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DatabasePhaseProfileRunner.BuildSchedule(rounds));

    [Fact]
    public void PhaseSummariesExcludeWarmupsAndKeepFailedMeasurementsVisible()
    {
        var result = DatabasePhaseProfileRunner.Summarize([
            Sample(-1, DatabaseKind.Sqlite, 1, 999),
            Sample(0, DatabaseKind.Sqlite, 1, 2),
            Sample(1, DatabaseKind.Sqlite, 2, 4) with { Success = false, Error = "Failure" }
        ]);
        var all = Assert.Single(result, row => row.Engine == DatabaseKind.Sqlite && row.Position is null);
        Assert.Equal(2, all.Samples); Assert.Equal(1, all.Successes);
        Assert.Equal(4, all.Ms["OpenTotal"].P95);
        Assert.Equal(2, Assert.Single(result, row => row.Engine == DatabaseKind.Sqlite && row.Position == 1).Ms["OpenTotal"].P95);
        Assert.Null(Assert.Single(result, row => row.Engine == DatabaseKind.CSharpDb && row.Position is null).Ms["OpenTotal"].P50);
    }

    [Fact]
    public void PairedDeltasUseMatchingRoundsAndExcludeIncompleteOrFailedPairs()
    {
        var result = DatabasePhaseProfileRunner.PairedDifferences([
            Sample(-1, DatabaseKind.Sqlite, 1, 1), Sample(-1, DatabaseKind.CSharpDb, 2, 999),
            Sample(0, DatabaseKind.Sqlite, 1, 10), Sample(0, DatabaseKind.CSharpDb, 2, 12),
            Sample(1, DatabaseKind.CSharpDb, 1, 103), Sample(1, DatabaseKind.Sqlite, 2, 100),
            Sample(2, DatabaseKind.Sqlite, 1, 5),
            Sample(3, DatabaseKind.Sqlite, 1, 2), Sample(3, DatabaseKind.CSharpDb, 2, 200) with { Success = false }
        ]);
        Assert.Equal(2, result["OpenTotal"].Count);
        Assert.Equal(2, result["OpenTotal"].P50);
        Assert.Equal(3, result["OpenTotal"].P95);
    }

    [Fact]
    public void SequentialPhaseKeepsNotApplicableAndFailedSamplesUnmeasured()
    {
        var measured = Sample(0, DatabaseKind.Sqlite, 1, 2);
        var notApplicable = Sample(1, DatabaseKind.Sqlite, 2, 3) with { SequentialDeepPageApplicable = false };
        var failed = Sample(2, DatabaseKind.Sqlite, 1, 4) with { Success = false, Error = "Setup failed" };
        var summaries = DatabasePhaseProfileRunner.Summarize([
            measured with { Ms = measured.Ms with { SequentialDeepPage = 1.25 } }, notApplicable, failed,
        ]);
        var summary = Assert.Single(summaries, value => value.Engine == DatabaseKind.Sqlite && value.Position is null);
        Assert.Equal(3, summary.Samples); Assert.Equal(2, summary.Successes);
        Assert.Equal(1, summary.Ms["SequentialDeepPage"].Count);
        Assert.Equal(1.25, summary.Ms["SequentialDeepPage"].P50);
    }

    [Fact]
    public void SequentialPairedDeltasRequireBothSuccessfulMeasuredPages()
    {
        DatabasePhaseSample Sequential(int round, DatabaseKind engine, double? value)
        {
            var sample = Sample(round, engine, engine == DatabaseKind.Sqlite ? 1 : 2, 10);
            return sample with { Ms = sample.Ms with { SequentialDeepPage = value } };
        }
        var result = DatabasePhaseProfileRunner.PairedDifferences([
            Sequential(-1, DatabaseKind.Sqlite, 1), Sequential(-1, DatabaseKind.CSharpDb, 999),
            Sequential(0, DatabaseKind.Sqlite, 2), Sequential(0, DatabaseKind.CSharpDb, 5),
            Sequential(1, DatabaseKind.Sqlite, null), Sequential(1, DatabaseKind.CSharpDb, 99),
            Sequential(2, DatabaseKind.Sqlite, 1), Sequential(2, DatabaseKind.CSharpDb, 900) with { Success = false },
        ]);
        Assert.Equal(1, result["SequentialDeepPage"].Count);
        Assert.Equal(3, result["SequentialDeepPage"].P50);
    }

    [Fact]
    public void SequentialPhaseSerializationRetainsApplicabilityAndNullMeasurements()
    {
        var sample = Sample(0, DatabaseKind.Sqlite, 1, 10) with { SequentialDeepPageApplicable = false };
        string json = JsonSerializer.Serialize(sample, DatabasePhaseJsonContext.Default.DatabasePhaseSample);
        var restored = JsonSerializer.Deserialize(json, DatabasePhaseJsonContext.Default.DatabasePhaseSample)!;
        Assert.False(restored.SequentialDeepPageApplicable);
        Assert.Null(restored.Ms.SequentialDeepPage);
        Assert.Equal(sample.Ms.DeepPage, restored.Ms.DeepPage);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, 3, false)]
    [InlineData(DatabaseKind.CSharpDb, 3, false)]
    [InlineData(DatabaseKind.Sqlite, 251, true)]
    [InlineData(DatabaseKind.CSharpDb, 251, true)]
    public async Task ProfilerValidatesSequentialPagesAndLabelsSinglePageDatabases(DatabaseKind engine, int rows, bool applicable)
    {
        string root = Path.Combine(Path.GetTempPath(), "FileSearchPhaseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "source.db");
        try
        {
            await CreateDatabaseAsync(path, engine, rows);
            var source = new DatabaseProfileSource(engine, path, DatabaseFiles.Fingerprint(path), []);
            var sample = await DatabasePhaseProfileRunner.MeasureAsync(source, new(0, 1, engine, false), rows, TestContext.Current.CancellationToken);
            Assert.True(sample.Success, sample.Error);
            Assert.Equal(applicable, sample.SequentialDeepPageApplicable);
            Assert.NotNull(sample.Ms.FirstPage); Assert.NotNull(sample.Ms.DeepPage);
            if (applicable) Assert.NotNull(sample.Ms.SequentialDeepPage); else Assert.Null(sample.Ms.SequentialDeepPage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PageValidationRejectsWrongOrdinalsValuesAndMissingExpectedMarker()
    {
        var valid = new DatabasePage([new(1, "rowid=1", ["1", "Ordinary record", null]), new(2, "rowid=2", ["2", TaskCorpus.DatabaseMarker, null])], false, false);
        Assert.True(DatabasePhaseProfileRunner.IsExpectedPage(valid, 1, 2));
        Assert.False(DatabasePhaseProfileRunner.IsExpectedPage(valid with { Rows = [valid.Rows[0], valid.Rows[1] with { Number = 3 }] }, 1, 2));
        Assert.False(DatabasePhaseProfileRunner.IsExpectedPage(valid with { Rows = [valid.Rows[0], valid.Rows[1] with { Values = ["3", TaskCorpus.DatabaseMarker, null] }] }, 1, 2));
        Assert.False(DatabasePhaseProfileRunner.IsExpectedPage(valid with { Rows = [valid.Rows[0], valid.Rows[1] with { Values = ["2", "wrong marker", null] }] }, 1, 2));
    }

    private static async Task CreateDatabaseAsync(string path, DatabaseKind engine, int rows)
    {
        string table = DatabaseFiles.Quote(TaskCorpus.Table);
        string schema = $"CREATE TABLE {table} (Id INTEGER PRIMARY KEY, Notes TEXT, Payload BLOB)";
        string Insert(int row) => $"INSERT INTO {table} VALUES ({row.ToString(CultureInfo.InvariantCulture)},'{(row == rows ? TaskCorpus.DatabaseMarker.Replace("'", "''", StringComparison.Ordinal) : "Ordinary record")}',NULL)";
        var token = TestContext.Current.CancellationToken;
        if (engine == DatabaseKind.Sqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = schema; await command.ExecuteNonQueryAsync(token);
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            for (int row = 1; row <= rows; row++) { command.CommandText = Insert(row); await command.ExecuteNonQueryAsync(token); }
            transaction.Commit();
        }
        else
        {
            await using var database = await CSharpDB.Engine.Database.OpenAsync(path, token);
            await using (var result = await database.ExecuteAsync(schema, token)) { }
            await database.BeginTransactionAsync(token);
            for (int row = 1; row <= rows; row++) { await using var result = await database.ExecuteAsync(Insert(row), token); }
            await database.CommitAsync(token);
        }
    }

    private static DatabasePhaseSample Sample(int round, DatabaseKind engine, int position, double open) =>
        new(new(round, position, engine, round < 0), true, null,
            new(null, null, null, null, null, null, null, null, open, null, null, null, null, null), 0, 0, 0, 0, true);
}
