using System.Linq;
using FileSearch.Core.Queries;
using FileSearch.Core.Workflows;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WorkflowJsonTests
{
    [Fact]
    public void RoundTrip_EveryStepKind_PreservesAllProperties()
    {
        var original = BuildWorkflowWithEveryStepKind();

        var json = WorkflowJson.Serialize(original);
        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Description, parsed.Description);
        AssertStepsEqual(original.Steps, parsed.Steps);
    }

    [Fact]
    public void Serialize_WritesTypeDiscriminatorForEveryStepKind()
    {
        var json = WorkflowJson.Serialize(BuildWorkflowWithEveryStepKind());

        Assert.Contains("\"type\": \"search\"", json);
        Assert.Contains("\"type\": \"if\"", json);
        Assert.Contains("\"type\": \"retry\"", json);
        Assert.Contains("\"type\": \"forEach\"", json);
        Assert.Contains("\"type\": \"export\"", json);
        Assert.Contains("\"type\": \"fileOperation\"", json);
        Assert.Contains("\"type\": \"runProgram\"", json);
        Assert.Contains("\"type\": \"stop\"", json);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var json = WorkflowJson.Serialize(BuildWorkflowWithEveryStepKind());

        Assert.Contains("\"maxIterations\"", json);
        Assert.Contains("\"scopeStepId\"", json);
        Assert.Contains("\"destinationDirectory\"", json);
        Assert.Contains("\"parameterSets\"", json);
        Assert.DoesNotContain("\"MaxIterations\"", json);
        Assert.DoesNotContain("\"Query\"", json);
    }

    [Fact]
    public void Serialize_UsesCamelCaseStringEnums()
    {
        var json = WorkflowJson.Serialize(BuildWorkflowWithEveryStepKind());

        Assert.Contains("\"mode\": \"regex\"", json);
        Assert.Contains("\"metric\": \"fileCount\"", json);
        Assert.Contains("\"operator\": \"lessThan\"", json);
        Assert.Contains("\"format\": \"csv\"", json);
        Assert.Contains("\"operation\": \"move\"", json);
        Assert.Contains("\"collision\": \"overwrite\"", json);
    }

    [Fact]
    public void TryDeserialize_Garbage_ReturnsNullAndError()
    {
        var parsed = WorkflowJson.TryDeserialize("{{{ this is not json", out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.StartsWith("Invalid workflow JSON", error);
    }

    [Fact]
    public void TryDeserialize_JsonNull_ReturnsNullAndError()
    {
        var parsed = WorkflowJson.TryDeserialize("null", out var error);

        Assert.Null(parsed);
        Assert.Equal("File contains no workflow.", error);
    }

    [Fact]
    public void TryDeserialize_UnknownStepType_ReturnsNullAndError()
    {
        const string json = """{"version":1,"name":"x","steps":[{"type":"teleport","id":"a"}]}""";

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.StartsWith("Invalid workflow JSON", error);
    }

    [Fact]
    public void TryDeserialize_StepMissingType_ReturnsNullAndErrorMentioningType()
    {
        const string json = """{"version":1,"name":"x","steps":[{"id":"a","query":"q"}]}""";

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("\"type\"", error);
    }

    [Fact]
    public void TryDeserialize_UnknownStepType_ErrorMentionsType()
    {
        const string json = """{"version":1,"name":"x","steps":[{"type":"teleport","id":"a"}]}""";

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("type", error);
    }

    [Fact]
    public void TryDeserialize_NullStepEntry_ReturnsNullAndError()
    {
        // A trailing "null" in the steps array (easy to produce by hand
        // editing) must be rejected with a friendly message, not parsed into
        // a workflow that explodes later.
        const string json = """{"version":1,"name":"x","steps":[{"type":"stop","id":"a"},null]}""";

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("null step", error);
    }

    [Fact]
    public void TryDeserialize_NullStepNestedInIfBranch_ReturnsNullAndError()
    {
        const string json = """
            {
              "version": 1,
              "name": "x",
              "steps": [
                {
                  "type": "if",
                  "id": "i1",
                  "condition": { "value": 1 },
                  "then": [ null ]
                }
              ]
            }
            """;

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("null step", error);
    }

    [Fact]
    public void TryDeserialize_NewerVersion_ReturnsNullAndError()
    {
        const string json = """{"version":99,"name":"future","steps":[]}""";

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("99", error);
        Assert.Contains("newer", error);
    }

    [Fact]
    public void TryDeserialize_HandWrittenCamelCaseJson_Parses()
    {
        const string json = """
            {
              "version": 1,
              "name": "Find TODOs",
              "description": "Looks for TODO markers.",
              "steps": [
                {
                  "type": "search",
                  "id": "find",
                  "query": "TODO",
                  "mode": "plainText",
                  "roots": ["C:\\src"],
                  "maxHits": 50
                },
                {
                  "type": "if",
                  "id": "check",
                  "condition": { "source": "find", "metric": "hitCount", "operator": "equals", "value": 0 },
                  "then": [ { "type": "stop", "id": "halt", "succeeded": false, "message": "Nothing to do." } ]
                }
              ]
            }
            """;

        var parsed = WorkflowJson.TryDeserialize(json, out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("Find TODOs", parsed.Name);
        Assert.Equal(2, parsed.Steps.Count);

        var search = Assert.IsType<SearchStep>(parsed.Steps[0]);
        Assert.Equal("TODO", search.Query);
        Assert.Equal(QueryMode.PlainText, search.Mode);
        Assert.Equal(50, search.MaxHits);

        var ifStep = Assert.IsType<IfStep>(parsed.Steps[1]);
        Assert.Equal("find", ifStep.Condition.Source);
        Assert.Equal(ConditionOperator.Equals, ifStep.Condition.Operator);
        var stop = Assert.IsType<StopStep>(Assert.Single(ifStep.Then));
        Assert.False(stop.Succeeded);
        Assert.Equal("Nothing to do.", stop.Message);
    }

    private static WorkflowDefinition BuildWorkflowWithEveryStepKind() => new()
    {
        Name = "Everything",
        Description = "Exercises every step kind with non-default values.",
        Steps = new WorkflowStep[]
        {
            new SearchStep
            {
                Id = "s1",
                Name = "Find alpha",
                Query = @"alpha\d+",
                Mode = QueryMode.Regex,
                CaseSensitive = true,
                Roots = new[] { @"C:\data", @"D:\docs" },
                UseIndex = true,
                MaxHits = 25,
                Filters = new SearchFilters
                {
                    IncludeGlobs = new[] { "*.txt", "*.md" },
                    ExcludeGlobs = new[] { "*.log" },
                    Recursive = false,
                    IncludeHidden = true,
                    MinFileSizeBytes = 16,
                    MaxFileSizeBytes = 4096,
                    ModifiedAfterUtc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
                    ModifiedBeforeUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                },
            },
            new IfStep
            {
                Id = "i1",
                Name = "Branch on file count",
                Condition = new WorkflowCondition
                {
                    Source = "s1",
                    Metric = ConditionMetric.FileCount,
                    Operator = ConditionOperator.LessThan,
                    Value = 3,
                },
                Then = new WorkflowStep[]
                {
                    new StopStep { Id = "halt", Succeeded = false, Message = "Too few files." },
                },
                Else = new WorkflowStep[]
                {
                    new SearchStep { Id = "s2", Query = "beta", ScopeStepId = "s1" },
                },
            },
            new RetryStep
            {
                Id = "r1",
                Name = "Broaden and retry",
                MaxIterations = 4,
                Body = new WorkflowStep[]
                {
                    new SearchStep { Id = "rs", Query = "probe ${term}", Roots = new[] { @"C:\data" } },
                },
                Until = new WorkflowCondition
                {
                    Source = "rs",
                    Metric = ConditionMetric.HitCount,
                    Operator = ConditionOperator.GreaterThan,
                    Value = 10,
                },
                ParameterSets = new IReadOnlyDictionary<string, string>[]
                {
                    new Dictionary<string, string> { ["term"] = "narrow", ["glob"] = "*.cs" },
                    new Dictionary<string, string> { ["term"] = "wide", ["glob"] = "*.*" },
                },
            },
            new ForEachStep
            {
                Id = "f1",
                SourceStepId = "s1",
                MaxItems = 7,
                Body = new WorkflowStep[]
                {
                    new SearchStep { Id = "fs", Query = "secret in ${fileName}", ScopeStepId = "s1" },
                },
            },
            new ExportStep
            {
                Id = "e1",
                SourceStepId = "s1",
                Format = ExportFormat.Csv,
                Path = @"C:\out\hits.csv",
                Overwrite = false,
            },
            new FileOperationStep
            {
                Id = "o1",
                Operation = FileOperationKind.Move,
                SourceStepId = "s1",
                DestinationDirectory = @"C:\sorted\${fileName}",
                Collision = FileCollisionPolicy.Overwrite,
            },
            new RunProgramStep
            {
                Id = "p1",
                Program = "notepad.exe",
                Arguments = "--open ${file}",
                PerFile = true,
                SourceStepId = "s1",
                WorkingDirectory = @"C:\work",
                WaitForExit = false,
                TimeoutSeconds = 5,
                MaxFiles = 3,
            },
            new StopStep { Id = "done", Succeeded = true, Message = "All done." },
        },
    };

    private static void AssertStepsEqual(IReadOnlyList<WorkflowStep> expected, IReadOnlyList<WorkflowStep> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            AssertStepEqual(expected[i], actual[i]);
    }

    private static void AssertStepEqual(WorkflowStep expected, WorkflowStep actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);

        switch (expected)
        {
            case SearchStep e:
                var search = (SearchStep)actual;
                Assert.Equal(e.Query, search.Query);
                Assert.Equal(e.Mode, search.Mode);
                Assert.Equal(e.CaseSensitive, search.CaseSensitive);
                Assert.Equal(e.Roots, search.Roots);
                Assert.Equal(e.ScopeStepId, search.ScopeStepId);
                Assert.Equal(e.UseIndex, search.UseIndex);
                Assert.Equal(e.MaxHits, search.MaxHits);
                AssertFiltersEqual(e.Filters, search.Filters);
                break;

            case IfStep e:
                var ifStep = (IfStep)actual;
                Assert.Equal(e.Condition, ifStep.Condition);
                AssertStepsEqual(e.Then, ifStep.Then);
                AssertStepsEqual(e.Else, ifStep.Else);
                break;

            case RetryStep e:
                var retry = (RetryStep)actual;
                Assert.Equal(e.Until, retry.Until);
                Assert.Equal(e.MaxIterations, retry.MaxIterations);
                Assert.Equal(e.ParameterSets.Count, retry.ParameterSets.Count);
                for (var i = 0; i < e.ParameterSets.Count; i++)
                {
                    Assert.Equal(
                        e.ParameterSets[i].OrderBy(p => p.Key, StringComparer.Ordinal),
                        retry.ParameterSets[i].OrderBy(p => p.Key, StringComparer.Ordinal));
                }
                AssertStepsEqual(e.Body, retry.Body);
                break;

            case ForEachStep e:
                var forEach = (ForEachStep)actual;
                Assert.Equal(e.SourceStepId, forEach.SourceStepId);
                Assert.Equal(e.MaxItems, forEach.MaxItems);
                AssertStepsEqual(e.Body, forEach.Body);
                break;

            case ExportStep e:
                var export = (ExportStep)actual;
                Assert.Equal(e.SourceStepId, export.SourceStepId);
                Assert.Equal(e.Format, export.Format);
                Assert.Equal(e.Path, export.Path);
                Assert.Equal(e.Overwrite, export.Overwrite);
                break;

            case FileOperationStep e:
                var operation = (FileOperationStep)actual;
                Assert.Equal(e.Operation, operation.Operation);
                Assert.Equal(e.SourceStepId, operation.SourceStepId);
                Assert.Equal(e.DestinationDirectory, operation.DestinationDirectory);
                Assert.Equal(e.Collision, operation.Collision);
                break;

            case RunProgramStep e:
                var program = (RunProgramStep)actual;
                Assert.Equal(e.Program, program.Program);
                Assert.Equal(e.Arguments, program.Arguments);
                Assert.Equal(e.PerFile, program.PerFile);
                Assert.Equal(e.SourceStepId, program.SourceStepId);
                Assert.Equal(e.WorkingDirectory, program.WorkingDirectory);
                Assert.Equal(e.WaitForExit, program.WaitForExit);
                Assert.Equal(e.TimeoutSeconds, program.TimeoutSeconds);
                Assert.Equal(e.MaxFiles, program.MaxFiles);
                break;

            case StopStep e:
                var stop = (StopStep)actual;
                Assert.Equal(e.Succeeded, stop.Succeeded);
                Assert.Equal(e.Message, stop.Message);
                break;

            default:
                Assert.Fail($"Unhandled step kind '{expected.Kind}' — extend the test.");
                break;
        }
    }

    private static void AssertFiltersEqual(SearchFilters expected, SearchFilters actual)
    {
        Assert.Equal(expected.IncludeGlobs, actual.IncludeGlobs);
        Assert.Equal(expected.ExcludeGlobs, actual.ExcludeGlobs);
        Assert.Equal(expected.Recursive, actual.Recursive);
        Assert.Equal(expected.IncludeHidden, actual.IncludeHidden);
        Assert.Equal(expected.MinFileSizeBytes, actual.MinFileSizeBytes);
        Assert.Equal(expected.MaxFileSizeBytes, actual.MaxFileSizeBytes);
        Assert.Equal(expected.ModifiedAfterUtc, actual.ModifiedAfterUtc);
        Assert.Equal(expected.ModifiedBeforeUtc, actual.ModifiedBeforeUtc);
    }
}
