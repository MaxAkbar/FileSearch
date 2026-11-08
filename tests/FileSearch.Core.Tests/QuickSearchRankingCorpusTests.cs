using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Volumes;

namespace FileSearch.Core.Tests;

/// <summary>
/// Frozen task evidence for the drive-name ranking kernel. Expected target ranks
/// are reported rather than asserted here, so the same corpus runs before and
/// after a ranking change and retains unsupported abbreviation/typo tasks.
/// </summary>
public sealed class QuickSearchRankingCorpusTests
{
    [Fact]
    public void CaptureFrozenRankingTasks()
    {
        var corpus = CreateCorpus();
        var tasks = CreateTasks(corpus.DeepBudgetPath);
        var results = new List<TaskResult>();
        foreach (var task in tasks)
        {
            int[] scopes = task.ScopeRoots?.Select(corpus.RecordOf).ToArray() ?? [corpus.Table.RootRecord];
            var request = new VolumeTermSearchRequest(task.Query, MaxResults: 10);
            var started = Stopwatch.GetTimestamp();
            var (matches, total) = VolumeTermSearch.Search(corpus.Table, @"C:\",
                VolumeTermSearch.Compile(task.Query)!, request, scopes.ToHashSet(), CancellationToken.None);
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int target = matches.FindIndex(match => string.Equals(match.Path, task.Target, StringComparison.OrdinalIgnoreCase));
            if (task.MustStayLiteralNoMatch) Assert.Empty(matches);
            if (task.ScopeRoots is { Length: > 0 })
                Assert.All(matches, match => Assert.Contains(task.ScopeRoots,
                    root => match.Path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(matches, match => match.Path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(matches, match => match.Path.Contains("\\Hidden\\", StringComparison.OrdinalIgnoreCase));
            results.Add(new(task.Id, task.Partition, task.Query, task.Target,
                target < 0 ? null : target + 1, total, milliseconds, task.MustStayLiteralNoMatch,
                matches.Select(match => new RankedPath(match.Path, match.Score)).ToArray()));
        }

        // The shortlist adversary has more equal base-score deep matches than
        // MaxResults * 4, followed by a shallow candidate with a better final score.
        var shortlist = CreateShortlistCorpus();
        var shortlistStarted = Stopwatch.GetTimestamp();
        var shortlistMatches = VolumeTermSearch.Search(shortlist.Table, @"C:\",
            VolumeTermSearch.Compile("needle")!, new("needle", MaxResults: 1),
            [shortlist.Table.RootRecord], CancellationToken.None);
        results.Add(new("shortlist-final-score", "training", "needle", @"C:\Work\needle.txt",
            shortlistMatches.Matches.FindIndex(match => match.Path == @"C:\Work\needle.txt") is var rank && rank >= 0 ? rank + 1 : null,
            shortlistMatches.Total, Stopwatch.GetElapsedTime(shortlistStarted).TotalMilliseconds, false,
            shortlistMatches.Matches.Select(match => new RankedPath(match.Path, match.Score)).ToArray()));

        string definition = JsonSerializer.Serialize(new { paths = corpus.Paths, tasks, shortlistPaths = shortlist.Paths });
        var report = new
        {
            schemaVersion = 1,
            corpus = "nw08-quick-ranking-v1",
            corpusSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition))),
            scope = "In-memory drive-name ranking tasks with synthetic paths. Kernel elapsed times are diagnostic, not UI latency or benchmark evidence. No automatic typo/abbreviation expansion or usage learning.",
            overall = Metrics(results),
            training = Metrics(results.Where(result => result.Partition == "training").ToArray()),
            heldOut = Metrics(results.Where(result => result.Partition == "held-out").ToArray()),
            tasks = results,
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string? evidencePath = Environment.GetEnvironmentVariable("FILESEARCH_RANKING_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            string fullPath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
        }
        Console.WriteLine(json);
    }

    [Fact]
    public void DuplicateTieOrderDoesNotDependOnTableInsertionOrder()
    {
        static string[] Search(bool reverse)
        {
            var table = new VolumeNameTable();
            table.Set(Ref(5), Ref(5), ".", FileAttributes.Directory);
            var entries = new (int Id, int Parent, string Name, FileAttributes Attributes)[]
            {
                (10, 5, "Work", FileAttributes.Directory),
                (11, 10, "zeta", FileAttributes.Directory),
                (12, 10, "alpha", FileAttributes.Directory),
                (20, 11, "duplicate.txt", FileAttributes.Archive),
                (21, 12, "duplicate.txt", FileAttributes.Archive),
            };
            foreach (var entry in reverse ? entries.Reverse() : entries)
                table.Set(Ref(entry.Id), Ref(entry.Parent), entry.Name, entry.Attributes);
            return VolumeTermSearch.Search(table, @"C:\", VolumeTermSearch.Compile("duplicate")!,
                new("duplicate", MaxResults: 1), [5], CancellationToken.None).Matches.Select(match => match.Path).ToArray();
        }
        Assert.Equal(Search(false), Search(true));
    }

    private static object Metrics(IReadOnlyCollection<TaskResult> results) => new
    {
        taskCount = results.Count,
        top1 = results.Count(result => result.TargetRank == 1),
        top5 = results.Count(result => result.TargetRank is >= 1 and <= 5),
        missingTargets = results.Count(result => result.TargetRank is null),
        meanReciprocalRank = results.Average(result => result.TargetRank is { } rank ? 1d / rank : 0),
    };

    private static RankingTask[] CreateTasks(string deepBudgetPath) =>
    [
        new("exact-fullname", "training", "invoice.pdf", @"C:\Work\invoice.pdf"),
        new("exact-stem", "training", "report", @"C:\Work\report.docx"),
        new("deep-exact-before-prefix", "training", "budget", deepBudgetPath),
        new("dotted-folder-is-not-file-stem", "training", "client", @"C:\Work\client.txt"),
        new("literal-phrase-prefix", "training", "project plan", @"C:\Work\project plan.md"),
        new("parent-and-leaf", "training", "archive brief", @"C:\Work\archive\brief.txt"),
        new("literal-operator-text", "training", "OR", @"C:\Work\OR.txt"),
        new("abbreviation-not-enabled", "training", "fsr", @"C:\Work\FileSearchRanking.cs", MustStayLiteralNoMatch: true),
        new("exact-fullname-held-out", "held-out", "meeting notes.txt", @"C:\Work\meeting notes.txt"),
        new("full-name-before-stem", "held-out", "readme", @"C:\Work\readme"),
        new("phrase-order-held-out", "held-out", "travel receipt", @"C:\Work\travel receipt.pdf"),
        new("literal-punctuation", "held-out", "C++", @"C:\Work\C++.md"),
        new("literal-brackets", "held-out", "[draft]", @"C:\Work\[draft].txt"),
        new("duplicate-stable-tie", "held-out", "duplicate", @"C:\Work\zeta\duplicate.txt"),
        new("explicit-multiple-roots", "held-out", "restricted", @"C:\Work\teamA\restricted-report.txt", [@"C:\Work\teamA", @"C:\Work\teamC"]),
        new("excluded-folders", "held-out", "secret", @"C:\Work\secret.txt"),
        new("typo-not-enabled", "held-out", "invoiec", @"C:\Work\invoice.pdf", MustStayLiteralNoMatch: true),
    ];

    private static Corpus CreateCorpus()
    {
        var corpus = new Corpus();
        foreach (string path in new[]
        {
            @"C:\Work\invoice.pdf.backup", @"C:\Work\invoice.pdf", @"C:\Work\report-final.docx", @"C:\Work\report.docx",
            @"C:\Work\budget-notes.xlsx", @"C:\Work\client.txt", @"C:\Work\project planning notes.md", @"C:\Work\plan project.md",
            @"C:\Work\project plan.md", @"C:\Work\archive\brief.txt", @"C:\Work\OR.txt",
            @"C:\Work\FileSearchRanking.cs", @"C:\Work\meeting notes.txt.backup", @"C:\Work\meeting notes.txt", @"C:\Work\readme.md",
            @"C:\Work\readme", @"C:\Work\receipt travel.md", @"C:\Work\travel receipt.pdf", @"C:\Work\C++ handbook.md", @"C:\Work\C++.md",
            @"C:\Work\[draft]-notes.txt", @"C:\Work\[draft].txt", @"C:\Work\zeta\duplicate.txt", @"C:\Work\alpha\duplicate.txt",
            @"C:\Work\teamB\restricted.txt", @"C:\Work\teamA\restricted-report.txt", @"C:\Work\teamC\restricted-notes.txt",
            @"C:\Work\secret.txt", @"C:\Work\node_modules\secret.txt",
        }) corpus.Add(path);
        corpus.Add(@"C:\Work\client.v2", FileAttributes.Directory);
        corpus.Add(@"C:\Work\Hidden", FileAttributes.Directory | FileAttributes.Hidden);
        corpus.Add(@"C:\Work\Hidden\secret.txt");
        corpus.DeepBudgetPath = @"C:\Work\" + string.Join("\\", Enumerable.Range(0, 70).Select(index => "d" + index)) + @"\budget.xlsx";
        corpus.Add(corpus.DeepBudgetPath);
        return corpus;
    }

    private static Corpus CreateShortlistCorpus()
    {
        var corpus = new Corpus();
        string deep = @"C:\Work\" + string.Join("\\", Enumerable.Range(0, 70).Select(index => "d" + index));
        for (int index = 0; index < 8; index++) corpus.Add(deep + "\\copy" + index + @"\needle.txt");
        corpus.Add(@"C:\Work\needle.txt");
        return corpus;
    }

    private static ulong Ref(int record) => VolumeNameTable.ComposeReference(record, 1);

    private sealed class Corpus
    {
        private readonly Dictionary<string, int> _records = new(StringComparer.OrdinalIgnoreCase) { [@"C:\"] = 5 };
        private int _nextRecord = 10;
        public VolumeNameTable Table { get; } = new();
        public string DeepBudgetPath { get; set; } = "";
        public string[] Paths => _records.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        public Corpus() => Table.Set(Ref(5), Ref(5), ".", FileAttributes.Directory);
        public int RecordOf(string path) => _records[path];
        public void Add(string path, FileAttributes attributes = FileAttributes.Archive)
        {
            string current = @"C:\";
            int parent = 5;
            string[] parts = path[3..].Split('\\');
            for (int index = 0; index < parts.Length; index++)
            {
                current = current.TrimEnd('\\') + "\\" + parts[index];
                if (!_records.TryGetValue(current, out int record))
                {
                    record = _nextRecord++;
                    _records.Add(current, record);
                    Table.Set(Ref(record), Ref(parent), parts[index], index == parts.Length - 1 ? attributes : FileAttributes.Directory);
                }
                parent = record;
            }
        }
    }

    private sealed record RankingTask(string Id, string Partition, string Query, string Target, string[]? ScopeRoots = null, bool MustStayLiteralNoMatch = false);
    private sealed record RankedPath(string Path, double Score);
    private sealed record TaskResult(string Id, string Partition, string Query, string Target, int? TargetRank, long TotalMatches,
        double KernelMilliseconds, bool MustStayLiteralNoMatch, IReadOnlyList<RankedPath> Results);
}
