using System.Numerics.Tensors;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed record VectorIndexOptions
{
    public string IndexPath { get; init; } = GetDefaultIndexPath();

    public bool UseApproximateSearch { get; init; }

    public int ApproximateSearchMinimumDocuments { get; init; } = 200_000;

    public int ApproximateSearchTargetCandidates { get; init; } = 1024;

    public bool UseInt8Storage { get; init; } = true;

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
        SourceLocator? locator = null,
        string root = "",
        string filePath = "")
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
        Root = string.IsNullOrWhiteSpace(root) ? string.Empty : IndexPath.NormalizeRoot(root);
        FilePath = string.IsNullOrWhiteSpace(filePath) ? string.Empty : IndexPath.NormalizeFile(filePath);
    }

    internal VectorDocument(
        string id,
        VectorDocumentKind kind,
        long fileId,
        IReadOnlyCollection<long> contentUnitIds,
        QuantizedVector vector,
        EmbeddingModelInfo model,
        string chunkerVersion,
        string contentChecksum,
        SourceLocator? locator,
        string root,
        string filePath = "")
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Vector document ID is required.", nameof(id));
        if (fileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileId), "File ID must be greater than zero.");
        ArgumentNullException.ThrowIfNull(vector);

        var normalizedModel = model.Normalize();
        if (vector.Count != normalizedModel.Dimension)
            throw new ArgumentException("Vector length must match the embedding model dimension.", nameof(vector));

        Id = id.Trim();
        Kind = kind;
        FileId = fileId;
        ContentUnitIds = contentUnitIds?.Where(idValue => idValue > 0).Distinct().ToArray()
            ?? Array.Empty<long>();
        Vector = vector;
        Model = normalizedModel;
        ChunkerVersion = chunkerVersion?.Trim() ?? string.Empty;
        ContentChecksum = contentChecksum?.Trim() ?? string.Empty;
        Locator = locator;
        Root = string.IsNullOrWhiteSpace(root) ? string.Empty : IndexPath.NormalizeRoot(root);
        FilePath = string.IsNullOrWhiteSpace(filePath) ? string.Empty : IndexPath.NormalizeFile(filePath);
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

    public string Root { get; init; }

    public string FilePath { get; init; }

    public ReadOnlyMemory<float> VectorMemory => Vector.ToArray();

    internal VectorDocument WithVector(QuantizedVector vector) =>
        new(
            Id,
            Kind,
            FileId,
            ContentUnitIds,
            vector,
            Model,
            ChunkerVersion,
            ContentChecksum,
            Locator,
            Root,
            FilePath);
}

internal sealed class QuantizedVector : IReadOnlyList<float>
{
    private readonly sbyte[] _values;

    public QuantizedVector(sbyte[] values, float scale, long squaredNorm = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));

        _values = values;
        Scale = scale;
        SquaredNorm = squaredNorm > 0
            ? squaredNorm
            : values.Sum(value => (long)value * value);
    }

    public int Count => _values.Length;

    public float Scale { get; }

    public long SquaredNorm { get; }

    public ReadOnlySpan<sbyte> Values => _values;

    public float this[int index] => _values[index] * Scale;

    public static QuantizedVector Create(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var maximum = 0f;
        for (var i = 0; i < vector.Count; i++)
            maximum = Math.Max(maximum, Math.Abs(vector[i]));

        var scale = maximum > 0 ? maximum / sbyte.MaxValue : 1f;
        var values = new sbyte[vector.Count];
        long squaredNorm = 0;
        for (var i = 0; i < vector.Count; i++)
        {
            var quantized = (int)MathF.Round(vector[i] / scale, MidpointRounding.AwayFromZero);
            var value = (sbyte)Math.Clamp(quantized, sbyte.MinValue, sbyte.MaxValue);
            values[i] = value;
            squaredNorm += (long)value * value;
        }

        return new QuantizedVector(values, scale, squaredNorm);
    }

    public IEnumerator<float> GetEnumerator()
    {
        for (var i = 0; i < _values.Length; i++)
            yield return _values[i] * Scale;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
    SourceLocator? Locator,
    string Root = "",
    string FilePath = "");

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
        IReadOnlyCollection<long>? fileIds = null,
        IReadOnlyCollection<string>? roots = null);

    Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        IReadOnlyCollection<string>? roots = null) =>
        Task.FromResult(VectorIndexStats.Empty);
}

public interface IVectorIndexBulkMutations
{
    Task ReplaceFileAsync(
        string root,
        long fileId,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken);

    Task ReplaceFileAsync(
        string root,
        long fileId,
        string? filePath,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken) =>
        ReplaceFileAsync(root, fileId, documents, cancellationToken);

    Task ReplaceRootAsync(
        string root,
        IReadOnlyCollection<long> fileIds,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken);

    Task DeleteFileAsync(
        string root,
        long fileId,
        CancellationToken cancellationToken);

    Task DeleteRootAsync(
        string root,
        CancellationToken cancellationToken);
}

