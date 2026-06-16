using System.ComponentModel;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// Covers the one runtime cross-VM dependency the split introduced: the
/// index view model listening to the search view model's SearchPath.
/// </summary>
public sealed class IndexViewModelTests
{
    [Fact]
    public void SearchPathChangeRefreshesCurrentFolderState()
    {
        var (search, index) = Build();
        var raised = new List<string>();
        index.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var canExecuteChanged = false;
        index.AddCurrentFolderToIndexCommand.CanExecuteChanged += (_, _) => canExecuteChanged = true;

        search.SearchPath = Path.GetTempPath();

        Assert.Contains(nameof(IndexViewModel.IsCurrentFolderIndexed), raised);
        Assert.Contains(nameof(IndexViewModel.CurrentFolderIndexActionText), raised);
        Assert.True(canExecuteChanged);
    }

    [Fact]
    public void DisposeStopsListeningToSearchChanges()
    {
        var (search, index) = Build();
        index.Dispose();

        var raised = new List<string>();
        index.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        search.SearchPath = Path.GetTempPath();

        Assert.Empty(raised);
    }

    [Fact]
    public void ActiveRefreshProgressUpdatesLocationRuntimeSummary()
    {
        var root = Path.GetTempPath();
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        settings.Current.IndexedLocations.Add(new() { Root = root });
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var search = new SearchViewModel(
            new NullSearcher(),
            new ExtractorRegistry(Array.Empty<ITextExtractor>()),
            new QueryFactory(),
            new FakePreviewService(),
            new FakeFileLauncher(),
            settings,
            new FakeFileTypeOptionsStore(),
            new FakeFolderPicker(),
            history,
            status);
        var indexingService = new FakeIndexingService();
        var index = new IndexViewModel(
            new FakeFileIndex(),
            indexingService,
            settings,
            appSettings,
            new FakeFileLauncher(),
            new InlineDispatcher(),
            search,
            status);

        indexingService.RaiseStatus(new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: true,
            QueueLength: 0,
            Message: "Scanning 10; 2 changed, 8 unchanged",
            ActiveRoot: IndexPath.NormalizeRoot(root),
            ActiveKind: IndexChangeKind.RefreshRoot,
            ActiveProgress: new IndexProgress(
                FilesEnumerated: 10,
                FilesIndexed: 2,
                FilesSkippedUnchanged: 8,
                FilesRemoved: 0,
                FilesFailed: 0,
                LinesIndexed: 20)));

