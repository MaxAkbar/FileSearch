namespace FileSearch.Core.Engine;

public sealed record VectorIndexOptions
{
    public string IndexPath { get; init; } = GetDefaultIndexPath();

    public bool UseApproximateSearch { get; init; } = true;

    public int ApproximateSearchMinimumDocuments { get; init; } = 512;

    public int ApproximateSearchTargetCandidates { get; init; } = 1024;

    public static string GetDefaultIndexPath(string? databasePath = null)
    {
        var overridePath = Environment.GetEnvironmentVariable("FILESEARCH_VECTOR_INDEX_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var fileName = Path.GetFileNameWithoutExtension(databasePath);
                return Path.Combine(directory, $"{fileName}.vectors.json");
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch",
            "Index",
            "vectors.json");
    }
}

public enum VectorDocumentKind
{
    File,
    ContentChunk,
}

public sealed record EmbeddingModelInfo(
    string ModelId,
    string ModelVersion,
    int Dimension,
    string QuantizationVersion = "")
{
    public EmbeddingModelInfo Normalize()
    {
        if (string.IsNullOrWhiteSpace(ModelId))
            throw new ArgumentException("Embedding model ID is required.", nameof(ModelId));
        if (string.IsNullOrWhiteSpace(ModelVersion))
            throw new ArgumentException("Embedding model version is required.", nameof(ModelVersion));
        if (Dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(Dimension), "Embedding dimension must be greater than zero.");

        return new EmbeddingModelInfo(
            ModelId.Trim(),
            ModelVersion.Trim(),
            Dimension,
            QuantizationVersion?.Trim() ?? string.Empty);
    }
}

public sealed record VectorDocument
{
    public VectorDocument(
        string id,
        VectorDocumentKind kind,
        long fileId,
        IReadOnlyCollection<long> contentUnitIds,
        ReadOnlyMemory<float> vector,
        EmbeddingModelInfo model,
        string chunkerVersion,
        string contentChecksum,
        SourceLocator? locator = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Vector document ID is required.", nameof(id));
        if (fileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileId), "File ID must be greater than zero.");

        var normalizedModel = model.Normalize();
        var vectorArray = vector.ToArray();
        if (vectorArray.Length != normalizedModel.Dimension)
            throw new ArgumentException("Vector length must match the embedding model dimension.", nameof(vector));

        Id = id.Trim();
        Kind = kind;
        FileId = fileId;
        ContentUnitIds = contentUnitIds?.Where(idValue => idValue > 0).Distinct().ToArray()
            ?? Array.Empty<long>();
        Vector = vectorArray;
        Model = normalizedModel;
        ChunkerVersion = chunkerVersion?.Trim() ?? string.Empty;
        ContentChecksum = contentChecksum?.Trim() ?? string.Empty;
        Locator = locator;
    }

    public string Id { get; init; }

    public VectorDocumentKind Kind { get; init; }

    public long FileId { get; init; }

    public IReadOnlyList<long> ContentUnitIds { get; init; }

    public IReadOnlyList<float> Vector { get; init; }

    public EmbeddingModelInfo Model { get; init; }

    public string ChunkerVersion { get; init; }

    public string ContentChecksum { get; init; }

    public SourceLocator? Locator { get; init; }

    public ReadOnlyMemory<float> VectorMemory => Vector.ToArray();
}

public sealed record VectorMatch(
    string Id,
    VectorDocumentKind Kind,
    long FileId,
    IReadOnlyList<long> ContentUnitIds,
    float Score,
    EmbeddingModelInfo Model,
    string ChunkerVersion,
    string ContentChecksum,
    SourceLocator? Locator);

public sealed record VectorIndexStats(
    int DocumentCount,
    int IndexedFileCount,
    int CoveredContentUnitCount)
{
    public static VectorIndexStats Empty { get; } = new(0, 0, 0);
}

public interface IVectorIndex
{
    Task UpsertAsync(
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<VectorMatch>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        VectorDocumentKind? kind = null,
        IReadOnlyCollection<long>? fileIds = null);

    Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null) =>
        Task.FromResult(VectorIndexStats.Empty);
}

