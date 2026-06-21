using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed record SemanticIndexRootStatus(
    string Root,
    bool IsModelAvailable,
    string ModelId,
    string ModelDisplayName,
    int IndexedFileCount,
    int ContentUnitCount,
    int VectorCount,
    int CoveredContentUnitCount,
    string Message)
{
    public bool IsReady =>
        IsModelAvailable &&
        ContentUnitCount > 0 &&
        VectorCount > 0 &&
        CoveredContentUnitCount >= ContentUnitCount;
}

public interface ISemanticIndexStatusService
{
    Task<SemanticIndexRootStatus> GetRootStatusAsync(
        string root,
        CancellationToken cancellationToken);
}

public sealed class SemanticIndexStatusService : ISemanticIndexStatusService
{
    private readonly IEmbeddingModelPackStore _modelPacks;
    private readonly IContentUnitReader _contentUnits;
    private readonly IVectorIndex _vectorIndex;

    public SemanticIndexStatusService(
        IEmbeddingModelPackStore modelPacks,
        IContentUnitReader contentUnits,
        IVectorIndex vectorIndex)
    {
        _modelPacks = modelPacks ?? throw new ArgumentNullException(nameof(modelPacks));
        _contentUnits = contentUnits ?? throw new ArgumentNullException(nameof(contentUnits));
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
    }

    public async Task<SemanticIndexRootStatus> GetRootStatusAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));

        var selected = await _modelPacks.GetSelectedPackAsync(cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return new SemanticIndexRootStatus(
                root,
                IsModelAvailable: false,
                string.Empty,
                string.Empty,
                0,
                0,
                0,
                0,
                UnavailableTextEmbedder.Message);
        }

        if (!selected.IsUsable)
        {
            return new SemanticIndexRootStatus(
                root,
                IsModelAvailable: false,
                selected.Manifest.Id,
                selected.Manifest.DisplayName,
                0,
                0,
                0,
                0,
                selected.Status);
        }

        var fileIds = await _contentUnits.GetFileIdsForRootAsync(root, cancellationToken).ConfigureAwait(false);
        var contentUnitIds = await _contentUnits.GetContentUnitIdsForRootAsync(root, cancellationToken).ConfigureAwait(false);
        if (fileIds.Count == 0)
        {
            return CreateStatus(
                root,
                selected,
                indexedFileCount: 0,
                contentUnitCount: 0,
                VectorIndexStats.Empty,
                "No indexed files found for this location.");
        }

        if (contentUnitIds.Count == 0)
        {
            return CreateStatus(
                root,
                selected,
                fileIds.Count,
                contentUnitCount: 0,
                VectorIndexStats.Empty,
                "Indexed files are present, but no structured content units are available for semantic search.");
        }

        var stats = await _vectorIndex
            .GetStatsAsync(contentUnitIds, cancellationToken, selected.Manifest.ToModelInfo())
            .ConfigureAwait(false);

        var message = stats.DocumentCount == 0
            ? "Smart Search vectors are not built for this location."
            : stats.CoveredContentUnitCount >= contentUnitIds.Count
                ? $"Smart Search ready with {stats.DocumentCount:n0} vector chunk(s)."
                : $"Smart Search covers {stats.CoveredContentUnitCount:n0} of {contentUnitIds.Count:n0} content unit(s).";

        return CreateStatus(
            root,
            selected,
            fileIds.Count,
            contentUnitIds.Count,
            stats,
            message);
    }

    private static SemanticIndexRootStatus CreateStatus(
        string root,
        InstalledEmbeddingModelPack selected,
        int indexedFileCount,
        int contentUnitCount,
        VectorIndexStats stats,
        string message) =>
        new(
            root,
            IsModelAvailable: true,
            selected.Manifest.Id,
            selected.Manifest.DisplayName,
            indexedFileCount,
            contentUnitCount,
            stats.DocumentCount,
            stats.CoveredContentUnitCount,
            message);
}
