using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ReplacementLocationScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchReplacementScope-" + Guid.NewGuid().ToString("N"));

    public ReplacementLocationScopeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(LocationScopeKind.SelectedIndexedLocations)]
    [InlineData(LocationScopeKind.AllIndexedLocations)]
    public void IndexedScopeCannotPreviewOrApplyToTheUnusedRememberedFolder(LocationScopeKind scope)
    {
        Run(new RecordingReplacementService(), (pump, search, replacement, service, file) =>
        {
            search.SelectedLocationScope = scope;
            replacement.FindText = "needle";
            replacement.ReplaceWith = "thread";

            Assert.False(replacement.PreviewCommand.CanExecute(null));
            Assert.False(replacement.ApplyCommand.CanExecute(null));
            Assert.Contains("Choose Selected folder", replacement.ScopeText);
            Assert.Contains("Browse", replacement.ScopeText);
            Assert.DoesNotContain(search.SearchPath, replacement.ScopeText);
            Complete(pump, replacement.PreviewCommand.ExecuteAsync(null));
            Complete(pump, replacement.ApplyCommand.ExecuteAsync(null));

            Assert.Empty(service.PreviewRequests);
            Assert.Equal(0, service.ApplyCalls);
            Assert.Empty(replacement.Items);
            Assert.Equal("needle", File.ReadAllText(file));
        });
    }

    [Fact]
    public void SwitchingScopeInvalidatesThePlanAndReturningToFolderRequiresAFreshPreview()
    {
        Run(new RecordingReplacementService(), (pump, search, replacement, service, file) =>
        {
            replacement.FindText = "needle";
            replacement.ReplaceWith = "thread";
            Complete(pump, replacement.PreviewCommand.ExecuteAsync(null));
            Assert.Single(replacement.Items);
            Assert.True(replacement.ApplyCommand.CanExecute(null));

            search.SelectedLocationScope = LocationScopeKind.AllIndexedLocations;
            Assert.Empty(replacement.Items);
            Assert.False(replacement.ApplyCommand.CanExecute(null));
            Assert.False(replacement.PreviewCommand.CanExecute(null));
            Complete(pump, replacement.ApplyCommand.ExecuteAsync(null));
            Assert.Equal(0, service.ApplyCalls);

            search.SelectedLocationScope = LocationScopeKind.SelectedFolder;
            Assert.True(replacement.PreviewCommand.CanExecute(null));
            Assert.False(replacement.ApplyCommand.CanExecute(null));
            Complete(pump, replacement.ApplyCommand.ExecuteAsync(null));
            Assert.Equal(0, service.ApplyCalls);
            Complete(pump, replacement.PreviewCommand.ExecuteAsync(null));
            Assert.Equal(2, service.PreviewRequests.Count);
            Assert.All(service.PreviewRequests, request => Assert.Equal([search.SearchPath], request.Roots));
            Assert.True(replacement.ApplyCommand.CanExecute(null));
            Assert.Equal("needle", File.ReadAllText(file));
        });
    }

    [Fact]
    public void APreviewThatIgnoresScopeCancellationCannotPublishAnApplyablePlan()
    {
        Run(new RecordingReplacementService(deferPreview: true), (pump, search, replacement, service, file) =>
        {
            replacement.FindText = "needle";
            replacement.ReplaceWith = "thread";
            var pending = replacement.PreviewCommand.ExecuteAsync(null);
            pump.PumpUntil(() => service.PreviewRequests.Count == 1, TimeSpan.FromSeconds(10));
            search.SelectedLocationScope = LocationScopeKind.SelectedIndexedLocations;
            Assert.True(service.PreviewToken.IsCancellationRequested);
            service.CompletePreview();
            Complete(pump, pending);

            Assert.Empty(replacement.Items);
            Assert.False(replacement.ApplyCommand.CanExecute(null));
            Assert.Contains("Choose Selected folder", replacement.Summary);
            Complete(pump, replacement.ApplyCommand.ExecuteAsync(null));
            Assert.Equal(0, service.ApplyCalls);
            Assert.Equal("needle", File.ReadAllText(file));
        });
    }

    [Fact]
    public void ExplicitUndoRemainsAvailableInAnIndexedScope()
    {
        Run(new RecordingReplacementService(canUndo: true), (pump, search, replacement, service, file) =>
        {
            search.SelectedLocationScope = LocationScopeKind.AllIndexedLocations;
            Assert.True(replacement.UndoCommand.CanExecute(null));
            Complete(pump, replacement.UndoCommand.ExecuteAsync(null));

            Assert.Equal(1, service.UndoCalls);
            Assert.Empty(service.PreviewRequests);
            Assert.Equal(0, service.ApplyCalls);
            Assert.Equal("needle", File.ReadAllText(file));
        });
    }

    private void Run(RecordingReplacementService service,
        Action<PumpingSynchronizationContext, SearchViewModel, ReplacementViewModel, RecordingReplacementService, string> action) => StaTestDispatcher.Run(() =>
    {
        string file = Path.Combine(_root, "needle.txt");
        File.WriteAllText(file, "needle");
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var settings = new FakeSettingsService();
            var status = new StatusBarViewModel();
            var history = new HistoryViewModel(settings, new ApplicationSettingsViewModel(settings, status), status);
            using var search = new SearchViewModel(new EmptySearcher(), new ExtractorRegistry([]), new QueryFactory(),
                new FakePreviewService(), new FakeFileLauncher(), settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
            search.SearchPath = _root;
            search.QueryText = "";
            using var replacement = new ReplacementViewModel(service, search, settings, history, status);
            action(pump, search, replacement, service, file);
        }
        finally { service.CompletePreview(); SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static void Complete(PumpingSynchronizationContext pump, Task task)
    {
        pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));
        task.GetAwaiter().GetResult();
    }

    private sealed class EmptySearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class RecordingReplacementService(bool deferPreview = false, bool canUndo = false) : IReplacementService
    {
        private readonly TaskCompletionSource<ReplacementPlan> _preview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ReplacementRequest> PreviewRequests { get; } = [];
        public CancellationToken PreviewToken { get; private set; }
        public int ApplyCalls { get; private set; }
        public int UndoCalls { get; private set; }

        public Task<ReplacementPlan> PreviewAsync(ReplacementRequest request, CancellationToken cancellationToken)
        {
            PreviewRequests.Add(request); PreviewToken = cancellationToken;
            return deferPreview ? _preview.Task : Task.FromResult(Plan(request));
        }

        public void CompletePreview()
        {
            if (PreviewRequests.Count > 0) _preview.TrySetResult(Plan(PreviewRequests[0]));
        }

        private static ReplacementPlan Plan(ReplacementRequest request) => new(request,
        [
            new("item", Path.Combine(request.Roots[0], "needle.txt"), false, "fingerprint", "identity", 1,
                [new ReplacementChange("line 1", "needle", "thread")]),
        ]);

        public Task<ReplacementBatchResult> ApplyAsync(ReplacementPlan plan, IReadOnlySet<string> checkedItemIds, CancellationToken cancellationToken)
        { ApplyCalls++; return Task.FromResult(new ReplacementBatchResult(null, [])); }
        public Task<ReplacementBatchResult> UndoAsync(CancellationToken cancellationToken)
        { UndoCalls++; return Task.FromResult(new ReplacementBatchResult(null, [])); }
        public Task<bool> CanUndoAsync(CancellationToken cancellationToken) => Task.FromResult(canUndo);
        public Task<ReplacementRecoveryGroup?> GetLastRecoveryGroupAsync(CancellationToken cancellationToken) => Task.FromResult<ReplacementRecoveryGroup?>(null);
        public Task<ReplacementBatchResult> UndoGroupAsync(string groupId, CancellationToken cancellationToken) => UndoAsync(cancellationToken);
    }
}
