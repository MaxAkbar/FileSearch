using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed record SemanticIndexCleanupResult
{
    public SemanticIndexCleanupResult(
        long? fileId,
        IReadOnlyCollection<long>? contentUnitIds,
        string message,
        string? root = null)
    {
        FileId = fileId;
        Root = root;
        ContentUnitIds = NormalizeContentUnitIds(contentUnitIds);
        Message = message ?? string.Empty;
    }

    public long? FileId { get; }

    public string? Root { get; }

    public IReadOnlyList<long> ContentUnitIds { get; }

    public string Message { get; }

    public int ContentUnitCount => ContentUnitIds.Count;

    public bool DeletedAny => ContentUnitIds.Count > 0;

    private static long[] NormalizeContentUnitIds(IReadOnlyCollection<long>? contentUnitIds) =>
        contentUnitIds is null
            ? Array.Empty<long>()
            : contentUnitIds.Where(id => id > 0).Distinct().ToArray();
}

public sealed record SemanticRootIndexBuildResult(
    string Root,
    bool IsAvailable,
    int FileCount,
    int IndexedFileCount,
    int VectorCount,
    string Message)
{
    public bool WasIndexed => IsAvailable && VectorCount > 0;

    public static SemanticRootIndexBuildResult Unavailable(string root, int fileCount, string message) =>
        new(root, false, fileCount, 0, 0, message);

    public static SemanticRootIndexBuildResult Completed(
        string root,
        int fileCount,
        int indexedFileCount,
        int vectorCount,
        string message) =>
        new(root, true, fileCount, indexedFileCount, vectorCount, message);
}

public interface ISemanticIndexingCoordinator
{
    Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken);

    Task<SemanticIndexBuildResult> UpsertFileAsync(
        string root,
        string path,
        CancellationToken cancellationToken);

    Task<SemanticRootIndexBuildResult> UpsertRootAsync(
        string root,
        CancellationToken cancellationToken);

    Task<SemanticIndexCleanupResult> DeleteFileAsync(
        long fileId,
        CancellationToken cancellationToken);

    Task<SemanticIndexCleanupResult> DeleteFileAsync(
        string root,
        string path,
        CancellationToken cancellationToken);

    Task<SemanticIndexCleanupResult> DeleteRootAsync(
        string root,
        CancellationToken cancellationToken);

    Task<SemanticIndexCleanupResult> DeleteContentUnitsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken);
}

public sealed class SemanticIndexingCoordinator : ISemanticIndexingCoordinator
{
    private readonly IContentUnitReader _contentUnits;
    private readonly ISemanticIndexBuilder _builder;
    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _vectorIndex;

    public SemanticIndexingCoordinator(
        IContentUnitReader contentUnits,
        ISemanticIndexBuilder builder,
        ITextEmbedder embedder,
        IVectorIndex vectorIndex)
    {
        _contentUnits = contentUnits ?? throw new ArgumentNullException(nameof(contentUnits));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
    }