        var location = Assert.Single(index.IndexedLocations);
        Assert.Equal("Scanning 10; 2 changed, 8 unchanged", location.RuntimeStatusSummary);
    }

    [Fact]
    public void DatabaseInfoIsFormattedForIndexedLocationsPanel()
    {
        var fileIndex = new FakeFileIndex
        {
            DatabaseInfo = new IndexDatabaseInfo(
                @"C:\Index\filesearch.db",
                Exists: true,
                IsCompatible: true,
                SchemaVersion: "3",
                DatabaseBytes: 2048,
                WalBytes: 512,
                ShmBytes: 128,
                LocationCount: 2,
                TotalFileCount: 3,
                TotalLineCount: 10,
                PendingChangeCount: 1,
                LastIndexedUtc: new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc)),
        };

        var (_, index) = Build(fileIndex);

        Assert.Equal(@"C:\Index\filesearch.db", index.IndexDatabasePath);
        Assert.Equal("Ready, schema 3", index.IndexDatabaseStatusText);
        Assert.Contains("2.6 KB total", index.IndexDatabaseSizeText);
        Assert.Contains("db 2.0 KB", index.IndexDatabaseSizeText);
        Assert.Equal("2 locations, 3 files, 10 lines", index.IndexDatabaseContentText);
        Assert.Equal("1 pending index change", index.IndexDatabaseQueueText);
        Assert.StartsWith("Last indexed ", index.IndexDatabaseLastIndexedText);
    }

    [Fact]
    public async Task AddFolderToIndexUsesNewIndexOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-add-index-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var indexingService = new FakeIndexingService();
        var (_, index) = Build(indexingService: indexingService);
        index.NewIndexRecursive = false;
        index.NewIndexIncludeHidden = true;
        index.NewIndexEnableDocumentExtraction = false;
        index.NewIndexSkipUnknownFileTypes = true;
        index.NewIndexIncludedExtensions = ".cs; md";
        index.NewIndexIncludedFolders = "src; tests";
        index.NewIndexExcludedExtensions = ".dll; exe";
        index.NewIndexExcludedFolders = "bin; obj";

        try
        {
            await index.AddFolderToIndexAsync(root);

            var location = Assert.Single(index.IndexedLocations);
            Assert.False(location.Recursive);
            Assert.True(location.IncludeHidden);
            Assert.False(location.EnableDocumentExtraction);
            Assert.True(location.SkipUnknownFileTypes);
            Assert.Equal(".cs; .md", location.IncludedExtensions);
            Assert.Equal("src; tests", location.IncludedFolders);
            Assert.Equal(".dll; .exe", location.ExcludedExtensions);
            Assert.Equal("bin; obj", location.ExcludedFolders);

            var indexed = Assert.Single(indexingService.AddedLocations);
            Assert.False(indexed.WalkerOptions.Recursive);
            Assert.True(indexed.WalkerOptions.IncludeHidden);
            Assert.Contains(".cs", indexed.WalkerOptions.IncludeExtensions);
            Assert.Contains(".md", indexed.WalkerOptions.IncludeExtensions);
            Assert.Contains(".dll", indexed.WalkerOptions.ExcludeExtensions);
            Assert.Contains(".exe", indexed.WalkerOptions.ExcludeExtensions);
            Assert.Contains(".pdf", indexed.WalkerOptions.ExcludeExtensions);
            Assert.Contains("src", indexed.WalkerOptions.IncludeDirectories);
            Assert.Contains("tests", indexed.WalkerOptions.IncludeDirectories);
            Assert.Contains("bin", indexed.WalkerOptions.ExcludeDirectories);
            Assert.Contains("obj", indexed.WalkerOptions.ExcludeDirectories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NamedIndexFilterListsCanBeSavedAndSelected()
    {
        var (_, index) = Build();
        index.NewIndexInclusionListName = "Source";
        index.NewIndexIncludedExtensions = "cs; .md";
        index.NewIndexIncludedFolders = "src; tests";
        index.NewIndexExclusionListName = "Build";
        index.NewIndexExcludedExtensions = "dll; exe";
        index.NewIndexExcludedFolders = "bin; obj";

        index.SaveNewIndexInclusionListCommand.Execute(null);
        index.SaveNewIndexExclusionListCommand.Execute(null);

        var include = Assert.Single(index.IndexInclusionLists);
        Assert.Equal("Source", include.Name);
        Assert.Equal(".cs; .md", include.Extensions);
        Assert.Equal("src; tests", include.Folders);

        var exclude = Assert.Single(index.IndexExclusionLists);
        Assert.Equal("Build", exclude.Name);
        Assert.Equal(".dll; .exe", exclude.Extensions);
        Assert.Equal("bin; obj", exclude.Folders);

        index.NewIndexIncludedExtensions = string.Empty;
        index.NewIndexExcludedFolders = string.Empty;
        index.SelectedIndexInclusionList = null;
        index.SelectedIndexExclusionList = null;
        index.SelectedIndexInclusionList = include;
        index.SelectedIndexExclusionList = exclude;

        Assert.Equal(".cs; .md", index.NewIndexIncludedExtensions);
        Assert.Equal("src; tests", index.NewIndexIncludedFolders);
        Assert.Equal(".dll; .exe", index.NewIndexExcludedExtensions);
        Assert.Equal("bin; obj", index.NewIndexExcludedFolders);
    }

    [Fact]
    public async Task CompactCommandRunsMaintenanceAndRefreshesDatabaseInfo()
    {
        var fileIndex = new FakeFileIndex
        {
            DatabaseInfo = new IndexDatabaseInfo(
                @"C:\Index\filesearch.db",
                Exists: true,
                IsCompatible: true,
                SchemaVersion: "3",
                DatabaseBytes: 2048,
                WalBytes: 512,
                ShmBytes: 0,
                LocationCount: 1,
                TotalFileCount: 1,
                TotalLineCount: 2,
                PendingChangeCount: 1,
                LastIndexedUtc: null),
        };
        var (_, index) = Build(fileIndex);

        fileIndex.DatabaseInfo = fileIndex.DatabaseInfo with
        {
            DatabaseBytes = 1536,
            WalBytes = 0,
            PendingChangeCount = 0,
        };
        await index.CompactIndexDatabaseCommand.ExecuteAsync(null);

        Assert.Equal(1, fileIndex.CompactCallCount);
        Assert.Contains("1.5 KB total", index.IndexDatabaseSizeText);
        Assert.Equal("No pending index changes", index.IndexDatabaseQueueText);
    }

    [Fact]
    public async Task RemoveSelectedIndexRemovesLocationBeforeStorageClearCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "filesearch-remove-ui-" + Guid.NewGuid().ToString("N"));
        var indexingService = new FakeIndexingService
        {
            RemoveLocationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var (_, index) = Build(
            indexingService: indexingService,
            configureSettings: settings => settings.IndexedLocations.Add(new() { Root = root }));

        index.SelectedIndexedLocation = Assert.Single(index.IndexedLocations);

        await index.RemoveSelectedIndexCommand.ExecuteAsync(null);

        Assert.Empty(index.IndexedLocations);
        Assert.Equal(IndexPath.NormalizeRoot(root), Assert.Single(indexingService.RemovedLocations));
        Assert.False(indexingService.RemoveLocationCompletion.Task.IsCompleted);

        indexingService.RemoveLocationCompletion.SetResult();
    }

    [Fact]
    public async Task CompactCommandQueuesDuringIndexingAndRunsWhenCurrentWorkStops()
    {
        var fileIndex = new FakeFileIndex
        {
            DatabaseInfo = new IndexDatabaseInfo(
                @"C:\Index\filesearch.db",
                Exists: true,
                IsCompatible: true,
                SchemaVersion: "3",
                DatabaseBytes: 2048,
                WalBytes: 512,
                ShmBytes: 0,
                LocationCount: 1,
                TotalFileCount: 1,
                TotalLineCount: 2,
                PendingChangeCount: 0,
                LastIndexedUtc: null),
        };
        var indexingService = new FakeIndexingService();
        var (_, index) = Build(fileIndex, indexingService);

        indexingService.RaiseStatus(new IndexingStatus(
            IsRunning: true,
            IsPaused: false,
            IsProcessing: true,
            QueueLength: 3,
            Message: "Indexing",
            ActiveRoot: Path.GetTempPath(),
            ActiveKind: IndexChangeKind.RefreshRoot));

        await index.CompactIndexDatabaseCommand.ExecuteAsync(null);

        Assert.True(index.IsIndexDatabaseCompactionQueued);
        Assert.Equal("Compact queued", index.CompactIndexDatabaseActionText);
        Assert.Equal(1, indexingService.PauseCallCount);
        Assert.Equal(0, fileIndex.CompactCallCount);

        fileIndex.DatabaseInfo = fileIndex.DatabaseInfo with
        {
            DatabaseBytes = 1536,
            WalBytes = 0,
        };

        indexingService.RaiseStatus(new IndexingStatus(
            IsRunning: true,
            IsPaused: true,
            IsProcessing: false,
            QueueLength: 3,
            Message: "Indexing paused."));

        await WaitUntilAsync(() => fileIndex.CompactCallCount == 1);

        Assert.False(index.IsIndexDatabaseCompactionQueued);
        Assert.Equal(1, indexingService.ResumeCallCount);
        Assert.False(indexingService.IsPaused);
        Assert.Contains("1.5 KB total", index.IndexDatabaseSizeText);
    }

    private static (SearchViewModel Search, IndexViewModel Index) Build(
        FakeFileIndex? fileIndex = null,
        FakeIndexingService? indexingService = null,
        Action<AppSettings>? configureSettings = null)
    {
        var status = new StatusBarViewModel();
        var settings = new FakeSettingsService();
        configureSettings?.Invoke(settings.Current);
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status);
        var search = new SearchViewModel(
            new NullSearcher(),
            new ExtractorRegistry(Array.Empty<ITextExtractor>()),
            new QueryFactory(),
            new FakePreviewService(),
            new FakeFileLauncher(),
            settings,
            new FakeFileTypeOptionsStore(),
            new FakeFolderPicker(),
            history,
            status);
        var index = new IndexViewModel(
            fileIndex ?? new FakeFileIndex(),
            indexingService ?? new FakeIndexingService(),
            settings,
            appSettings,
            new FakeFileLauncher(),
            new InlineDispatcher(),
            search,
            status);
        return (search, index);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class NullSearcher : FileSearch.Core.Engine.ISearcher
    {
        public async IAsyncEnumerable<FileSearch.Core.Engine.Hit> SearchAsync(
            FileSearch.Core.Engine.SearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