public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VectorDocument> _documents = new(StringComparer.Ordinal);

    public Task UpsertAsync(
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _documents[document.Id] = document;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = contentUnitIds.Where(id => id > 0).ToHashSet();
        if (idSet.Count == 0)
            return Task.CompletedTask;

        lock (_gate)
        {
            foreach (var id in _documents
                         .Where(pair => pair.Value.ContentUnitIds.Any(idSet.Contains))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _documents.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        VectorDocumentKind? kind = null,
        IReadOnlyCollection<long>? fileIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0 || queryVector.Length == 0)
            return Task.FromResult<IReadOnlyList<VectorMatch>>(Array.Empty<VectorMatch>());

        var query = queryVector.ToArray();
        var queryNorm = Norm(query);
        if (queryNorm <= 0)
            return Task.FromResult<IReadOnlyList<VectorMatch>>(Array.Empty<VectorMatch>());

        VectorDocument[] snapshot;
        lock (_gate)
            snapshot = _documents.Values.ToArray();

        var fileIdSet = NormalizeFileIds(fileIds);
        var matches = snapshot
            .Where(document => MatchesFilters(document, query.Length, model, kind, fileIdSet))
            .Select(document => ToMatch(document, query, queryNorm))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Id, StringComparer.Ordinal)
            .Take(count)
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorMatch>>(matches);
    }

    public Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = contentUnitIds.Where(id => id > 0).ToHashSet();
        if (idSet.Count == 0)
            return Task.FromResult(VectorIndexStats.Empty);

        VectorDocument[] snapshot;
        lock (_gate)
            snapshot = _documents.Values.ToArray();

        return Task.FromResult(CreateStats(snapshot, idSet, model));
    }

    private static VectorMatch ToMatch(VectorDocument document, float[] query, double queryNorm)
    {
        var score = CosineSimilarity(query, queryNorm, document.Vector);
        return new VectorMatch(
            document.Id,
            document.Kind,
            document.FileId,
            document.ContentUnitIds,
            score,
            document.Model,
            document.ChunkerVersion,
            document.ContentChecksum,
            document.Locator);
    }

    private static bool MatchesModel(EmbeddingModelInfo documentModel, EmbeddingModelInfo? queryModel) =>
        queryModel is null ||
        documentModel.Dimension == queryModel.Dimension &&
        string.Equals(documentModel.ModelId, queryModel.ModelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.ModelVersion, queryModel.ModelVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.QuantizationVersion, queryModel.QuantizationVersion, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFilters(
        VectorDocument document,
        int dimension,
        EmbeddingModelInfo? model,
        VectorDocumentKind? kind,
        HashSet<long>? fileIds) =>
        document.Vector.Count == dimension &&
        (kind is null || document.Kind == kind) &&
        (fileIds is null || fileIds.Contains(document.FileId)) &&
        MatchesModel(document.Model, model);

    private static HashSet<long>? NormalizeFileIds(IReadOnlyCollection<long>? fileIds)
    {
        if (fileIds is null || fileIds.Count == 0)
            return null;

        var normalized = fileIds.Where(id => id > 0).ToHashSet();
        return normalized.Count == 0 ? null : normalized;
    }

    private static VectorIndexStats CreateStats(
        IReadOnlyList<VectorDocument> documents,
        HashSet<long> contentUnitIds,
        EmbeddingModelInfo? model)
    {
        var matching = documents
            .Where(document => MatchesModel(document.Model, model))
            .Where(document => document.ContentUnitIds.Any(contentUnitIds.Contains))
            .ToArray();
        if (matching.Length == 0)
            return VectorIndexStats.Empty;

        return new VectorIndexStats(
            matching.Length,
            matching.Select(document => document.FileId).Distinct().Count(),
            matching.SelectMany(document => document.ContentUnitIds).Where(contentUnitIds.Contains).Distinct().Count());
    }

    private static float CosineSimilarity(float[] query, double queryNorm, IReadOnlyList<float> vector)
    {
        var vectorNorm = Norm(vector);
        if (vectorNorm <= 0)
            return 0;

        double dot = 0;
        for (var i = 0; i < query.Length; i++)
            dot += query[i] * vector[i];

        return (float)(dot / (queryNorm * vectorNorm));
    }

    private static double Norm(IReadOnlyList<float> vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Count; i++)
            sum += vector[i] * vector[i];
        return Math.Sqrt(sum);
    }
}
