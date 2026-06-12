using FileSearch.Core.Queries;
using FileSearch.Core.Workflows;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// Pins the workflow editor's definition → step view models → definition
/// mapping: a workflow exercising every step kind with non-default values
/// (nested if/retry/forEach bodies included) must rebuild byte-identical.
/// Catches a new model property that the editor silently drops or defaults.
/// </summary>
public sealed class WorkflowEditorRoundTripTests
{
    [Fact]
    public void EveryStepKindSurvivesEditorRoundTrip()
    {
        var original = BuildSampleDefinition();
        var viewModel = CreateViewModel();

        viewModel.LoadDefinition(original, fileName: null);
        var rebuilt = viewModel.BuildDefinition();

        // Records compare list-typed properties by reference, so compare the
        // canonical JSON instead — it covers every serialized property of
        // every step, including the nested bodies.
        Assert.Equal(WorkflowJson.Serialize(original), WorkflowJson.Serialize(rebuilt));
    }

    private static WorkflowsViewModel CreateViewModel() => new(
        new FakeWorkflowStore(),
        new FakeWorkflowRunner(),
        new FakeFileLauncher(),
        new FakeFolderPicker());

    private static WorkflowDefinition BuildSampleDefinition() => new()
    {
        Name = "Round trip",
        Description = "Exercises every step kind",
        Steps = new WorkflowStep[]
        {
            new SearchStep
            {
                Id = "search-1",
                Name = "Find todos",
                Query = "TODO AND NOT done",
                Mode = QueryMode.Boolean,
                CaseSensitive = true,
                Roots = new[] { @"C:\src", @"D:\notes" },
                UseIndex = true,
                Filters = new SearchFilters
                {
                    IncludeGlobs = new[] { "*.cs", "*.md" },
                    ExcludeGlobs = new[] { "bin", "obj" },
                    ExcludeDirectories = new[] { "packages", "artifacts" },
                    Recursive = false,
                    IncludeHidden = true,
                    MinFileSizeBytes = 10,
                    MaxFileSizeBytes = 123_456,
                    // Times chosen so neither converts to local midnight —
                    // the editor maps a midnight "before" to end-of-day.
                    ModifiedAfterUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    ModifiedBeforeUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                },
                MaxHits = 250,
            },
            new RetryStep
            {
                Id = "retry-1",
                Name = "Broaden until enough files",
                MaxIterations = 2,
                Until = new WorkflowCondition
                {
                    Source = "search-2",
                    Metric = ConditionMetric.FileCount,
                    Operator = ConditionOperator.GreaterThan,
                    Value = 4,
                },
                ParameterSets = new IReadOnlyDictionary<string, string>[]
                {
                    new Dictionary<string, string> { ["pattern"] = "TODO" },
                    new Dictionary<string, string> { ["pattern"] = "FIXME", ["depth"] = "2" },
                },
                Body = new WorkflowStep[]
                {
                    new SearchStep
                    {
                        Id = "search-2",
                        Query = "${pattern}",
                        Roots = new[] { @"C:\src" },
                    },
                },
            },
            new SearchStep
            {
                Id = "search-3",
                Name = "Narrow into matches",
                Query = "password",
                ScopeStepId = "search-1",
                // Empty list (walk everything) must survive the editor
                // distinctly from null (engine defaults).
                Filters = new SearchFilters { ExcludeDirectories = Array.Empty<string>() },
            },
            new IfStep
            {
                Id = "if-1",
                Name = "Anything found?",
                Condition = new WorkflowCondition
                {
                    Source = "search-1",
                    Metric = ConditionMetric.HitCount,
                    Operator = ConditionOperator.GreaterOrEqual,
                    Value = 1,
                },
                Then = new WorkflowStep[]
                {
                    new ForEachStep
                    {
                        Id = "forEach-1",
                        Name = "Per matched file",
                        SourceStepId = "search-1",
                        MaxItems = 25,
                        Body = new WorkflowStep[]
                        {
                            new RunProgramStep
                            {
                                Id = "runProgram-1",
                                Name = "Open match",
                                Program = "notepad.exe",
                                Arguments = "\"${file}\" /readonly",
                                PerFile = true,
                                SourceStepId = "search-1",
                                WorkingDirectory = @"C:\work",
                                WaitForExit = false,
                                TimeoutSeconds = 15,
                                MaxFiles = 7,
                            },
                        },
                    },
                    new ExportStep
                    {
                        Id = "export-1",
                        Name = "Write report",
                        SourceStepId = "search-1",
                        Format = ExportFormat.Markdown,
                        Path = @"C:\reports\todos.md",
                        Overwrite = false,
                    },
                },
                Else = new WorkflowStep[]
                {
                    new StopStep
                    {
                        Id = "stop-1",
                        Name = "Nothing to do",
                        Succeeded = false,
                        Message = "No hits found.",
                    },
                },
            },
            new FileOperationStep
            {
                Id = "fileOperation-1",
                Name = "Archive matches",
                Operation = FileOperationKind.Move,
                SourceStepId = "search-1",
                DestinationDirectory = @"C:\archive",
                Collision = FileCollisionPolicy.Skip,
            },
        },
    };

    private sealed class FakeWorkflowStore : IWorkflowStore
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "FileSearchGuiTests", "Workflows");

        public IReadOnlyList<WorkflowSummary> List() => Array.Empty<WorkflowSummary>();

        public WorkflowDefinition? TryLoad(string fileName, out string? error)
        {
            error = $"Workflow file not found: {fileName}";
            return null;
        }

        public string Save(WorkflowDefinition workflow, string? fileName = null) =>
            fileName ?? JsonWorkflowStore.ToFileName(workflow.Name);

        public void Delete(string fileName)
        {
        }

        public string GetFullPath(string fileName) => Path.Combine(DirectoryPath, fileName);
    }

    private sealed class FakeWorkflowRunner : IWorkflowRunner
    {
        public Task<WorkflowRunResult> RunAsync(
            WorkflowDefinition workflow,
            WorkflowRunOptions? options = null,
            IWorkflowObserver? observer = null,
            IWorkflowInteraction? interaction = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowRunResult { Status = WorkflowRunStatus.Completed, Succeeded = true });
    }
}
