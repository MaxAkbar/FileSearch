using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Indexing;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Core.Workflows;

namespace FileSearch.Core.Tests;

public sealed class WorkflowReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workflow-replace-" + Guid.NewGuid().ToString("N"));
    private readonly ReplacementOptions _options;
    private readonly ReplacementService _service;
    private readonly WorkflowRunner _runner;
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public WorkflowReplacementTests()
    {
        Directory.CreateDirectory(_root);
        _options = new() { BackupDirectory = Path.Combine(_root, "recovery") };
        _service = new(_options);
        var text = new PlainTextExtractor(); var registry = new ExtractorRegistry([text], text);
        _runner = new(new Searcher(new FileWalker(), registry), new QueryFactory(), registry, replacement: _service);
    }
    public void Dispose() { _service.Dispose(); Directory.Delete(_root, true); }
    private string PathFor(string name) => Path.Combine(_root, name);
    private WorkflowDefinition Workflow(params WorkflowStep[] steps) => new() { Name = "Cleanup", Steps = steps };
    private ReplacementStep Replace(string id = "replace", string find = "needle", string replacement = "thread") =>
        new() { Id = id, Find = find, ReplaceWith = replacement, Roots = [_root] };

    [Fact]
    public async Task DryRunProducesExactPreviewsAndDoesNotCreateBackups()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle needle", Token);
        var result = await _runner.RunAsync(Workflow(Replace()), new() { DryRun = true }, cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Equal(2, Assert.Single(result.StepOutcomes).ChangeCount);
        Assert.Empty(result.ReplacementBatches); Assert.False(Directory.Exists(_options.BackupDirectory));
        Assert.Equal("needle needle", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
    }

    [Fact]
    public async Task HeadlessAndGenericConfirmationRequireExplicitApplyAuthorization()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        Assert.False((await _runner.RunAsync(Workflow(Replace()), cancellationToken: Token)).Succeeded);
        var generic = new GenericInteraction();
        Assert.False((await _runner.RunAsync(Workflow(Replace()), interaction: generic, cancellationToken: Token)).Succeeded);
        Assert.Equal(0, generic.Calls);
        Assert.True((await _runner.RunAsync(Workflow(Replace()), new() { AllowReplacementApply = true }, interaction: generic, cancellationToken: Token)).Succeeded);
        Assert.Equal(1, generic.Calls);
        Assert.Equal("thread", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
    }

    [Fact]
    public async Task InteractiveReviewAppliesOnlyApprovedItems()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token); await File.WriteAllTextAsync(PathFor("b.txt"), "needle", Token);
        var review = new ReviewInteraction(plan => plan.Items.Where(item => item.Path == PathFor("a.txt")).Select(item => item.Id).ToHashSet());
        var result = await _runner.RunAsync(Workflow(Replace()), interaction: review, cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Equal(1, Assert.Single(result.ReplacementBatches).SucceededCount);
        Assert.Equal("thread", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("b.txt"), Token));
    }

    [Fact]
    public async Task DecliningReviewWritesNothing()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(Replace()), interaction: new ReviewInteraction(_ => null), cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Empty(result.ReplacementBatches);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
    }

    [Fact]
    public async Task ChangesMadeDuringReviewAreRevalidatedAndPartialFailureIsReported()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token); await File.WriteAllTextAsync(PathFor("b.txt"), "needle", Token);
        var review = new ReviewInteraction(plan => { File.WriteAllText(PathFor("a.txt"), "external"); return plan.Items.Select(item => item.Id).ToHashSet(); });
        var result = await _runner.RunAsync(Workflow(Replace()), interaction: review, cancellationToken: Token);
        Assert.False(result.Succeeded); Assert.Equal(1, Assert.Single(result.ReplacementBatches).SucceededCount);
        Assert.Equal(1, (await _service.GetLastRecoveryGroupAsync(Token))!.SkippedItemCount);
        Assert.Equal("external", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
        Assert.Equal(1, (await _service.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
    }

    [Fact]
    public async Task EarlierSearchScopesOnlyItsExactFiles()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "candidate needle", Token); await File.WriteAllTextAsync(PathFor("b.txt"), "needle", Token);
        var search = new SearchStep { Id = "search", Query = "candidate", Roots = [_root] };
        var result = await _runner.RunAsync(Workflow(search, Replace() with { Roots = [], ScopeStepId = "search" }), new() { AllowReplacementApply = true }, cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Equal(1, Assert.Single(result.ReplacementBatches).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("b.txt"), Token));
    }

    [Fact]
    public async Task LimitedSearchCannotBecomeAReplacementScope()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle needle", Token);
        var result = await _runner.RunAsync(Workflow(new SearchStep { Id = "search", Query = "needle", Roots = [_root], MaxHits = 1 },
            Replace() with { ScopeStepId = "search", Roots = [] }), new() { AllowReplacementApply = true }, cancellationToken: Token);
        Assert.False(result.Succeeded); Assert.Contains("limit", result.Message!); Assert.Empty(result.ReplacementBatches);
    }

    [Fact]
    public async Task BufferedHitLimitDoesNotTruncateTheDistinctFileScope()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token); await File.WriteAllTextAsync(PathFor("b.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(new SearchStep { Id = "search", Query = "needle", Roots = [_root] },
            Replace() with { ScopeStepId = "search", Roots = [] }), new() { AllowReplacementApply = true, MaxBufferedHitsPerStep = 1 }, cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Equal(2, Assert.Single(result.ReplacementBatches).SucceededCount);
    }

    [Fact]
    public async Task RenameUpdatesPathsForLaterExportAndReplacementSteps()
    {
        await File.WriteAllTextAsync(PathFor("needle.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(new SearchStep { Id = "search", Query = "needle", Roots = [_root] },
            Replace("names") with { Target = ReplacementTarget.Names, ScopeStepId = "search", Roots = [] },
            Replace("contents") with { ScopeStepId = "search", Roots = [] },
            new ExportStep { Id = "export", SourceStepId = "search", Path = PathFor("result.json") }), new() { AllowReplacementApply = true }, cancellationToken: Token);
        Assert.True(result.Succeeded); Assert.Equal(2, result.ReplacementBatches.Count);
        Assert.Equal("thread", await File.ReadAllTextAsync(PathFor("thread.txt"), Token));
        using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(PathFor("result.json"), Token));
        Assert.Equal(PathFor("thread.txt"), exported.RootElement.GetProperty("hits")[0].GetProperty("path").GetString());
        Assert.Equal(2, (await _service.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("needle.txt"), Token));
    }

    [Fact]
    public async Task NestedRenameUpdatesForEachVariablesAndRemainingItems()
    {
        Directory.CreateDirectory(PathFor("needle"));
        await File.WriteAllTextAsync(PathFor("needle/needle-a.txt"), "needle", Token);
        await File.WriteAllTextAsync(PathFor("needle/needle-b.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(new SearchStep { Id = "search", Query = "needle", Roots = [_root] },
            new ForEachStep
            {
                Id = "each", SourceStepId = "search", Body =
                [
                    new RetryStep { Id = "retry", MaxIterations = 1, Until = new() { Source = "search" },
                        Body = [Replace("names") with { Target = ReplacementTarget.Names }] },
                    Replace("contents") with { Roots = ["${directory}"], ReplaceWith = "${file}",
                        Filters = new() { IncludeGlobs = ["${fileName}"] } }
                ]
            }), new() { AllowReplacementApply = true }, cancellationToken: Token);
        Assert.True(result.Succeeded, result.Message);
        foreach (var name in new[] { "a", "b" })
            Assert.Equal(Path.GetFullPath(PathFor($"thread/thread-{name}.txt")), await File.ReadAllTextAsync(PathFor($"thread/thread-{name}.txt"), Token));
        Assert.Equal(5, (await _service.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("needle/needle-b.txt"), Token));
    }

    [Fact]
    public async Task SeveralContentStepsUndoInReverseAfterRestart()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(Replace("one"), Replace("two", "thread", "done")), new() { AllowReplacementApply = true }, cancellationToken: Token);
        using var restarted = new ReplacementService(_options);
        var group = await restarted.GetLastRecoveryGroupAsync(Token); Assert.NotNull(group); Assert.Equal(result.RecoveryGroupId, group.Id); Assert.Equal(2, group.BatchIds.Count);
        Assert.Equal(2, group.ChangeCount); Assert.Equal(0, group.SkippedItemCount);
        Assert.Equal(2, (await restarted.UndoGroupAsync(group.Id, Token)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
        Assert.Null(await restarted.GetLastRecoveryGroupAsync(Token));
    }

    [Fact]
    public async Task UndoConflictRetainsEarlierDependentBatches()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(Replace("one"), Replace("two", "thread", "done")), new() { AllowReplacementApply = true }, cancellationToken: Token);
        await File.WriteAllTextAsync(PathFor("a.txt"), "external", Token);
        Assert.Equal(0, (await _service.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
        Assert.Equal("external", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
        Assert.NotNull(await _service.GetLastRecoveryGroupAsync(Token));
    }

    [Fact]
    public async Task NestedRenameThenContentUndoKeepsVerifiedIdentitiesAcrossRestart()
    {
        Directory.CreateDirectory(PathFor("needle"));
        await File.WriteAllTextAsync(PathFor("needle/needle.txt"), "needle", Token);
        var result = await _runner.RunAsync(Workflow(Replace("names") with { Target = ReplacementTarget.Names }, Replace("contents")),
            new() { AllowReplacementApply = true }, cancellationToken: Token);
        Assert.True(result.Succeeded);
        // The main screen can undo the most recent content batch before workflow Undo resumes.
        Assert.Equal(1, (await _service.UndoAsync(Token)).SucceededCount);
        using var restarted = new ReplacementService(_options);
        Assert.Equal(2, (await restarted.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
        Assert.Equal("needle", await File.ReadAllTextAsync(PathFor("needle/needle.txt"), Token));
    }

    [Fact]
    public async Task CancellingAfterACompletedStepRecordsItsUndoableBatch()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var observer = new CancelAfterReplacement(cancellation);
        var result = await _runner.RunAsync(Workflow(Replace("one"), Replace("two", "thread", "done")),
            new() { AllowReplacementApply = true }, observer, cancellationToken: cancellation.Token);
        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status); Assert.Single(result.ReplacementBatches);
        Assert.Equal("thread", await File.ReadAllTextAsync(PathFor("a.txt"), Token));
        Assert.Equal(1, (await _service.UndoGroupAsync(result.RecoveryGroupId!, Token)).SucceededCount);
    }

    [Fact]
    public async Task ExplicitScopesCannotModifyRecoveryFiles()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        await _runner.RunAsync(Workflow(Replace()), new() { AllowReplacementApply = true }, cancellationToken: Token);
        var journal = Path.Combine(_options.BackupDirectory, "latest.json");
        var plan = await _service.PreviewAsync(new([_root], new(), "latest", "changed", ReplacementTarget.Names, SourcePaths: [journal]), Token);
        Assert.False(Assert.Single(plan.Items).CanApply); Assert.Contains("recovery", plan.Items[0].SkipReason!);
    }

    [Fact]
    public async Task GroupUndoPreservesAnUnrelatedMainScreenUndoTarget()
    {
        await File.WriteAllTextAsync(PathFor("a.txt"), "needle", Token);
        var workflow = await _runner.RunAsync(Workflow(Replace()), new() { AllowReplacementApply = true }, cancellationToken: Token);
        await File.WriteAllTextAsync(PathFor("b.txt"), "other", Token);
        var plan = await _service.PreviewAsync(new([_root], new(), "other", "changed"), Token);
        await _service.ApplyAsync(plan, plan.Items.Where(item => item.CanApply).Select(item => item.Id).ToHashSet(), Token);
        Assert.Equal(1, (await _service.UndoGroupAsync(workflow.RecoveryGroupId!, Token)).SucceededCount);
        Assert.True(await _service.CanUndoAsync(Token)); Assert.Equal(1, (await _service.UndoAsync(Token)).SucceededCount);
        Assert.Equal("other", await File.ReadAllTextAsync(PathFor("b.txt"), Token));
    }

    [Fact]
    public void SavedIndexProfileAndQueuedRefreshPreserveOcrAndScope()
    {
        var options = new WalkerOptions { EnableOcr = true, Recursive = false, IncludeHidden = true, IncludeExtensions = new HashSet<string>([".txt", ".png"], StringComparer.OrdinalIgnoreCase),
            ExcludeDirectories = new HashSet<string>(["ignored"], StringComparer.OrdinalIgnoreCase) };
        var profile = IndexProfile.FromWalkerOptions(options).ToStorageString();
        var restored = new IndexedLocationInfo(_root, 0, 0, null, profile, true).GetWalkerOptions();
        Assert.NotNull(restored); Assert.True(restored.EnableOcr); Assert.False(restored.Recursive); Assert.True(restored.IncludeHidden);
        var queued = BackgroundIndexedLocation.FromIndexedLocation(new(_root, restored, true)).ToIndexedLocation();
        Assert.True(queued.WalkerOptions.EnableOcr); Assert.False(queued.WalkerOptions.Recursive);
        Assert.Contains("ignored", queued.WalkerOptions.ExcludeDirectories); Assert.Contains(".png", queued.WalkerOptions.IncludeExtensions);
    }

    private sealed class CancelAfterReplacement(CancellationTokenSource cancellation) : IWorkflowObserver
    {
        public void OnStepStarted(WorkflowStep step, int depth) { }
        public void OnStepCompleted(WorkflowStepOutcome outcome) { if (outcome.StepKind == "replace") cancellation.Cancel(); }
        public void OnHit(SearchStep step, Hit hit) { }
        public void OnLog(string message) { }
    }

    [Fact]
    public void ReplacementJsonAndValidationPreserveOptions()
    {
        var original = Workflow(Replace() with { UseRegex = true, Find = "(needle)", ReplaceWith = "$1!", IncludeFormulas = true, IncludeExtensions = true,
            NameTarget = ReplacementNameTarget.Files, AdditionalTextExtensions = [".custom"], Filters = new() { IncludeGlobs = ["*.txt"], IncludeHidden = true } });
        var restored = WorkflowJson.TryDeserialize(WorkflowJson.Serialize(original), out var error);
        Assert.Null(error); Assert.NotNull(restored); Assert.Equal(WorkflowJson.Serialize(original), WorkflowJson.Serialize(restored));
        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.NotEmpty(WorkflowValidator.Validate(Workflow(Replace() with { Find = "" })));
        Assert.NotEmpty(WorkflowValidator.Validate(Workflow(Replace() with { Roots = [], ScopeStepId = "missing" })));
        Assert.NotEmpty(WorkflowValidator.Validate(Workflow(Replace() with { UseRegex = true, Find = "[" })));
    }

    private sealed class GenericInteraction : IWorkflowInteraction
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken) { Calls++; return Task.FromResult(true); }
    }
    private sealed class ReviewInteraction(Func<ReplacementPlan, IReadOnlySet<string>?> review) : IWorkflowReplacementInteraction
    {
        public Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken) => throw new InvalidOperationException("Replacement must use item review.");
        public Task<IReadOnlySet<string>?> ReviewReplacementAsync(ReplacementPlan plan, CancellationToken cancellationToken) => Task.FromResult(review(plan));
    }
}
