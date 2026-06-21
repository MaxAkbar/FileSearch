using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public void Update(Action<AppSettings> mutate) => mutate(Current);
}

internal sealed class FakePreviewService : IFilePreviewService
{
    public Task<string> LoadHitsPreviewAsync(string path, IReadOnlyList<int> hitLineNumbers, int contextLines, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);

    public Task<string> LoadFullTextAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);
}

internal sealed class FakeFileLauncher : IFileLauncher
{
    public string? LastOpenedPath { get; private set; }

    public string? LastOpenedAtPath { get; private set; }

    public Hit? LastOpenedAtHit { get; private set; }

    public bool OpenAtLocationResult { get; set; }

    public ImageOcrPreviewViewModel? LastImageOcrPreview { get; private set; }

    public string? LastClipboardText { get; private set; }

    public void Open(string path)
    {
        LastOpenedPath = path;
    }

    public Task<bool> OpenAtLocationAsync(string path, Hit hit, CancellationToken cancellationToken)
    {
        LastOpenedAtPath = path;
        LastOpenedAtHit = hit;
        return Task.FromResult(OpenAtLocationResult);
    }

    public void OpenImageOcrPreview(ImageOcrPreviewViewModel preview)
    {
        LastImageOcrPreview = preview;
    }

    public void RevealInExplorer(string path)
    {
    }

    public void CopyToClipboard(string text)
    {
        LastClipboardText = text;
    }
}

internal sealed class FakeFileOperationService : IFileOperationService
{
    public FileOperationResult RenameResult { get; set; } = FileOperationResult.Cancelled();

    public FileOperationResult DeleteResult { get; set; } = FileOperationResult.Cancelled();

    public string? RenamePath { get; private set; }

    public string? DeletePath { get; private set; }

    public int RenameCallCount { get; private set; }

    public int DeleteCallCount { get; private set; }

    public Task<FileOperationResult> RenameFileAsync(string path, CancellationToken cancellationToken)
    {
        RenameCallCount++;
        RenamePath = path;
        return Task.FromResult(RenameResult);
    }

    public Task<FileOperationResult> MoveFileToRecycleBinAsync(string path, CancellationToken cancellationToken)
    {
        DeleteCallCount++;
        DeletePath = path;
        return Task.FromResult(DeleteResult);
    }
}

internal sealed class FakeFileTypeOptionsStore : IFileTypeOptionsStore
{
    public FileTypeOptions Load() => new();

    public void Save(FileTypeOptions options)
    {
    }
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? PathToReturn { get; set; }

    public string? LastTitle { get; private set; }

    public string? LastInitialDirectory { get; private set; }

    public string? PickFolder(string title, string? initialDirectory)
    {
        LastTitle = title;
        LastInitialDirectory = initialDirectory;
        return PathToReturn;
    }
}

internal sealed class FakeFileSavePicker : IFileSavePicker
{
    public string? PathToReturn { get; set; }

    public string? LastTitle { get; private set; }

    public string? LastFilter { get; private set; }

    public string? LastDefaultFileName { get; private set; }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        LastTitle = title;
        LastFilter = filter;
        LastDefaultFileName = defaultFileName;
        return PathToReturn;
    }
}

internal sealed class FakeFileOpenPicker : IFileOpenPicker
{
    public string? PathToReturn { get; set; }

    public string? LastTitle { get; private set; }

    public string? LastFilter { get; private set; }

    public string? PickOpenFile(string title, string filter)
    {
        LastTitle = title;
        LastFilter = filter;
        return PathToReturn;
    }
}

internal sealed class FakeStartupRegistrationService : IStartupRegistrationService
{
    public bool IsEnabled { get; set; }

    public bool ThrowOnEnable { get; set; }

    public bool ThrowOnDisable { get; set; }

    public int EnableCallCount { get; private set; }

    public int DisableCallCount { get; private set; }

    public bool IsBackgroundStartupEnabled() => IsEnabled;

    public void EnableBackgroundStartup()
    {
        EnableCallCount++;
        if (ThrowOnEnable)
            throw new InvalidOperationException("enable failed");

        IsEnabled = true;
    }