public sealed class InMemoryVectorIndex : IVectorIndex, IVectorIndexBulkMutations
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
                _documents[CreateDocumentKey(document)] = document;
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
        IReadOnlyCollection<long>? fileIds = null,
        IReadOnlyCollection<string>? roots = null)
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
        var rootSet = NormalizeRoots(roots);
        var matches = snapshot
            .Where(document => MatchesFilters(document, query.Length, model, kind, fileIdSet, rootSet))
            .Select(document => ToMatch(document, query, queryNorm))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Id, StringComparer.Ordinal)
            .Take(count)
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorMatch>>(matches);
    }

    public Task ReplaceFileAsync(
        string root,
        long fileId,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken) =>
        ReplaceFileAsync(
            root,
            fileId,
            documents.Select(document => document.FilePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            documents,
            cancellationToken);

    public Task ReplaceFileAsync(
        string root,
        long fileId,
        string? filePath,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        ValidateReplacementDocuments(normalizedRoot, documents, fileId);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : IndexPath.NormalizeFile(filePath);

        lock (_gate)
        {
            var filePaths = documents
                .Select(document => document.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(normalizedFilePath))
                filePaths.Add(normalizedFilePath);
            RemoveDocuments(document =>
                document.FileId == fileId ||
                !string.IsNullOrEmpty(document.FilePath) && filePaths.Contains(document.FilePath));
            AddDocuments(documents, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task ReplaceRootAsync(
        string root,
        IReadOnlyCollection<long> fileIds,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        ArgumentNullException.ThrowIfNull(fileIds);
        ValidateReplacementDocuments(normalizedRoot, documents);
        cancellationToken.ThrowIfCancellationRequested();
        var fileIdSet = fileIds.Where(id => id > 0).ToHashSet();

        lock (_gate)
        {
            RemoveDocuments(document =>
                string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                fileIdSet.Contains(document.FileId));
            AddDocuments(documents, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(
        string root,
        long fileId,
        CancellationToken cancellationToken)
    {
        _ = NormalizeRequiredRoot(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            RemoveDocuments(document => document.FileId == fileId);
        return Task.CompletedTask;
    }

    public Task DeleteRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveDocuments(document =>
                string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    public Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        IReadOnlyCollection<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = contentUnitIds.Where(id => id > 0).ToHashSet();
        if (idSet.Count == 0)
            return Task.FromResult(VectorIndexStats.Empty);

        VectorDocument[] snapshot;
        lock (_gate)
            snapshot = _documents.Values.ToArray();

        return Task.FromResult(CreateStats(snapshot, idSet, model, NormalizeRoots(roots)));
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
            document.Locator,
            document.Root,
            document.FilePath);
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
        HashSet<long>? fileIds,
        HashSet<string>? roots) =>
        document.Vector.Count == dimension &&
        (kind is null || document.Kind == kind) &&
        (fileIds is null || fileIds.Contains(document.FileId)) &&
        (roots is null || roots.Contains(document.Root)) &&
        MatchesModel(document.Model, model);

    private static HashSet<long>? NormalizeFileIds(IReadOnlyCollection<long>? fileIds)
    {
        if (fileIds is null || fileIds.Count == 0)
            return null;

        var normalized = fileIds.Where(id => id > 0).ToHashSet();
        return normalized.Count == 0 ? null : normalized;
    }

    private static HashSet<string>? NormalizeRoots(IReadOnlyCollection<string>? roots)
    {
        if (roots is null || roots.Count == 0)
            return null;

        var normalized = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexPath.NormalizeRoot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalized.Count == 0 ? null : normalized;
    }

    private static string NormalizeRequiredRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));
        return IndexPath.NormalizeRoot(root);
    }

    private static void ValidateReplacementDocuments(
        string normalizedRoot,
        IReadOnlyCollection<VectorDocument> documents,
        long? fileId = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var document in documents)
        {
            if (!string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Replacement documents must belong to the requested root.", nameof(documents));
            if (fileId is not null && document.FileId != fileId.Value)
                throw new ArgumentException("Replacement documents must belong to the requested file.", nameof(documents));
        }
    }

    private void AddDocuments(
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _documents[CreateDocumentKey(document)] = document;
        }
    }

    private void RemoveDocuments(Func<VectorDocument, bool> predicate)
    {
        foreach (var key in _documents
                     .Where(pair => predicate(pair.Value))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _documents.Remove(key);
        }
    }

    private static string CreateDocumentKey(VectorDocument document) =>
        $"{document.Root}\0{document.Id}";

    private static VectorIndexStats CreateStats(
        IReadOnlyList<VectorDocument> documents,
        HashSet<long> contentUnitIds,
        EmbeddingModelInfo? model,
        HashSet<string>? roots)
    {
        var matching = documents
            .Where(document => MatchesModel(document.Model, model))
            .Where(document => roots is null || roots.Contains(document.Root))
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
        var vectorArray = vector as float[] ?? vector.ToArray();
        var vectorNorm = Norm(vectorArray);
        if (vectorNorm <= 0)
            return 0;

        var dot = TensorPrimitives.Dot(query, vectorArray);
        return (float)(dot / (queryNorm * vectorNorm));
    }

    private static double Norm(ReadOnlySpan<float> vector)
    {
        var sum = TensorPrimitives.Dot(vector, vector);
        return Math.Sqrt(sum);
    }
}
