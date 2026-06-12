using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Core.Workflows;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WorkflowRunnerTests : IDisposable
{
    private readonly string _root;

    public WorkflowRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filesearch-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task SequentialSearches_RecordOutcomesWithHitAndFileCounts()
    {
        var fake = new FakeSearcher(r => QueryTextOf(r) switch
        {
            "alpha" => new[]
            {
                MakeHit(@"C:\data\a.txt", 1),
                MakeHit(@"C:\data\a.txt", 2),
                MakeHit(@"C:\data\b.txt", 1),
            },
            "beta" => new[] { MakeHit(@"C:\data\c.txt", 7) },
            _ => Array.Empty<Hit>(),
        });
        var workflow = Workflow(
            new SearchStep { Id = "s1", Query = "alpha", Roots = new[] { @"C:\data" }, UseIndex = true },
            Search("s2", "beta"));

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.True(result.Succeeded);
        Assert.Null(result.Message);
        Assert.Equal(2, result.StepOutcomes.Count);

        var first = result.StepOutcomes[0];
        Assert.Equal("s1", first.StepId);
        Assert.Equal("search", first.StepKind);
        Assert.True(first.Succeeded);
        Assert.Equal(3L, first.HitCount);
        Assert.Equal(2, first.FileCount);

        var second = result.StepOutcomes[1];
        Assert.Equal("s2", second.StepId);
        Assert.Equal(1L, second.HitCount);
        Assert.Equal(1, second.FileCount);

        // The runner must pass the step's roots and index preference through.
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(new[] { @"C:\data" }, fake.Requests[0].Roots);
        Assert.True(fake.Requests[0].UseIndex);
        Assert.False(fake.Requests[1].UseIndex);
    }

    [Theory]
    [InlineData(1L, "then-q")]
    [InlineData(100L, "else-q")]
    public async Task IfStep_RunsBranchSelectedByCondition(long threshold, string expectedQuery)
    {
        var fake = new FakeSearcher(r => QueryTextOf(r) == "alpha"
            ? new[] { MakeHit(@"C:\data\a.txt"), MakeHit(@"C:\data\b.txt") }
            : Array.Empty<Hit>());
        var workflow = Workflow(
            Search("s1", "alpha"),
            new IfStep
            {
                Id = "i1",
                Condition = new WorkflowCondition
                {
                    Source = "s1",
                    Metric = ConditionMetric.HitCount,
                    Operator = ConditionOperator.GreaterOrEqual,
                    Value = threshold,
                },
                Then = new WorkflowStep[] { Search("t1", "then-q") },
                Else = new WorkflowStep[] { Search("e1", "else-q") },
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "alpha", expectedQuery }, fake.Requests.Select(QueryTextOf).ToArray());
    }

    [Fact]
    public async Task Condition_SourceDefaultsToLastSearchStep()
    {
        // s1 has hits but s2 (the most recent search) has none, so a
        // source-less condition must look at s2 and take the else branch.
        var fake = new FakeSearcher(r => QueryTextOf(r) == "alpha"
            ? new[] { MakeHit(@"C:\data\a.txt") }
            : Array.Empty<Hit>());
        var workflow = Workflow(
            Search("s1", "alpha"),
            Search("s2", "beta"),
            new IfStep
            {
                Id = "i1",
                Condition = new WorkflowCondition { Operator = ConditionOperator.GreaterOrEqual, Value = 1 },
                Then = new WorkflowStep[] { Search("t1", "then-q") },
                Else = new WorkflowStep[] { Search("e1", "else-q") },
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "alpha", "beta", "else-q" }, fake.Requests.Select(QueryTextOf).ToArray());
    }

    [Fact]
    public async Task RetryStep_StopsWhenConditionMet_AndExposesIterationAndParameterSetVariables()
    {
        // Only "probe two 2" (parameter set #2 plus ${iteration} = 2) yields a
        // hit, so the loop must run exactly two of its three iterations.
        var fake = new FakeSearcher(r => QueryTextOf(r) == "probe two 2"
            ? new[] { MakeHit(@"C:\data\found.txt") }
            : Array.Empty<Hit>());
        var workflow = Workflow(new RetryStep
        {
            Id = "r1",
            MaxIterations = 3,
            ParameterSets = new IReadOnlyDictionary<string, string>[]
            {
                new Dictionary<string, string> { ["term"] = "one" },
                new Dictionary<string, string> { ["term"] = "two" },
                new Dictionary<string, string> { ["term"] = "three" },
            },
            Body = new WorkflowStep[] { Search("rs", "probe ${term} ${iteration}") },
            Until = new WorkflowCondition { Operator = ConditionOperator.GreaterOrEqual, Value = 1 },
        });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "probe one 1", "probe two 2" }, fake.Requests.Select(QueryTextOf).ToArray());

        var retryOutcome = result.StepOutcomes.Single(o => o.StepId == "r1");
        Assert.True(retryOutcome.Succeeded);
        Assert.Contains("met after 2 iteration(s)", retryOutcome.Detail);
    }

    [Fact]
    public async Task ForEachStep_RunsBodyPerMatchedFile_WithFileVariables()
    {
        var fake = new FakeSearcher(r => QueryTextOf(r) == "alpha"
            ? new[]
            {
                MakeHit(@"C:\data\a.txt", 1),
                MakeHit(@"C:\data\a.txt", 2),
                MakeHit(@"C:\data\b.txt", 1),
            }
            : Array.Empty<Hit>());
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ForEachStep
            {
                Id = "f1",
                SourceStepId = "s1",
                Body = new WorkflowStep[]
                {
                    new SearchStep
                    {
                        Id = "body",
                        Query = "in ${file} named ${fileName}",
                        Roots = new[] { "${directory}" },
                    },
                },
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);

        // One body execution per distinct matched file, with ${file},
        // ${fileName} and ${directory} substituted.
        var bodyRequests = fake.Requests.Skip(1).ToList();
        Assert.Equal(2, bodyRequests.Count);
        Assert.Equal(@"in C:\data\a.txt named a.txt", QueryTextOf(bodyRequests[0]));
        Assert.Equal(@"in C:\data\b.txt named b.txt", QueryTextOf(bodyRequests[1]));
        Assert.All(bodyRequests, r => Assert.Equal(@"C:\data", Assert.Single(r.Roots)));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "f1");
        Assert.Contains("iterated 2 file(s)", outcome.Detail);
    }

    [Fact]
    public async Task ScopedSubSearch_SearchesOnlyFilesMatchedByTheSourceStep()
    {
        File.WriteAllText(Path.Combine(_root, "one.txt"), "alpha and beta\n");
        File.WriteAllText(Path.Combine(_root, "two.txt"), "alpha only\n");
        File.WriteAllText(Path.Combine(_root, "three.txt"), "gamma\n");

        // Real engine end to end: filesystem search, then a sub-search scoped
        // to the first step's matched files.
        var registry = CreateRegistry();
        var runner = new WorkflowRunner(new Searcher(new FileWalker(), registry), new QueryFactory(), registry);
        var observer = new RecordingObserver();
        var workflow = Workflow(
            new SearchStep { Id = "s1", Query = "alpha", Roots = new[] { _root } },
            new SearchStep { Id = "s2", Query = "beta", ScopeStepId = "s1" });

        var result = await runner.RunAsync(
            workflow, observer: observer, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);

        var outer = result.StepOutcomes.Single(o => o.StepId == "s1");
        Assert.Equal(2, outer.FileCount);

        var scoped = result.StepOutcomes.Single(o => o.StepId == "s2");
        Assert.Equal(1L, scoped.HitCount);
        Assert.Equal(1, scoped.FileCount);

        var scopedHit = Assert.Single(observer.Hits, h => h.Step.Id == "s2");
        Assert.EndsWith("one.txt", scopedHit.Hit.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopStep_InsideIf_EndsRunWithStoppedStatusAndMessage(bool succeeded)
    {
        var fake = new FakeSearcher(_ => Array.Empty<Hit>());
        var workflow = Workflow(
            Search("s1", "alpha"),
            new IfStep
            {
                Id = "i1",
                Condition = new WorkflowCondition { Source = "s1", Operator = ConditionOperator.Equals, Value = 0 },
                Then = new WorkflowStep[]
                {
                    new StopStep { Id = "halt", Succeeded = succeeded, Message = "Nothing found." },
                },
            },
            Search("after", "never-runs"));

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Stopped, result.Status);
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal("Nothing found.", result.Message);
        Assert.DoesNotContain(fake.Requests, r => QueryTextOf(r) == "never-runs");
    }

    [Fact]
    public async Task InvalidWorkflow_FailsWithValidationErrors_WithoutRunningAnySteps()
    {
        var fake = new FakeSearcher(_ => Array.Empty<Hit>());
        var workflow = new WorkflowDefinition
        {
            Name = "",
            Steps = new WorkflowStep[]
            {
                new SearchStep { Id = "s1", Query = "", Roots = new[] { @"C:\data" } },
            },
        };

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Message);
        Assert.StartsWith("Workflow is invalid", result.Message);
        Assert.Equal(2, result.ValidationErrors.Count);
        Assert.Empty(result.StepOutcomes);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task RunawayRetryLoop_FailsWhenStepExecutionBudgetIsExceeded()
    {
        var fake = new FakeSearcher(_ => Array.Empty<Hit>());
        var workflow = Workflow(new RetryStep
        {
            Id = "r1",
            MaxIterations = 100,
            Body = new WorkflowStep[] { Search("rs", "never-enough") },
            Until = new WorkflowCondition { Operator = ConditionOperator.GreaterOrEqual, Value = 1 },
        });

        var result = await CreateRunner(fake).RunAsync(
            workflow,
            new WorkflowRunOptions { MaxStepExecutions = 5 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Message);
        Assert.Contains("Exceeded the limit of 5 step executions", result.Message);
        Assert.True(fake.Requests.Count <= 5);
    }

    [Fact]
    public async Task ExportStep_Json_WritesParsableDocumentWithTotals()
    {
        var fake = new FakeSearcher(_ => new[]
        {
            MakeHit(@"C:\data\a.txt", 1, "alpha one"),
            MakeHit(@"C:\data\a.txt", 5, "alpha two"),
            MakeHit(@"C:\data\b.txt", 2, "alpha three"),
        });
        var path = Path.Combine(_root, "out", "hits.json");
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Format = ExportFormat.Json, Path = path });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("Test workflow", root.GetProperty("workflow").GetString());
        Assert.Equal("s1", root.GetProperty("sourceStep").GetString());
        Assert.Equal(3L, root.GetProperty("totalHits").GetInt64());
        Assert.Equal(2, root.GetProperty("fileCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var hits = root.GetProperty("hits");
        Assert.Equal(3, hits.GetArrayLength());
        Assert.Equal(@"C:\data\a.txt", hits[0].GetProperty("path").GetString());
        Assert.Equal(5, hits[1].GetProperty("lineNumber").GetInt32());
        Assert.Equal("alpha three", hits[2].GetProperty("line").GetString());
    }

    [Fact]
    public async Task ExportStep_Csv_QuotesCommasAndQuotes()
    {
        var fake = new FakeSearcher(_ => new[]
        {
            MakeHit(@"C:\data\plain.txt", 3, "value, with \"quotes\""),
            MakeHit(@"C:\data\plain.txt", 4, "no specials"),
        });
        var path = Path.Combine(_root, "hits.csv");
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Format = ExportFormat.Csv, Path = path });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.Equal("path,lineNumber,line", lines[0]);
        Assert.Equal(@"C:\data\plain.txt,3," + "\"value, with \"\"quotes\"\"\"", lines[1]);
        Assert.Equal(@"C:\data\plain.txt,4,no specials", lines[2]);
    }

    [Fact]
    public async Task ExportStep_Markdown_GroupsHitsUnderFileHeaders()
    {
        var fake = new FakeSearcher(_ => new[]
        {
            MakeHit(@"C:\data\a.txt", 1, "alpha one"),
            MakeHit(@"C:\data\a.txt", 5, "alpha two"),
            MakeHit(@"C:\data\b.txt", 2, "alpha three"),
        });
        var path = Path.Combine(_root, "hits.md");
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Format = ExportFormat.Markdown, Path = path });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var content = File.ReadAllText(path);
        Assert.StartsWith("# Test workflow — search results", content);
        Assert.Contains("- Hits: 3 in 2 file(s)", content);
        Assert.Contains(@"## C:\data\a.txt", content);
        Assert.Contains(@"## C:\data\b.txt", content);
        Assert.Contains("- L1: alpha one", content);
        Assert.Contains("- L5: alpha two", content);
        Assert.Contains("- L2: alpha three", content);
    }

    [Fact]
    public async Task ExportStep_WithoutOverwrite_FailsWhenFileExists()
    {
        var fake = new FakeSearcher(_ => new[] { MakeHit(@"C:\data\a.txt") });
        var path = Path.Combine(_root, "existing.json");
        File.WriteAllText(path, "precious");
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Path = path, Overwrite = false });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("already exists", result.Message);
        Assert.Equal("precious", File.ReadAllText(path));
    }

    [Fact]
    public async Task FileOperation_CopyWithRenameCollision_CreatesNumberedCopy()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        Directory.CreateDirectory(destination);
        File.WriteAllText(sourceFile, "fresh content");
        File.WriteAllText(Path.Combine(destination, "doc.txt"), "existing content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Copy,
                SourceStepId = "s1",
                DestinationDirectory = destination,
                Collision = FileCollisionPolicy.Rename,
            });

        // Null interaction means headless consent — no prompt needed.
        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("fresh content", File.ReadAllText(Path.Combine(destination, "doc (1).txt")));
        Assert.Equal("existing content", File.ReadAllText(Path.Combine(destination, "doc.txt")));
        Assert.True(File.Exists(sourceFile));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.FileCount);
    }

    [Fact]
    public async Task FileOperation_DryRun_LogsButDoesNotCopy()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var observer = new RecordingObserver();
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep { Id = "op", SourceStepId = "s1", DestinationDirectory = destination });

        var result = await CreateRunner(fake).RunAsync(
            workflow,
            new WorkflowRunOptions { DryRun = true },
            observer,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.False(Directory.Exists(destination));
        Assert.Contains(observer.Logs, l => l.Contains("[dry run]") && l.Contains("copy 1 file(s)"));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Contains("[dry run]", outcome.Detail);
    }

    [Fact]
    public async Task FileOperation_DeclinedByInteraction_SkipsWithoutCopying()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var interaction = new FakeInteraction(answer: false);
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep { Id = "op", SourceStepId = "s1", DestinationDirectory = destination });

        var result = await CreateRunner(fake).RunAsync(
            workflow, interaction: interaction, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.False(Directory.Exists(destination));

        var confirmation = Assert.Single(interaction.Confirmations);
        Assert.Equal("Copy 1 file(s)", confirmation.Title);
        Assert.Contains(sourceFile, confirmation.Details);

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Contains("declined", outcome.Detail);
    }

    [Fact]
    public async Task RunProgram_DryRun_LogsCommandLinesWithoutLaunching()
    {
        var fake = new FakeSearcher(_ => new[]
        {
            MakeHit(@"C:\data\a.txt"),
            MakeHit(@"C:\data\b.txt"),
        });
        var observer = new RecordingObserver();
        var workflow = Workflow(
            Search("s1", "alpha"),
            new RunProgramStep
            {
                Id = "p1",
                Program = "notepad.exe",
                Arguments = "--open ${file}",
                PerFile = true,
                SourceStepId = "s1",
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow,
            new WorkflowRunOptions { DryRun = true },
            observer,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Contains(@"[dry run] Would run: notepad.exe --open C:\data\a.txt", observer.Logs);
        Assert.Contains(@"[dry run] Would run: notepad.exe --open C:\data\b.txt", observer.Logs);

        var outcome = result.StepOutcomes.Single(o => o.StepId == "p1");
        Assert.True(outcome.Succeeded);
        Assert.Contains("would launch 2 process(es)", outcome.Detail);
    }

    [Fact]
    public async Task CancellationBeforeRun_ReturnsCancelledWithoutSearching()
    {
        var fake = new FakeSearcher(_ => new[] { MakeHit(@"C:\data\a.txt") });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await CreateRunner(fake).RunAsync(
            Workflow(Search("s1", "alpha")), cancellationToken: cts.Token);

        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("Cancelled.", result.Message);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Observer_ReceivesStartHitsAndCompletionInOrder()
    {
        var fake = new FakeSearcher(_ => new[]
        {
            MakeHit(@"C:\data\a.txt", 1),
            MakeHit(@"C:\data\b.txt", 2),
        });
        var observer = new RecordingObserver();

        var result = await CreateRunner(fake).RunAsync(
            Workflow(Search("s1", "alpha")),
            observer: observer,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "started:s1", "hit:s1", "hit:s1", "completed:s1" }, observer.Events);
        Assert.Equal(result.StepOutcomes, observer.Outcomes);
    }

    [Fact]
    public async Task MaxHits_CapsSearchStep()
    {
        var fake = new FakeSearcher(_ => Enumerable.Range(1, 100)
            .Select(i => MakeHit(@"C:\data\big.txt", i))
            .ToArray());
        var workflow = Workflow(new SearchStep
        {
            Id = "s1",
            Query = "alpha",
            Roots = new[] { @"C:\data" },
            MaxHits = 10,
        });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var outcome = Assert.Single(result.StepOutcomes);
        Assert.Equal(10L, outcome.HitCount);
        Assert.Contains("stopped at maxHits 10", outcome.Detail);
    }

    [Fact]
    public async Task ExportStep_DryRun_DoesNotWriteFile()
    {
        var fake = new FakeSearcher(_ => new[] { MakeHit(@"C:\data\a.txt") });
        var path = Path.Combine(_root, "dry.json");
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Path = path });

        var result = await CreateRunner(fake).RunAsync(
            workflow,
            new WorkflowRunOptions { DryRun = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.False(File.Exists(path));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "e1");
        Assert.True(outcome.Succeeded);
        Assert.Contains("dry run", outcome.Detail);
    }

    [Fact]
    public async Task ExportStep_WithoutSource_AggregatesAllSearchSteps()
    {
        var fake = new FakeSearcher(r => QueryTextOf(r) switch
        {
            "alpha" => new[]
            {
                MakeHit(@"C:\data\a.txt", 1, "alpha one"),
                MakeHit(@"C:\data\a.txt", 2, "alpha two"),
            },
            "beta" => new[] { MakeHit(@"C:\data\b.txt", 3, "beta one") },
            _ => Array.Empty<Hit>(),
        });
        var path = Path.Combine(_root, "all.json");
        var workflow = Workflow(
            Search("s1", "alpha"),
            Search("s2", "beta"),
            new ExportStep { Id = "e1", Path = path });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(3L, root.GetProperty("totalHits").GetInt64());
        Assert.Equal(2, root.GetProperty("fileCount").GetInt32());

        var paths = root.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("path").GetString())
            .ToArray();
        Assert.Equal(3, paths.Length);
        Assert.Contains(@"C:\data\a.txt", paths);
        Assert.Contains(@"C:\data\b.txt", paths);
    }

    [Fact]
    public async Task ExportStep_OverwriteDeclined_LeavesExistingFileUntouched()
    {
        var fake = new FakeSearcher(_ => new[] { MakeHit(@"C:\data\a.txt") });
        var path = Path.Combine(_root, "existing.json");
        File.WriteAllText(path, "precious");
        var interaction = new FakeInteraction(answer: false);
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Path = path, Overwrite = true });

        var result = await CreateRunner(fake).RunAsync(
            workflow, interaction: interaction, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("precious", File.ReadAllText(path));
        Assert.Single(interaction.Confirmations);

        var outcome = result.StepOutcomes.Single(o => o.StepId == "e1");
        Assert.True(outcome.Succeeded);
        Assert.Equal("skipped — declined by user", outcome.Detail);
    }

    [Fact]
    public async Task ExportStep_OverwriteConfirmed_ReplacesExistingFile()
    {
        var fake = new FakeSearcher(_ => new[] { MakeHit(@"C:\data\a.txt") });
        var path = Path.Combine(_root, "existing.json");
        File.WriteAllText(path, "precious");
        var interaction = new FakeInteraction(answer: true);
        var workflow = Workflow(
            Search("s1", "alpha"),
            new ExportStep { Id = "e1", SourceStepId = "s1", Path = path, Overwrite = true });

        var result = await CreateRunner(fake).RunAsync(
            workflow, interaction: interaction, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Single(interaction.Confirmations);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1L, document.RootElement.GetProperty("totalHits").GetInt64());
    }

    [Fact]
    public async Task FileOperation_Move_RemovesSourceAndCreatesTarget()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "movable");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Move,
                SourceStepId = "s1",
                DestinationDirectory = destination,
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.False(File.Exists(sourceFile));
        Assert.Equal("movable", File.ReadAllText(Path.Combine(destination, "doc.txt")));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.FileCount);
    }

    [Fact]
    public async Task FileOperation_CopyWithSkipCollision_LeavesExistingTarget()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        Directory.CreateDirectory(destination);
        File.WriteAllText(sourceFile, "fresh content");
        File.WriteAllText(Path.Combine(destination, "doc.txt"), "existing content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Copy,
                SourceStepId = "s1",
                DestinationDirectory = destination,
                Collision = FileCollisionPolicy.Skip,
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("existing content", File.ReadAllText(Path.Combine(destination, "doc.txt")));
        Assert.True(File.Exists(sourceFile));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Contains("1 skipped", outcome.Detail);
    }

    [Fact]
    public async Task FileOperation_CopyWithOverwriteCollision_ReplacesTarget()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        Directory.CreateDirectory(destination);
        File.WriteAllText(sourceFile, "fresh content");
        File.WriteAllText(Path.Combine(destination, "doc.txt"), "existing content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Copy,
                SourceStepId = "s1",
                DestinationDirectory = destination,
                Collision = FileCollisionPolicy.Overwrite,
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("fresh content", File.ReadAllText(Path.Combine(destination, "doc.txt")));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.FileCount);
    }

    [Fact]
    public async Task FileOperation_CopyOntoItself_SkipsInsteadOfFailing()
    {
        var directory = Path.Combine(_root, "same");
        var sourceFile = Path.Combine(directory, "doc.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(sourceFile, "stay put");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Copy,
                SourceStepId = "s1",
                DestinationDirectory = directory,
            });

        var result = await CreateRunner(fake).RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("stay put", File.ReadAllText(sourceFile));

        var outcome = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(outcome.Succeeded);
        Assert.Contains("1 skipped", outcome.Detail);
        Assert.DoesNotContain("failed", outcome.Detail);
    }

    [Fact]
    public async Task CancellationDuringConfirmation_ReturnsCancelledWithoutOperating()
    {
        var sourceFile = Path.Combine(_root, "source", "doc.txt");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "content");

        var fake = new FakeSearcher(_ => new[] { MakeHit(sourceFile) });
        using var cts = new CancellationTokenSource();
        // The user hits Cancel while the confirmation prompt is open: the run
        // is cancelled and the dialog resolves to "no". That must surface as
        // a cancelled run, not as a completed run with a recorded decline.
        var interaction = new CancelOnConfirmInteraction(cts);
        var workflow = Workflow(
            Search("s1", "alpha"),
            new FileOperationStep { Id = "op", SourceStepId = "s1", DestinationDirectory = destination });

        var result = await CreateRunner(fake).RunAsync(
            workflow, interaction: interaction, cancellationToken: cts.Token);

        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(destination));
        Assert.True(File.Exists(sourceFile));
    }

    [Fact(Timeout = 60_000)]
    public async Task MaxHits_RealEngine_ReleasesFileHandlesForFollowUpMove()
    {
        var sourceDir = Path.Combine(_root, "many");
        var destination = Path.Combine(_root, "moved");
        Directory.CreateDirectory(sourceDir);
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(
                Path.Combine(sourceDir, $"file{i:D2}.txt"),
                string.Concat(Enumerable.Repeat("alpha match line\n", 6)));
        }

        var registry = CreateRegistry();
        var runner = new WorkflowRunner(new Searcher(new FileWalker(), registry), new QueryFactory(), registry);
        var workflow = Workflow(
            new SearchStep { Id = "s1", Query = "alpha", Roots = new[] { sourceDir }, MaxHits = 5 },
            new FileOperationStep
            {
                Id = "op",
                Operation = FileOperationKind.Move,
                SourceStepId = "s1",
                DestinationDirectory = destination,
            });

        var result = await runner.RunAsync(
            workflow, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);

        var search = result.StepOutcomes.Single(o => o.StepId == "s1");
        Assert.Equal(5L, search.HitCount);
        Assert.Contains("stopped at maxHits 5", search.Detail);

        // Stopping at maxHits cancels the search mid-stream; the engine must
        // join its producer before returning so no worker still holds a
        // handle on a matched file when the move runs.
        var move = result.StepOutcomes.Single(o => o.StepId == "op");
        Assert.True(move.Succeeded);
        Assert.DoesNotContain("failed", move.Detail);
        Assert.Equal(search.FileCount, move.FileCount);
        Assert.Equal(move.FileCount, Directory.GetFiles(destination).Length);
    }

    [Fact]
    public async Task SearchOutcome_SurfacesUnreadableFiles()
    {
        var directory = Path.Combine(_root, "locked");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "readable.txt"), "alpha match\n");
        var lockedPath = Path.Combine(directory, "locked.log");
        File.WriteAllText(lockedPath, "alpha locked\n");

        // PlainTextExtractor deliberately skips files it cannot open, so the
        // locked file is routed to an extractor that — like the document
        // extractors — lets the sharing violation reach the engine, where it
        // is counted as a failed file.
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain, new StrictLogExtractor() }, plain);
        var runner = new WorkflowRunner(new Searcher(new FileWalker(), registry), new QueryFactory(), registry);
        var workflow = Workflow(new SearchStep { Id = "s1", Query = "alpha", Roots = new[] { directory } });

        WorkflowRunResult result;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await runner.RunAsync(
                workflow, cancellationToken: TestContext.Current.CancellationToken);
        }

        // The run still succeeds, but the unreadable file is surfaced so
        // downstream conditions are not trusted blindly.
        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        var outcome = result.StepOutcomes.Single(o => o.StepId == "s1");
        Assert.True(outcome.Succeeded);
        Assert.Equal(1L, outcome.HitCount);
        Assert.Contains("1 file(s) failed to read", outcome.Detail);
    }

    [Fact]
    public async Task SearchFilters_DefaultExcludeDirectories_PruneNodeModules()
    {
        var paths = await RunFilteredSearchAsync(excludeDirectories: null);

        Assert.Equal(2, paths.Length);
        Assert.Contains(paths, p => p.EndsWith("top.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains(@"\custom\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchFilters_EmptyExcludeDirectories_WalksEverything()
    {
        var paths = await RunFilteredSearchAsync(excludeDirectories: Array.Empty<string>());

        Assert.Equal(3, paths.Length);
        Assert.Contains(paths, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchFilters_ExplicitExcludeDirectories_ReplaceDefaults()
    {
        var paths = await RunFilteredSearchAsync(excludeDirectories: new[] { "custom" });

        // An explicit list replaces the default pruning entirely, so
        // node_modules is back in while "custom" is pruned.
        Assert.Equal(2, paths.Length);
        Assert.Contains(paths, p => p.EndsWith("top.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains(@"\custom\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs a real-engine workflow search for "alpha" over a fresh tree with a
    /// matching file at the top, one in node_modules and one in a "custom"
    /// folder; returns the matched file paths.
    /// </summary>
    private async Task<string[]> RunFilteredSearchAsync(IReadOnlyList<string>? excludeDirectories)
    {
        var root = Path.Combine(_root, "tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(root, "custom"));
        File.WriteAllText(Path.Combine(root, "top.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(root, "node_modules", "dep.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(root, "custom", "extra.txt"), "alpha\n");

        var registry = CreateRegistry();
        var runner = new WorkflowRunner(new Searcher(new FileWalker(), registry), new QueryFactory(), registry);
        var observer = new RecordingObserver();
        var workflow = Workflow(new SearchStep
        {
            Id = "s1",
            Query = "alpha",
            Roots = new[] { root },
            Filters = new SearchFilters { ExcludeDirectories = excludeDirectories },
        });

        var result = await runner.RunAsync(
            workflow, observer: observer, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        return observer.Hits
            .Select(h => h.Hit.Path)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IExtractorRegistry CreateRegistry()
    {
        var plain = new PlainTextExtractor();
        return new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
    }

    private static WorkflowRunner CreateRunner(ISearcher searcher) =>
        new(searcher, new QueryFactory(), CreateRegistry());

    private static WorkflowDefinition Workflow(params WorkflowStep[] steps) =>
        new() { Name = "Test workflow", Steps = steps };

    private static SearchStep Search(string id, string query) =>
        new() { Id = id, Query = query, Roots = new[] { @"C:\fake" } };

    private static Hit MakeHit(string path, int lineNumber = 1, string lineContent = "match") =>
        new(path, lineNumber, lineContent, Array.Empty<MatchSpan>());

    private static string QueryTextOf(SearchRequest request) =>
        Assert.IsType<TermQuery>(request.Expression).Term;

    /// <summary>
    /// Scripted <see cref="ISearcher"/>: records every request and streams the
    /// hits the script returns for it, so tests can assert which searches the
    /// runner issued (query text after substitution, roots, index preference).
    /// </summary>
    private sealed class FakeSearcher : ISearcher
    {
        private readonly Func<SearchRequest, IReadOnlyList<Hit>> _respond;

        public FakeSearcher(Func<SearchRequest, IReadOnlyList<Hit>> respond) => _respond = respond;

        public List<SearchRequest> Requests { get; } = new();

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var hit in _respond(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return hit;
            }
        }
    }

    private sealed class RecordingObserver : IWorkflowObserver
    {
        private readonly object _gate = new();

        public List<string> Events { get; } = new();
        public List<WorkflowStepOutcome> Outcomes { get; } = new();
        public List<(SearchStep Step, Hit Hit)> Hits { get; } = new();
        public List<string> Logs { get; } = new();

        public void OnStepStarted(WorkflowStep step, int depth)
        {
            lock (_gate) Events.Add($"started:{step.Id}");
        }

        public void OnStepCompleted(WorkflowStepOutcome outcome)
        {
            lock (_gate)
            {
                Events.Add($"completed:{outcome.StepId}");
                Outcomes.Add(outcome);
            }
        }

        public void OnHit(SearchStep step, Hit hit)
        {
            lock (_gate)
            {
                Events.Add($"hit:{step.Id}");
                Hits.Add((step, hit));
            }
        }

        public void OnLog(string message)
        {
            lock (_gate) Logs.Add(message);
        }
    }

    private sealed class FakeInteraction : IWorkflowInteraction
    {
        private readonly bool _answer;

        public FakeInteraction(bool answer) => _answer = answer;

        public List<WorkflowConfirmation> Confirmations { get; } = new();

        public Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken)
        {
            Confirmations.Add(confirmation);
            return Task.FromResult(_answer);
        }
    }

    /// <summary>
    /// Reads ".log" files as text but, unlike <see cref="PlainTextExtractor"/>,
    /// does not swallow open failures — mirroring the document extractors,
    /// whose I/O errors the engine counts as failed files.
    /// </summary>
    private sealed class StrictLogExtractor : ITextExtractor
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".log" };

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(path);
            var number = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                yield return new TextLine(++number, line);
        }
    }

    /// <summary>
    /// Interaction that cancels the run while the confirmation prompt is open
    /// and then resolves the prompt to "no" — the answer a closing dialog
    /// produces. The runner must report this as cancellation, not a decline.
    /// </summary>
    private sealed class CancelOnConfirmInteraction : IWorkflowInteraction
    {
        private readonly CancellationTokenSource _cts;

        public CancelOnConfirmInteraction(CancellationTokenSource cts) => _cts = cts;

        public Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken)
        {
            _cts.Cancel();
            return Task.FromResult(false);
        }
    }
}