    public void DisableBackgroundStartup()
    {
        DisableCallCount++;
        if (ThrowOnDisable)
            throw new InvalidOperationException("disable failed");

        IsEnabled = false;
    }
}

internal sealed class FakeThemeService : IThemeService
{
    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public string? CurrentCustomThemeFileName { get; private set; }

    public string CustomThemeFolderPath { get; set; } = @"C:\Users\Tester\AppData\Roaming\FileSearch\Themes";

    public IReadOnlyList<CustomThemeInfo> Themes { get; set; } = Array.Empty<CustomThemeInfo>();

    public bool CustomThemeResult { get; set; } = true;

    public int SetThemeCallCount { get; private set; }

    public int SetCustomThemeCallCount { get; private set; }

    public IReadOnlyList<CustomThemeInfo> GetCustomThemes() => Themes;

    public void SetTheme(AppTheme theme)
    {
        SetThemeCallCount++;
        CurrentTheme = theme;
        CurrentCustomThemeFileName = null;
    }

    public bool TrySetCustomTheme(string fileName, out string error)
    {
        SetCustomThemeCallCount++;
        if (!CustomThemeResult)
        {
            error = "theme failed";
            return false;
        }

        error = string.Empty;
        CurrentCustomThemeFileName = fileName;
        return true;
    }
}

internal sealed class FakeStyleService : IStyleService
{
    public AppStyle CurrentStyle { get; private set; } = AppStyle.Comfortable;

    public int SetStyleCallCount { get; private set; }

    public void SetStyle(AppStyle style)
    {
        SetStyleCallCount++;
        CurrentStyle = style;
    }
}

internal sealed class FakeBackgroundIndexerProcessService : IBackgroundIndexerProcessService
{
    public bool EnsureRunningResult { get; set; } = true;

    public bool CommandResult { get; set; } = true;

    public IndexingStatus? Status { get; set; }

    public int EnsureRunningCallCount { get; private set; }

    public int GetStatusCallCount { get; private set; }

    public int AddOrUpdateCallCount { get; private set; }

    public int RemoveCallCount { get; private set; }

    public int RefreshCallCount { get; private set; }

    public int RefreshSemanticRootCallCount { get; private set; }

    public int PauseCallCount { get; private set; }

    public int ResumeCallCount { get; private set; }

    public int SetResourceProfileCallCount { get; private set; }

    public int SetRuntimeOptionsCallCount { get; private set; }

    public int SetForegroundSearchActiveCallCount { get; private set; }

    public int QueueRootRefreshCallCount { get; private set; }

    public int CompactDatabaseCallCount { get; private set; }

    public int ValidateRootCallCount { get; private set; }

    public List<IndexedLocation> AddedLocations { get; } = new();

    public List<string> RemovedRoots { get; } = new();

    public List<IndexedLocation> RefreshedLocations { get; } = new();

    public List<IndexedLocation> SemanticRefreshedLocations { get; } = new();

    public List<IndexedLocation> QueuedRefreshLocations { get; } = new();