    public Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        return _builder.UpsertFileAsync(fileId, cancellationToken);
    }

    public async Task<SemanticIndexBuildResult> UpsertFileAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var fileId = await _contentUnits.GetFileIdAsync(root, path, cancellationToken).ConfigureAwait(false);
        return fileId is null
            ? SemanticIndexBuildResult.Completed(
                0,
                0,
                0,
                0,
                null,
                "No indexed file found for semantic indexing.")
            : await _builder.UpsertFileAsync(root, fileId.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SemanticRootIndexBuildResult> UpsertRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));

        var fileIds = await _contentUnits.GetFileIdsForRootAsync(root, cancellationToken).ConfigureAwait(false);
        if (fileIds.Count == 0)
        {
            return SemanticRootIndexBuildResult.Completed(
                root,
                0,
                0,
                0,
                "No indexed files found for semantic indexing.");
        }

        var normalizedFileIds = fileIds.Where(id => id > 0).Distinct().ToArray();
        if (_builder is ISemanticBatchIndexBuilder batchBuilder &&
            _vectorIndex is IVectorIndexBulkMutations bulkMutations)
        {
            var batch = await batchBuilder.BuildFilesAsync(root, normalizedFileIds, cancellationToken)
                .ConfigureAwait(false);
            if (!batch.IsAvailable)
                return SemanticRootIndexBuildResult.Unavailable(root, normalizedFileIds.Length, batch.Message);

            await bulkMutations.ReplaceRootAsync(root, normalizedFileIds, batch.Documents, cancellationToken)
                .ConfigureAwait(false);
            return SemanticRootIndexBuildResult.Completed(
                root,
                normalizedFileIds.Length,
                batch.IndexedFileCount,
                batch.VectorCount,
                $"Indexed semantic vectors for {batch.IndexedFileCount:n0} file(s).");
        }

        var availability = await _embedder.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
            return SemanticRootIndexBuildResult.Unavailable(root, fileIds.Count, availability.Message);

        var indexedFiles = 0;
        var vectors = 0;
        foreach (var fileId in normalizedFileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _builder.UpsertFileAsync(root, fileId, cancellationToken).ConfigureAwait(false);
            if (result.WasIndexed)
                indexedFiles++;
            vectors += result.VectorCount;
        }

        return SemanticRootIndexBuildResult.Completed(
            root,
            normalizedFileIds.Length,
            indexedFiles,
            vectors,
            $"Indexed semantic vectors for {indexedFiles:n0} file(s).");
    }

    public async Task<SemanticIndexCleanupResult> DeleteFileAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);

        var units = await _contentUnits.GetContentUnitsForFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var contentUnitIds = units.Select(unit => unit.Id).Where(id => id > 0).Distinct().ToArray();
        if (contentUnitIds.Length == 0)
        {
            return new SemanticIndexCleanupResult(
                fileId,
                contentUnitIds,
                "No semantic vectors found for this file.");
        }

        await _vectorIndex.DeleteAsync(contentUnitIds, cancellationToken).ConfigureAwait(false);
        return new SemanticIndexCleanupResult(
            fileId,
            contentUnitIds,
            $"Deleted semantic vectors for {contentUnitIds.Length:n0} content unit(s).");
    }

    public async Task<SemanticIndexCleanupResult> DeleteFileAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var fileId = await _contentUnits.GetFileIdAsync(root, path, cancellationToken).ConfigureAwait(false);
        if (fileId is null)
        {
            return new SemanticIndexCleanupResult(
                null,
                Array.Empty<long>(),
                "No indexed file found for semantic vector cleanup.");
        }

        if (_vectorIndex is IVectorIndexBulkMutations bulkMutations)
        {
            await bulkMutations.DeleteFileAsync(root, fileId.Value, cancellationToken).ConfigureAwait(false);
            return new SemanticIndexCleanupResult(
                fileId,
                Array.Empty<long>(),
                "Deleted semantic vectors for the indexed file.",
                root);
        }

        return await DeleteFileAsync(fileId.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SemanticIndexCleanupResult> DeleteRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));

        if (_vectorIndex is IVectorIndexBulkMutations bulkMutations)
        {
            await bulkMutations.DeleteRootAsync(root, cancellationToken).ConfigureAwait(false);
            return new SemanticIndexCleanupResult(
                null,
                Array.Empty<long>(),
                "Deleted semantic vectors for the indexed root.",
                root);
        }

        var contentUnitIds = await _contentUnits.GetContentUnitIdsForRootAsync(root, cancellationToken)
            .ConfigureAwait(false);
        var normalizedIds = contentUnitIds.Where(id => id > 0).Distinct().ToArray();
        if (normalizedIds.Length == 0)
        {
            return new SemanticIndexCleanupResult(
                null,
                normalizedIds,
                "No semantic vectors found for this root.",
                root);
        }

        await _vectorIndex.DeleteAsync(normalizedIds, cancellationToken).ConfigureAwait(false);
        return new SemanticIndexCleanupResult(
            null,
            normalizedIds,
            $"Deleted semantic vectors for {normalizedIds.Length:n0} root content unit(s).",
            root);
    }

    public async Task<SemanticIndexCleanupResult> DeleteContentUnitsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);

        var normalizedIds = contentUnitIds.Where(id => id > 0).Distinct().ToArray();
        if (normalizedIds.Length == 0)
        {
            return new SemanticIndexCleanupResult(
                null,
                normalizedIds,
                "No semantic vectors found for these content units.");
        }

        await _vectorIndex.DeleteAsync(normalizedIds, cancellationToken).ConfigureAwait(false);
        return new SemanticIndexCleanupResult(
            null,
            normalizedIds,
            $"Deleted semantic vectors for {normalizedIds.Length:n0} content unit(s).");
    }
}
