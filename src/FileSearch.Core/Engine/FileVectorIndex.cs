using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Engine;

public sealed class FileVectorIndex : IVectorIndex, IDisposable
{
    private const int LegacyJsonFormatVersion = 1;
    private const int FormatVersion = 2;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, VectorDocument> _documents = new(StringComparer.Ordinal);
    private readonly VectorIndexOptions _options;
    private readonly ILogger _logger;
    private bool _loaded;
    private string _loadedPath = string.Empty;
    private string _loadedBinaryPath = string.Empty;
    private DateTime? _loadedLastWriteUtc;
    private DateTime? _loadedBinaryLastWriteUtc;
    private VectorSearchIndex _searchIndex = VectorSearchIndex.Empty;
    private bool _searchIndexDirty = true;

    public FileVectorIndex(
        VectorIndexOptions? options = null,
        ILogger<FileVectorIndex>? logger = null)
    {
        _options = options ?? new VectorIndexOptions();
        _logger = logger ?? NullLogger<FileVectorIndex>.Instance;
    }

    internal VectorIndexSearchDiagnostics LastSearchDiagnostics { get; private set; } =
        new(0, 0, UsedApproximateIndex: false, UsedFileIdIndex: false, "not-run");

    public void Dispose() => _gate.Dispose();

    public async Task UpsertAsync(
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _documents[document.Id] = document;
            }

            _searchIndexDirty = true;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = contentUnitIds.Where(id => id > 0).ToHashSet();
        if (idSet.Count == 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var removed = false;
            foreach (var id in _documents
                         .Where(pair => pair.Value.ContentUnitIds.Any(idSet.Contains))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                removed |= _documents.Remove(id);
            }

            if (removed)
            {
                _searchIndexDirty = true;
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int count,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        VectorDocumentKind? kind = null,
        IReadOnlyCollection<long>? fileIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0 || queryVector.Length == 0)
            return Array.Empty<VectorMatch>();

        VectorSearchIndex searchIndex;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            searchIndex = GetSearchIndex();
        }
        finally
        {
            _gate.Release();
        }