    public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        EnsureRunningCallCount++;
        return Task.FromResult(EnsureRunningResult);
    }

    public Task<bool> ShutdownIfRunningAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult);

    public Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        GetStatusCallCount++;
        return Task.FromResult(Status);
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken)
    {
        PauseCallCount++;
        return Task.FromResult(CommandResult);
    }

    public Task<bool> ResumeAsync(CancellationToken cancellationToken)
    {
        ResumeCallCount++;
        return Task.FromResult(CommandResult);
    }

    public Task<bool> SetResourceProfileAsync(IndexerResourceProfile profile, CancellationToken cancellationToken)
    {
        SetResourceProfileCallCount++;
        return Task.FromResult(CommandResult);
    }

    public Task<bool> SetRuntimeOptionsAsync(IndexerRuntimeOptions options, CancellationToken cancellationToken)
    {
        SetRuntimeOptionsCallCount++;
        return Task.FromResult(CommandResult);
    }

    public Task<bool> SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken)
    {
        SetForegroundSearchActiveCallCount++;
        return Task.FromResult(CommandResult);
    }

    public Task<bool> AddOrUpdateLocationAsync(IndexedLocation location, CancellationToken cancellationToken)
    {
        AddOrUpdateCallCount++;
        AddedLocations.Add(location);
        return Task.FromResult(CommandResult);
    }

    public Task<bool> RemoveLocationAsync(string root, CancellationToken cancellationToken)
    {
        RemoveCallCount++;
        RemovedRoots.Add(IndexPath.NormalizeRoot(root));
        return Task.FromResult(CommandResult);
    }

    public Task<bool> RefreshRootAsync(IndexedLocation location, CancellationToken cancellationToken)
    {
        RefreshCallCount++;
        RefreshedLocations.Add(location);
        return Task.FromResult(CommandResult);
    }

    public Task<bool> RefreshSemanticRootAsync(IndexedLocation location, CancellationToken cancellationToken)
    {
        RefreshSemanticRootCallCount++;
        SemanticRefreshedLocations.Add(location);
        return Task.FromResult(CommandResult);
    }

    public Task<bool> QueueRootRefreshAsync(
        IndexedLocation location,
        IndexQueuePriority priority,
        CancellationToken cancellationToken)
    {
        QueueRootRefreshCallCount++;
        QueuedRefreshLocations.Add(location);
        return Task.FromResult(CommandResult);
    }

    public Task<IndexValidationResult?> ValidateRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        ValidateRootCallCount++;
        return Task.FromResult<IndexValidationResult?>(IndexValidationResult.Create(
            location.Root,
            DateTime.UtcNow,
            filesChecked: 1,
            filesMatched: 1,
            missingFromIndex: 0,
            changedSinceIndex: 0,
            missingFromDisk: 0,
            failedChecks: 0));
    }

    public Task<bool> CompactDatabaseAsync(CancellationToken cancellationToken)
    {
        CompactDatabaseCallCount++;
        return Task.FromResult(CommandResult);
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeIndexingService : IIndexingService
{
    public event EventHandler<IndexingStatus>? StatusChanged;

    public IndexingStatus CurrentStatus { get; set; } = new(false, false, false, 0, "Idle");

    public bool IsPaused { get; private set; }

    public IndexerResourceProfile ResourceProfile { get; private set; } = IndexerResourceProfile.Balanced;

    public IndexerRuntimeOptions RuntimeOptions { get; private set; } = IndexerRuntimeOptions.Default;

    public int PauseCallCount { get; private set; }

    public int ResumeCallCount { get; private set; }

    public int EnqueuedRootRefreshCount { get; private set; }

    public int EnqueuedSemanticRootRefreshCount { get; private set; }

    public TaskCompletionSource? RemoveLocationCompletion { get; set; }

    public List<string> RemovedLocations { get; } = new();

    public List<IndexedLocation> AddedLocations { get; } = new();

    public Task StartAsync(IEnumerable<IndexedLocation> locations, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AddOrUpdateLocationAsync(IndexedLocation location, bool queueInitialRefresh, CancellationToken cancellationToken)
    {
        AddedLocations.Add(location);
        return Task.CompletedTask;
    }

    public Task RemoveLocationAsync(string root, CancellationToken cancellationToken)
    {
        RemovedLocations.Add(IndexPath.NormalizeRoot(root));
        return RemoveLocationCompletion?.Task ?? Task.CompletedTask;
    }

    public Task EnqueueRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken)
    {
        EnqueuedRootRefreshCount++;
        return Task.CompletedTask;
    }

    public Task EnqueueSemanticRootRefreshAsync(string root, WalkerOptions options, IndexQueuePriority priority, CancellationToken cancellationToken)
    {
        EnqueuedSemanticRootRefreshCount++;
        return Task.CompletedTask;
    }

    public void SetForegroundSearchActive(bool isActive)
    {
    }

    public void SetResourceProfile(IndexerResourceProfile profile)
    {
        ResourceProfile = profile;
    }

    public void SetRuntimeOptions(IndexerRuntimeOptions options)
    {
        RuntimeOptions = options.Normalize();
    }

    public void Pause()
    {
        PauseCallCount++;
        IsPaused = true;
        CurrentStatus = CurrentStatus with { IsPaused = true };
    }

    public void Resume()
    {
        ResumeCallCount++;
        IsPaused = false;
        CurrentStatus = CurrentStatus with { IsPaused = false };
    }

    public void RaiseStatus(IndexingStatus status)
    {
        CurrentStatus = status;
        IsPaused = status.IsPaused;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class FakeFileIndex : IFileIndex
{
    public IndexDatabaseInfo DatabaseInfo { get; set; } = new(
        string.Empty,
        Exists: false,
        IsCompatible: false,
        SchemaVersion: "5",
        DatabaseBytes: 0,
        WalBytes: 0,
        ShmBytes: 0,
        LocationCount: 0,
        TotalFileCount: 0,
        TotalLineCount: 0,
        PendingChangeCount: 0,
        LastIndexedUtc: null);

    public int CompactCallCount { get; private set; }

    public int ValidateRootCallCount { get; private set; }

    public int ExportFailuresCallCount { get; private set; }

    public string? ExportFailuresPath { get; private set; }

    public IndexFailureExportFormat? ExportFailuresFormat { get; private set; }

    public IReadOnlyList<IndexFailureInfo> Failures { get; set; } = Array.Empty<IndexFailureInfo>();

    public IReadOnlyList<IndexValidationDriftInfo> ValidationDrift { get; set; } = Array.Empty<IndexValidationDriftInfo>();

    public string DatabasePath => DatabaseInfo.DatabasePath;

    public Task BuildOrRefreshAsync(IndexRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RefreshRootAsync(IndexRequest request, IndexRefreshMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpsertFileAsync(string root, string path, WalkerOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteFileAsync(string root, string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<IndexCoverage> GetCoverageAsync(SearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new IndexCoverage(IndexCoverageStatus.Missing, "fake"));

    public Task<IndexStats> GetStatsAsync(string root, CancellationToken cancellationToken) =>
        Task.FromResult(new IndexStats(root, 0, 0, null, Exists: false));

    public Task<IReadOnlyList<IndexedLocationInfo>> GetLocationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexedLocationInfo>>(Array.Empty<IndexedLocationInfo>());

    public Task<IndexDatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DatabaseInfo);

    public Task<IndexValidationResult> ValidateRootAsync(IndexRequest request, CancellationToken cancellationToken)
    {
        ValidateRootCallCount++;
        request.ValidationProgress?.Invoke(new IndexValidationProgress(
            FilesChecked: 1,
            FilesMatched: 1,
            MissingFromIndex: 0,
            ChangedSinceIndex: 0,
            MissingFromDisk: 0,
            FailedChecks: 0));
        return Task.FromResult(IndexValidationResult.Create(
            request.Root,
            DateTime.UtcNow,
            filesChecked: 1,
            filesMatched: 1,
            missingFromIndex: 0,
            changedSinceIndex: 0,
            missingFromDisk: 0,
            failedChecks: 0));
    }

    public Task<IReadOnlyList<IndexFailureInfo>> GetFailedFilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Failures);

    public Task<IReadOnlyList<IndexValidationDriftInfo>> GetValidationDriftAsync(
        string root,
        CancellationToken cancellationToken) =>
        Task.FromResult(ValidationDrift);

    public Task ExportFailedFilesAsync(
        string path,
        IndexFailureExportFormat format,
        CancellationToken cancellationToken)
    {
        ExportFailuresCallCount++;
        ExportFailuresPath = path;
        ExportFailuresFormat = format;
        return Task.CompletedTask;
    }

    public Task CompactAsync(CancellationToken cancellationToken)
    {
        CompactCallCount++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(string root, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SavePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<PendingIndexChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingIndexChange>>(Array.Empty<PendingIndexChange>());

    public Task RemovePendingChangeAsync(string root, string? path, IndexChangeKind kind, CancellationToken cancellationToken) => Task.CompletedTask;
}
