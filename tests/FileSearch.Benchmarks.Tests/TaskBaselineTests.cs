using System.Security.Cryptography;
using System.Text.Json;
using FileSearch.Benchmarks;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;

namespace FileSearch.Benchmarks.Tests;

public sealed class TaskBaselineTests
{
    [Fact]
    public void ResumeValidatesOriginalRootHashesAndPathBoundaries()
    {
        var directory = Directory.CreateTempSubdirectory("FileSearchBaseline-");
        try
        {
            string root = Path.Combine(directory.FullName, "content");
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "note.txt");
            File.WriteAllText(source, "original");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            var manifest = new TaskManifest(1, "Generated controlled fixtures, test", "smoke", root, 1000, [], [new("note.txt", 8, hash)]);
            string path = Path.Combine(directory.FullName, "task-manifest.json");
            void Write(TaskManifest value) => File.WriteAllText(path, JsonSerializer.Serialize(value, TaskBaselineJsonContext.Default.TaskManifest));
            Write(manifest);
            Assert.Equal(root, TaskBaselineRunner.ReadManifest(path).ContentRoot);
            Write(manifest with { ContentRoot = directory.FullName });
            Assert.Throws<InvalidDataException>(() => TaskBaselineRunner.ReadManifest(path));
            Write(manifest with { Files = [new("../outside.txt", 8, hash)] });
            Assert.Throws<InvalidDataException>(() => TaskBaselineRunner.ReadManifest(path));
            Write(manifest);
            File.WriteAllText(source, "changed");
            Assert.Throws<InvalidDataException>(() => TaskBaselineRunner.ReadManifest(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void PercentilesPreserveMissingMeasurementsAndUseNearestRank()
    {
        var none = Percentiles.From([null, null]);
        Assert.Equal(0, none.Count);
        Assert.Null(none.P50); Assert.Null(none.P95); Assert.Null(none.P99);
        var values = Percentiles.From(Enumerable.Range(1, 100).Select(value => (double?)value).Append(null));
        Assert.Equal(100, values.Count);
        Assert.Equal(50, values.P50); Assert.Equal(95, values.P95); Assert.Equal(99, values.P99);
    }

    [Fact]
    public void ColdAndFailedSamplesCannotImproveWarmGoalOrUsefulLatency()
    {
        var summary = Assert.Single(TaskBaselineRunner.Summarize([
            Sample(iteration: 0, success: true, completion: 900, useful: 800),
            Sample(iteration: 1, success: true, completion: 4, useful: 3),
            Sample(iteration: 2, success: false, completion: 1, useful: 0.5)
        ]));
        Assert.Equal(2, summary.Samples);
        Assert.Equal(1, summary.Successes);
        Assert.Equal(4, summary.CompletionMs.P95);
        Assert.Equal(3, summary.FirstUsefulMs.P95);
        Assert.False(summary.MeetsGoal);
    }

    [Fact]
    public void SuccessfulNegativeSearchHasNoUsefulResultTime()
    {
        var summary = Assert.Single(TaskBaselineRunner.Summarize([Sample(1, true, 3, null) with { Task = "no-match" }]));
        Assert.Equal(1, summary.Successes);
        Assert.Null(summary.FirstUsefulMs.P50);
        Assert.Equal(3, summary.CompletionMs.P50);
    }

    [Fact]
    public void IncompleteWorkerCannotPassGoalWithOnlyFastCompletedSamples()
    {
        var summary = Assert.Single(TaskBaselineRunner.Summarize([Sample(1, true, 3, 2)],
            [new TaskWorker("known-file", "indexed", 21, 2, false, true, "Timed out")]));
        Assert.Equal(3, summary.CompletionMs.P95);
        Assert.False(summary.MeetsGoal);
    }

    [Fact]
    public void FirstUsefulMustIdentifyTheExpectedFileAndPdfPage()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileSearchBaseline");
        var task = new BaselineTask("pdf", "policy", "policy.pdf", Page: 2);
        var wrongPage = new Hit(Path.Combine(root, "policy.pdf"), 1, "policy", [], Anchor: SourceAnchor.PdfPage(1));
        Assert.False(TaskBaselineRunner.IsUseful(wrongPage, task, root));
        var correct = wrongPage with { Anchor = SourceAnchor.PdfPage(2) };
        Assert.True(TaskBaselineRunner.IsUseful(correct, task, root));
        Assert.False(TaskBaselineRunner.IsUseful(correct with { Path = Path.Combine(root, "decoy.pdf") }, task, root));
        Assert.False(TaskBaselineRunner.IsUseful(correct, task with { ExpectedFile = null }, root));
    }

    [Fact]
    public void DatabaseTaskRejectsWrongRecordColumnOrTable()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileSearchBaseline");
        var task = new BaselineTask("database", "needle", "records.db", DatabaseRow: 750, DatabaseColumn: "Notes");
        var location = new DatabaseLocation(DatabaseKind.Sqlite, TaskCorpus.Table, 750, "Notes", "750", "fingerprint");
        var hit = new Hit(Path.Combine(root, "records.db"), 1, "needle", [], Locator: new SourceLocator(Database: location));
        Assert.True(TaskBaselineRunner.IsUseful(hit, task, root));
        foreach (var wrong in new[] { location with { RowNumber = 1 }, location with { Column = "Id" }, location with { Table = "Other" } })
            Assert.False(TaskBaselineRunner.IsUseful(hit with { Locator = new SourceLocator(Database: wrong) }, task, root));
    }

    [Fact]
    public void ArchiveTaskChecksMemberFromBothLiveAndIndexedLocations()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileSearchBaseline");
        var task = new BaselineTask("archive", "needle", "policies.zip", Member: "folder/target.txt");
        var hit = new Hit(Path.Combine(root, "policies.zip"), 1, "needle", [], Locator: new SourceLocator(ArchiveMember: "folder/target.txt"));
        Assert.True(TaskBaselineRunner.IsUseful(hit, task, root));
        Assert.False(TaskBaselineRunner.IsUseful(hit with { Locator = new SourceLocator(ArchiveMember: "decoy.txt") }, task, root));
        Assert.True(TaskBaselineRunner.IsUseful(hit with { Locator = null, Anchor = new SourceAnchor(SourceAnchorKind.Archive, "member", MemberPath: task.Member) }, task, root));
    }

    private static TaskSample Sample(int iteration, bool success, double completion, double? useful) =>
        new("known-file", "indexed", iteration, iteration == 0 ? "first invocation" : "warm", success, 1,
            useful, useful, completion, 0, 0, success ? null : "Missing expected source", []);
}