        var result = searchIndex.Search(queryVector, count, model, kind, fileIds);
        LastSearchDiagnostics = result.Diagnostics;
        return result.Matches;
    }

    public async Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null)
    {
        ArgumentNullException.ThrowIfNull(contentUnitIds);
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = contentUnitIds.Where(id => id > 0).ToHashSet();
        if (idSet.Count == 0)
            return VectorIndexStats.Empty;

        VectorDocument[] snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            snapshot = _documents.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        return CreateStats(snapshot, idSet, model);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var path = NormalizeIndexPath();
        var binaryPath = GetBinaryPath(path);
        var lastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        var binaryLastWriteUtc = File.Exists(binaryPath) ? File.GetLastWriteTimeUtc(binaryPath) : (DateTime?)null;
        if (_loaded &&
            string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_loadedBinaryPath, binaryPath, StringComparison.OrdinalIgnoreCase) &&
            _loadedLastWriteUtc == lastWriteUtc &&
            _loadedBinaryLastWriteUtc == binaryLastWriteUtc)
        {
            return;
        }

        _loaded = true;
        _loadedPath = path;
        _loadedBinaryPath = binaryPath;
        _loadedLastWriteUtc = lastWriteUtc;
        _loadedBinaryLastWriteUtc = binaryLastWriteUtc;
        _documents.Clear();
        _searchIndex = VectorSearchIndex.Empty;
        _searchIndexDirty = true;
        if (!File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var jsonDocument = JsonDocument.Parse(json);
            var formatVersion = jsonDocument.RootElement.TryGetProperty(nameof(VectorIndexStore.FormatVersion), out var versionElement)
                ? versionElement.GetInt32()
                : LegacyJsonFormatVersion;

            if (formatVersion == LegacyJsonFormatVersion)
            {
                var legacyStore = JsonSerializer.Deserialize<LegacyVectorIndexStore>(json, s_jsonOptions);
                if (legacyStore?.FormatVersion != LegacyJsonFormatVersion)
                {
                    _logger.LogWarning("Ignoring unsupported vector index format at {Path}.", path);
                    return;
                }

                foreach (var document in legacyStore.Documents.Select(record => record.ToDocument()))
                    _documents[document.Id] = document;
                _searchIndexDirty = true;
                return;
            }

            if (formatVersion != FormatVersion)
            {
                _logger.LogWarning("Ignoring unsupported vector index format at {Path}.", path);
                return;
            }

            var store = JsonSerializer.Deserialize<VectorIndexStore>(json, s_jsonOptions);
            if (store is null)
                return;

            binaryPath = GetBinaryPath(path, store.VectorFileName);
            if (!File.Exists(binaryPath))
            {
                _logger.LogWarning("Vector index metadata exists but binary vector store is missing at {Path}.", binaryPath);
                return;
            }

            _loadedBinaryPath = binaryPath;
            _loadedBinaryLastWriteUtc = File.GetLastWriteTimeUtc(binaryPath);
            foreach (var document in await ReadBinaryDocumentsAsync(store, binaryPath, cancellationToken).ConfigureAwait(false))
                _documents[document.Id] = document;
            _searchIndexDirty = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            _logger.LogWarning(ex, "Could not load vector index at {Path}; starting with an empty vector index.", path);
            _documents.Clear();
            _searchIndex = VectorSearchIndex.Empty;
            _searchIndexDirty = true;
        }
    }

    private VectorSearchIndex GetSearchIndex()
    {
        if (!_searchIndexDirty)
            return _searchIndex;

        _searchIndex = new VectorSearchIndex(_documents.Values.ToArray(), _options);
        _searchIndexDirty = false;
        return _searchIndex;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var path = NormalizeIndexPath();
        var binaryPath = GetBinaryPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var tempBinaryPath = $"{binaryPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var orderedDocuments = _documents.Values
                .OrderBy(document => document.Id, StringComparer.Ordinal)
                .ToArray();
            var records = new List<VectorDocumentRecord>(orderedDocuments.Length);
            await using (var binaryStream = new FileStream(tempBinaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var document in orderedDocuments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = binaryStream.Position;
                    var vector = document.Vector.ToArray();
                    var bytes = new byte[vector.Length * sizeof(float)];
                    Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                    await binaryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    records.Add(VectorDocumentRecord.FromDocument(document, offset, vector.Length));
                }
            }

            var store = new VectorIndexStore(
                FormatVersion,
                Path.GetFileName(binaryPath),
                records.ToArray());
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, store, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempBinaryPath, binaryPath, overwrite: true);
            File.Move(tempPath, path, overwrite: true);
            _loadedPath = path;
            _loadedBinaryPath = binaryPath;
            _loadedLastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            _loadedBinaryLastWriteUtc = File.Exists(binaryPath) ? File.GetLastWriteTimeUtc(binaryPath) : (DateTime?)null;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(tempBinaryPath))
                File.Delete(tempBinaryPath);
        }
    }

    private string NormalizeIndexPath()
    {
        if (string.IsNullOrWhiteSpace(_options.IndexPath))
            throw new InvalidOperationException("Vector index path is required.");

        return Path.GetFullPath(_options.IndexPath);
    }

    private static string GetBinaryPath(string indexPath, string? vectorFileName = null)
    {
        if (!string.IsNullOrWhiteSpace(vectorFileName))
        {
            var directory = Path.GetDirectoryName(indexPath);
            return string.IsNullOrWhiteSpace(directory)
                ? Path.GetFullPath(vectorFileName)
                : Path.GetFullPath(Path.Combine(directory, vectorFileName));
        }

        return Path.ChangeExtension(indexPath, ".bin");
    }

    private static async Task<IReadOnlyList<VectorDocument>> ReadBinaryDocumentsAsync(
        VectorIndexStore store,
        string binaryPath,
        CancellationToken cancellationToken)
    {
        var documents = new List<VectorDocument>(store.Documents.Length);
        await using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var record in store.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.VectorOffset < 0 || record.VectorLength <= 0)
                throw new InvalidDataException("Vector index record has an invalid vector location.");

            var byteLength = checked(record.VectorLength * sizeof(float));
            if (byteLength > stream.Length || record.VectorOffset > stream.Length - byteLength)
                throw new InvalidDataException("Vector index binary store is truncated.");

            stream.Position = record.VectorOffset;
            var bytes = new byte[byteLength];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var vector = new float[record.VectorLength];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            documents.Add(record.ToDocument(vector));
        }

        return documents;
    }

    private static VectorMatch ToMatch(VectorDocument document, float score) =>
        new(
            document.Id,
            document.Kind,
            document.FileId,
            document.ContentUnitIds,
            score,
            document.Model,
            document.ChunkerVersion,
            document.ContentChecksum,
            document.Locator);

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

    private static void AddTopCandidate(
        List<ScoredVectorDocument> top,
        ScoredVectorDocument candidate,
        int count)
    {
        if (top.Count < count)
        {
            top.Add(candidate);
            return;
        }

        var worstIndex = 0;
        for (var i = 1; i < top.Count; i++)
        {
            if (IsWorse(top[i], top[worstIndex]))
                worstIndex = i;
        }

        if (IsBetter(candidate, top[worstIndex]))
            top[worstIndex] = candidate;
    }

    private static bool IsBetter(ScoredVectorDocument left, ScoredVectorDocument right) =>
        left.Score > right.Score ||
        left.Score.Equals(right.Score) &&
        string.Compare(left.Document.Id, right.Document.Id, StringComparison.Ordinal) < 0;

    private static bool IsWorse(ScoredVectorDocument left, ScoredVectorDocument right) =>
        left.Score < right.Score ||
        left.Score.Equals(right.Score) &&
        string.Compare(left.Document.Id, right.Document.Id, StringComparison.Ordinal) > 0;

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

    private sealed record ScoredVectorDocument(VectorDocument Document, float Score);

    private sealed record VectorIndexStore(
        int FormatVersion,
        string VectorFileName,
        VectorDocumentRecord[] Documents);

    private sealed record VectorDocumentRecord(
        string Id,
        VectorDocumentKind Kind,
        long FileId,
        long[] ContentUnitIds,
        EmbeddingModelInfo Model,
        string ChunkerVersion,
        string ContentChecksum,
        SourceLocator? Locator,
        long VectorOffset,
        int VectorLength)
    {
        public static VectorDocumentRecord FromDocument(VectorDocument document, long vectorOffset, int vectorLength) =>
            new(
                document.Id,
                document.Kind,
                document.FileId,
                document.ContentUnitIds.ToArray(),
                document.Model,
                document.ChunkerVersion,
                document.ContentChecksum,
                document.Locator,
                vectorOffset,
                vectorLength);

        public VectorDocument ToDocument(float[] vector) =>
            new(
                Id,
                Kind,
                FileId,
                ContentUnitIds,
                vector,
                Model,
                ChunkerVersion,
                ContentChecksum,
                Locator);
    }

    private sealed record LegacyVectorIndexStore(
        int FormatVersion,
        LegacyVectorDocumentRecord[] Documents);

    private sealed record LegacyVectorDocumentRecord(
        string Id,
        VectorDocumentKind Kind,
        long FileId,
        long[] ContentUnitIds,
        float[] Vector,
        EmbeddingModelInfo Model,
        string ChunkerVersion,
        string ContentChecksum,
        SourceLocator? Locator)
    {
        public VectorDocument ToDocument() =>
            new(
                Id,
                Kind,
                FileId,
                ContentUnitIds,
                Vector,
                Model,
                ChunkerVersion,
                ContentChecksum,
                Locator);
    }
}
