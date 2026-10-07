using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class WorkflowReplacementReviewTests
{
    [Fact]
    public void ReviewChecksOnlyApplicableItemsAndShowsTheActualSelectedDestination()
    {
        var root = @"C:\scope";
        var parent = new ReplacementItem("parent", root + @"\old", true, "hash", "id", 1, [new("Name", "old", "new")], root + @"\new");
        var child = new ReplacementItem("child", parent.Path + @"\old.txt", false, "hash", "id", 2, [new("Name", "old.txt", "new.txt")], parent.Path + @"\new.txt");
        var skip = new ReplacementItem("skip", root + @"\bad.txt", false, "", "", 0, [], SkipReason: "Protected");
        var review = new WorkflowReplacementReviewViewModel(new(new([root], new WalkerOptions(), "old", "new", ReplacementTarget.Names), [parent, child, skip]));
        Assert.Equal(3, review.CheckedChanges); Assert.Equal(2, review.CheckedIds.Count); Assert.False(review.Items[2].IsChecked);
        review.SelectedItem = review.Items[1]; Assert.Contains(root + @"\new\new.txt", review.SelectedPreview);
        review.Items[0].IsChecked = false; Assert.Contains(root + @"\old\new.txt", review.SelectedPreview);
        review.Items[1].IsChecked = false; Assert.False(review.CanApply); Assert.Empty(review.CheckedIds);
    }

    [Fact]
    public void DisplayedPathsFollowCompletedNestedRenamesAndUndo()
    {
        using var vm = new WorkflowsViewModel(new Store(), new Runner(), new FakeFileLauncher(), new FakeFolderPicker());
        vm.RunHits.Add(new(@"C:\scope\old\old.txt", 1, "original hit"));
        vm.RunHits.Add(new(@"C:\scope\blocked.txt", 1, "unchanged"));
        vm.RemapReplacementRunPaths(new("batch",
            [new(@"C:\scope\old\old.txt", true, "Applied", @"C:\scope\old\new.txt"),
             new(@"C:\scope\old", true, "Applied", @"C:\scope\new", true),
             new(@"C:\scope\blocked.txt", false, "Conflict", @"C:\scope\other.txt")]));
        Assert.Equal(@"C:\scope\new\new.txt", vm.RunHits[0].Path);
        Assert.Equal(@"C:\scope\blocked.txt", vm.RunHits[1].Path);
        vm.RemapReplacementRunPaths(new("batch",
            [new(@"C:\scope\new", true, "Undone", @"C:\scope\old", true),
             new(@"C:\scope\old\new.txt", true, "Undone", @"C:\scope\old\old.txt")]));
        Assert.Equal(@"C:\scope\old\old.txt", vm.RunHits[0].Path);
    }

    [Fact]
    public void ReplacementStepsAreExcludedFromSearchReferenceOptions()
    {
        // The replacement editor shares folder/filter state with Search, but it does not produce search hits.
        var vm = new WorkflowsViewModel(new Store(), new Runner(), new FakeFileLauncher(), new FakeFolderPicker());
        vm.WorkflowName = "test";
        vm.AddStepCommand.Execute("search"); vm.AddStepCommand.Execute("replace");
        var replacement = Assert.Single(vm.StepRows.OfType<ReplacementStepViewModel>());
        Assert.DoesNotContain(replacement.Id, vm.ScopeStepOptions);
        Assert.DoesNotContain(replacement.Id, vm.SourceStepOptions);
        Assert.Contains("search-1", vm.ScopeStepOptions);
    }
    private sealed class Store : FileSearch.Core.Workflows.IWorkflowStore
    {
        public string DirectoryPath => @"C:\workflows";
        public IReadOnlyList<FileSearch.Core.Workflows.WorkflowSummary> List() => [];
        public FileSearch.Core.Workflows.WorkflowDefinition? TryLoad(string fileName, out string? error) { error = "missing"; return null; }
        public string Save(FileSearch.Core.Workflows.WorkflowDefinition workflow, string? fileName = null) => "test.json";
        public void Delete(string fileName) { }
        public string GetFullPath(string fileName) => System.IO.Path.Combine(DirectoryPath, fileName);
    }
    private sealed class Runner : FileSearch.Core.Workflows.IWorkflowRunner
    {
        public Task<FileSearch.Core.Workflows.WorkflowRunResult> RunAsync(FileSearch.Core.Workflows.WorkflowDefinition workflow,
            FileSearch.Core.Workflows.WorkflowRunOptions? options = null, FileSearch.Core.Workflows.IWorkflowObserver? observer = null,
            FileSearch.Core.Workflows.IWorkflowInteraction? interaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileSearch.Core.Workflows.WorkflowRunResult { Succeeded = true });
    }
}
