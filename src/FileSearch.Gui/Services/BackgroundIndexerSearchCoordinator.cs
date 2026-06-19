using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Services;

public sealed class BackgroundIndexerSearchCoordinator : IIndexingSearchCoordinator
{
    private readonly IIndexingService _localIndexingService;
    private readonly IBackgroundIndexerProcessService _backgroundIndexer;
    private readonly ISettingsService _settingsService;

    public BackgroundIndexerSearchCoordinator(
        IIndexingService localIndexingService,
        IBackgroundIndexerProcessService backgroundIndexer,
        ISettingsService settingsService)
    {
        _localIndexingService = localIndexingService;
        _backgroundIndexer = backgroundIndexer;
        _settingsService = settingsService;
    }

    public async Task<IndexingStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (UseBackgroundIndexer)
        {
            var status = await _backgroundIndexer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is not null)
                return status;
        }

        return _localIndexingService.CurrentStatus;
    }

    public async Task SetForegroundSearchActiveAsync(bool isActive, CancellationToken cancellationToken)
    {
        if (UseBackgroundIndexer &&
            await _backgroundIndexer.SetForegroundSearchActiveAsync(isActive, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _localIndexingService.SetForegroundSearchActive(isActive);
    }

    public async Task EnqueueRootRefreshAsync(
        string root,
        WalkerOptions options,
        IndexQueuePriority priority,
        CancellationToken cancellationToken)
    {
        if (UseBackgroundIndexer)
        {
            var location = new IndexedLocation(IndexPath.NormalizeRoot(root), options, WatchEnabled: false);
            if (await _backgroundIndexer.QueueRootRefreshAsync(location, priority, cancellationToken).ConfigureAwait(false))
                return;
        }

        await _localIndexingService.EnqueueRootRefreshAsync(root, options, priority, cancellationToken).ConfigureAwait(false);
    }

    private bool UseBackgroundIndexer =>
        _settingsService.Current.StartBackgroundIndexerAtSignIn;
}
