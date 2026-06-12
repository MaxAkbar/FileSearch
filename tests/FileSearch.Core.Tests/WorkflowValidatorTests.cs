using System.Linq;
using FileSearch.Core.Workflows;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WorkflowValidatorTests
{
    [Fact]
    public void ValidWorkflow_HasNoErrors()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "Valid",
            Steps = new WorkflowStep[]
            {
                Search("s1"),
                new IfStep
                {
                    Id = "i1",
                    Condition = new WorkflowCondition { Source = "s1", Value = 1 },
                    Then = new WorkflowStep[]
                    {
                        new SearchStep { Id = "s2", Query = "beta", ScopeStepId = "s1" },
                        new ExportStep { Id = "e1", SourceStepId = "s2", Path = @"C:\out\hits.json" },
                    },
                    Else = new WorkflowStep[]
                    {
                        new StopStep { Id = "halt", Succeeded = false, Message = "nothing" },
                    },
                },
                new RetryStep
                {
                    Id = "r1",
                    Body = new WorkflowStep[] { Search("rs") },
                    Until = new WorkflowCondition { Source = "rs", Value = 1 },
                },
                new ForEachStep
                {
                    Id = "f1",
                    SourceStepId = "s1",
                    Body = new WorkflowStep[] { Search("fs") },
                },
                new FileOperationStep { Id = "o1", SourceStepId = "s1", DestinationDirectory = @"C:\sorted" },
                new RunProgramStep { Id = "p1", Program = "notepad.exe" },
            },
        };

        Assert.Empty(WorkflowValidator.Validate(workflow));
    }

    [Fact]
    public void MissingName_IsReported()
    {
        var errors = WorkflowValidator.Validate(new WorkflowDefinition
        {
            Name = "   ",
            Steps = new WorkflowStep[] { Search("s1") },
        });

        var error = Assert.Single(errors);
        Assert.Equal("Workflow needs a name.", error);
    }

    [Fact]
    public void NoSteps_IsReported()
    {
        var errors = WorkflowValidator.Validate(new WorkflowDefinition { Name = "Empty" });

        var error = Assert.Single(errors);
        Assert.Equal("Workflow has no steps.", error);
    }

    [Fact]
    public void StepWithoutId_IsReported()
    {
        var errors = Validate(new SearchStep { Id = "", Name = "find", Query = "x", Roots = new[] { @"C:\" } });

        var error = Assert.Single(errors);
        Assert.Contains("has no id", error);
    }

    [Fact]
    public void DuplicateIds_AreReported()
    {
        var errors = Validate(Search("dup"), Search("dup"));

        var error = Assert.Single(errors);
        Assert.Equal("Duplicate step id 'dup'.", error);
    }

    [Fact]
    public void SearchWithoutQuery_IsReported()
    {
        var errors = Validate(new SearchStep { Id = "s1", Query = " ", Roots = new[] { @"C:\" } });

        var error = Assert.Single(errors);
        Assert.Contains("has no query", error);
    }

    [Fact]
    public void SearchWithoutRootsOrScope_IsReported()
    {
        var errors = Validate(new SearchStep { Id = "s1", Query = "x" });

        var error = Assert.Single(errors);
        Assert.Contains("needs at least one root folder", error);
    }

    [Fact]
    public void SearchScopedToUnknownStep_IsReported()
    {
        var errors = Validate(new SearchStep { Id = "s1", Query = "x", ScopeStepId = "ghost" });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
        Assert.Contains("scope", error);
    }

    [Fact]
    public void SearchScopedToItself_IsReported()
    {
        var errors = Validate(new SearchStep { Id = "s1", Query = "x", ScopeStepId = "s1" });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 's1'", error);
    }

    [Fact]
    public void IfConditionReferencingUnknownStep_IsReported()
    {
        var errors = Validate(new IfStep
        {
            Id = "i1",
            Condition = new WorkflowCondition { Source = "ghost" },
            Then = new WorkflowStep[] { Search("s1") },
        });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
    }

    [Fact]
    public void IfWithoutBranches_IsReported()
    {
        var errors = Validate(new IfStep { Id = "i1" });

        var error = Assert.Single(errors);
        Assert.Contains("neither a then- nor an else-branch", error);
    }

    [Fact]
    public void RetryWithEmptyBody_IsReported()
    {
        var errors = Validate(new RetryStep { Id = "r1" });

        var error = Assert.Single(errors);
        Assert.Contains("empty body", error);
    }

    [Fact]
    public void RetryWithMaxIterationsBelowOne_IsReported()
    {
        var errors = Validate(new RetryStep
        {
            Id = "r1",
            MaxIterations = 0,
            Body = new WorkflowStep[] { Search("s1") },
            Until = new WorkflowCondition { Source = "s1" },
        });

        var error = Assert.Single(errors);
        Assert.Contains("maxIterations of at least 1", error);
    }

    [Fact]
    public void RetryConditionReferencingUnknownStep_IsReported()
    {
        var errors = Validate(new RetryStep
        {
            Id = "r1",
            Body = new WorkflowStep[] { Search("s1") },
            Until = new WorkflowCondition { Source = "ghost" },
        });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
    }

    [Fact]
    public void ForEachWithEmptyBody_IsReported()
    {
        var errors = Validate(Search("s1"), new ForEachStep { Id = "f1" });

        var error = Assert.Single(errors);
        Assert.Contains("empty body", error);
    }

    [Fact]
    public void ForEachWithUnknownSource_IsReported()
    {
        var errors = Validate(new ForEachStep
        {
            Id = "f1",
            SourceStepId = "ghost",
            Body = new WorkflowStep[] { Search("s1") },
        });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
        Assert.Contains("source", error);
    }

    [Fact]
    public void ExportWithoutPath_IsReported()
    {
        var errors = Validate(Search("s1"), new ExportStep { Id = "e1", Path = "" });

        var error = Assert.Single(errors);
        Assert.Contains("has no output path", error);
    }

    [Fact]
    public void ExportWithUnknownSource_IsReported()
    {
        var errors = Validate(new ExportStep { Id = "e1", Path = @"C:\out.json", SourceStepId = "ghost" });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
    }

    [Fact]
    public void FileOperationWithoutDestination_IsReported()
    {
        var errors = Validate(Search("s1"), new FileOperationStep { Id = "o1", DestinationDirectory = " " });

        var error = Assert.Single(errors);
        Assert.Contains("has no destination folder", error);
    }

    [Fact]
    public void FileOperationWithUnknownSource_IsReported()
    {
        var errors = Validate(new FileOperationStep
        {
            Id = "o1",
            DestinationDirectory = @"C:\sorted",
            SourceStepId = "ghost",
        });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
    }

    [Fact]
    public void RunProgramWithoutProgram_IsReported()
    {
        var errors = Validate(Search("s1"), new RunProgramStep { Id = "p1", Program = "" });

        var error = Assert.Single(errors);
        Assert.Contains("has no program", error);
    }

    [Fact]
    public void RunProgramWithUnknownSource_IsReported()
    {
        var errors = Validate(new RunProgramStep { Id = "p1", Program = "notepad.exe", SourceStepId = "ghost" });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 'ghost'", error);
    }

    [Fact]
    public void ExportReferencingLaterSearch_IsReported()
    {
        // The source exists, but only runs after the export — references must
        // point at search steps that run before the referencing step.
        var errors = Validate(
            new ExportStep { Id = "e1", Path = @"C:\out.json", SourceStepId = "s1" },
            Search("s1"));

        var error = Assert.Single(errors);
        Assert.Contains("references search step 's1'", error);
        Assert.Contains("runs before it", error);
    }

    [Fact]
    public void IfConditionReferencingSearchInsideItsOwnThenBranch_IsReported()
    {
        // The condition evaluates before either branch runs, so a search
        // inside the then-branch can never supply it.
        var errors = Validate(new IfStep
        {
            Id = "i1",
            Condition = new WorkflowCondition { Source = "t1" },
            Then = new WorkflowStep[] { Search("t1") },
        });

        var error = Assert.Single(errors);
        Assert.Contains("references search step 't1'", error);
        Assert.Contains("runs before it", error);
    }

    [Fact]
    public void RetryConditionReferencingSearchInsideItsOwnBody_IsValid()
    {
        // The exit condition evaluates after each iteration, so searches in
        // the body are legitimate sources.
        var errors = Validate(new RetryStep
        {
            Id = "r1",
            Body = new WorkflowStep[] { Search("rs") },
            Until = new WorkflowCondition { Source = "rs", Value = 1 },
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void RetryWithMoreParameterSetsThanIterations_IsReported()
    {
        var errors = Validate(new RetryStep
        {
            Id = "r1",
            MaxIterations = 1,
            ParameterSets = new IReadOnlyDictionary<string, string>[]
            {
                new Dictionary<string, string> { ["term"] = "narrow" },
                new Dictionary<string, string> { ["term"] = "wide" },
            },
            Body = new WorkflowStep[] { Search("rs") },
            Until = new WorkflowCondition { Source = "rs" },
        });

        var error = Assert.Single(errors);
        Assert.Contains("maxIterations", error);
    }

    [Fact]
    public void NullStepEntry_IsReportedWithoutThrowing()
    {
        var errors = Validate(Search("s1"), null!);

        var error = Assert.Single(errors);
        Assert.Contains("null", error);
    }

    [Fact]
    public void NestedSteps_AreValidated()
    {
        // The duplicate id and the missing query are both buried in nested
        // bodies; validation must flatten to find them.
        var errors = Validate(
            Search("dup"),
            new IfStep
            {
                Id = "i1",
                Condition = new WorkflowCondition { Source = "dup" },
                Then = new WorkflowStep[] { Search("dup") },
                Else = new WorkflowStep[]
                {
                    new RetryStep
                    {
                        Id = "r1",
                        Body = new WorkflowStep[]
                        {
                            new SearchStep { Id = "deep", Query = "", Roots = new[] { @"C:\" } },
                        },
                        Until = new WorkflowCondition { Source = "deep" },
                    },
                },
            });

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Duplicate step id 'dup'"));
        Assert.Contains(errors, e => e.Contains("'deep'") && e.Contains("has no query"));
    }

    [Fact]
    public void Flatten_ReturnsNestedStepsInDocumentOrder()
    {
        var steps = new WorkflowStep[]
        {
            Search("s1"),
            new IfStep
            {
                Id = "i1",
                Condition = new WorkflowCondition { Source = "s1" },
                Then = new WorkflowStep[] { Search("t1") },
                Else = new WorkflowStep[]
                {
                    new ForEachStep
                    {
                        Id = "f1",
                        Body = new WorkflowStep[] { Search("fb1") },
                    },
                },
            },
            new RetryStep
            {
                Id = "r1",
                Body = new WorkflowStep[] { Search("b1") },
                Until = new WorkflowCondition { Source = "b1" },
            },
        };

        var ids = WorkflowValidator.Flatten(steps).Select(s => s.Id).ToArray();

        Assert.Equal(new[] { "s1", "i1", "t1", "f1", "fb1", "r1", "b1" }, ids);
    }

    private static SearchStep Search(string id) =>
        new() { Id = id, Query = "needle", Roots = new[] { @"C:\data" } };

    private static IReadOnlyList<string> Validate(params WorkflowStep[] steps) =>
        WorkflowValidator.Validate(new WorkflowDefinition { Name = "Test", Steps = steps });
}
