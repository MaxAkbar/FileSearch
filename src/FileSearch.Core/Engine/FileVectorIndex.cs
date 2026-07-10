using System.Text.Json;
using FileSearch.Core.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Engine;

public sealed class FileVectorIndex : IVectorIndex, IVectorIndexBulkMutations, IDisposable
{
    private const int LegacyJsonFormatVersion = 1;
    private const int MonolithicBinaryFormatVersion = 2;
    private const int FormatVersion = 3;
    private const int SegmentFormatVersion = 1;
    private const int MaximumFileOverlaysPerRoot = 128;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, VectorDocument> _documents = new(StringComparer.Ordinal);
    private readonly VectorIndexOptions _options;
    private readonly ILogger _logger;
    private List<VectorSegmentDescriptor> _segments = new();
    private bool _loaded;
    private int _loadedFormatVersion;
    private string _loadedPath = string.Empty;
    private DateTime? _loadedLastWriteUtc;
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

    internal long InMemoryVectorPayloadBytes => _documents.Values.Sum(document =>
        document.Vector is QuantizedVector ? document.Vector.Count : (long)document.Vector.Count * sizeof(float));

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
            var nextDocuments = CloneDocuments();
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextDocuments[CreateDocumentKey(document)] = document;
            }

            await PersistSnapshotAsync(nextDocuments, cancellationToken).ConfigureAwait(false);
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
            var nextDocuments = CloneDocuments();
            var removed = RemoveDocuments(
                nextDocuments,
                document => document.ContentUnitIds.Any(idSet.Contains));
            if (removed)
                await PersistSnapshotAsync(nextDocuments, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyCollection<long>? fileIds = null,
        IReadOnlyCollection<string>? roots = null)
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

        var result = await Task.Run(
                () => searchIndex.Search(
                    queryVector,
                    count,
                    model,
                    kind,
                    fileIds,
                    roots,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        LastSearchDiagnostics = result.Diagnostics;
        return result.Matches;
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

    public async Task ReplaceFileAsync(
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

            var replacementPaths = documents
                .Select(document => document.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(normalizedFilePath))
                replacementPaths.Add(normalizedFilePath);
            var nextDocuments = CloneDocuments();
            RemoveDocuments(
                nextDocuments,
                document =>
                    document.FileId == fileId ||
                    !string.IsNullOrEmpty(document.FilePath) && replacementPaths.Contains(document.FilePath));
            AddDocuments(nextDocuments, documents, cancellationToken);

            var retainedSegments = _segments
                .Where(segment =>
                    segment.Scope != VectorSegmentScope.File ||
                    segment.FileId != fileId &&
                    (string.IsNullOrEmpty(segment.FilePath) || !replacementPaths.Contains(segment.FilePath)))
                .ToList();
            var overlayCount = retainedSegments.Count(segment =>
                segment.Scope == VectorSegmentScope.File &&
                string.Equals(segment.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));

            if (overlayCount + 1 >= MaximumFileOverlaysPerRoot)
            {
                retainedSegments.RemoveAll(segment =>
                    segment.Scope != VectorSegmentScope.Snapshot &&
                    string.Equals(segment.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
                var rootDocuments = nextDocuments.Values
                    .Where(document => string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await PersistScopedSegmentAsync(
                        nextDocuments,
                        retainedSegments,
                        VectorSegmentScope.Root,
                        normalizedRoot,
                        fileId: null,
                        filePath: string.Empty,
                        rootDocuments.Select(document => document.FileId).Distinct().ToArray(),
                        rootDocuments,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await PersistScopedSegmentAsync(
                        nextDocuments,
                        retainedSegments,
                        VectorSegmentScope.File,
                        normalizedRoot,
                        fileId,
                        normalizedFilePath,
                        new[] { fileId },
                        documents,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceRootAsync(
        string root,
        IReadOnlyCollection<long> fileIds,
        IReadOnlyCollection<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        ArgumentNullException.ThrowIfNull(fileIds);
        ValidateReplacementDocuments(normalizedRoot, documents);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedFileIds = fileIds.Where(id => id > 0).Distinct().ToArray();
        var fileIdSet = normalizedFileIds.ToHashSet();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

            var nextDocuments = CloneDocuments();
            RemoveDocuments(
                nextDocuments,
                document =>
                    string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                    fileIdSet.Contains(document.FileId));
            AddDocuments(nextDocuments, documents, cancellationToken);

            var retainedSegments = _segments
                .Where(segment =>
                    segment.Scope == VectorSegmentScope.Snapshot ||
                    !string.Equals(segment.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            await PersistScopedSegmentAsync(
                    nextDocuments,
                    retainedSegments,
                    VectorSegmentScope.Root,
                    normalizedRoot,
                    fileId: null,
                    filePath: string.Empty,
                    normalizedFileIds,
                    documents,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteFileAsync(
        string root,
        long fileId,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
            var nextDocuments = CloneDocuments();
            RemoveDocuments(nextDocuments, document => document.FileId == fileId);
            var retainedSegments = _segments
                .Where(segment => segment.Scope != VectorSegmentScope.File || segment.FileId != fileId)
                .ToList();
            await PersistScopedSegmentAsync(
                    nextDocuments,
                    retainedSegments,
                    VectorSegmentScope.File,
                    normalizedRoot,
                    fileId,
                    filePath: string.Empty,
                    new[] { fileId },
                    Array.Empty<VectorDocument>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteRootAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRequiredRoot(root);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
            var nextDocuments = CloneDocuments();
            var replacedFileIds = nextDocuments.Values
                .Where(document => string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .Select(document => document.FileId)
                .Distinct()
                .ToArray();
            RemoveDocuments(
                nextDocuments,
                document => string.Equals(document.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase));
            var retainedSegments = _segments
                .Where(segment =>
                    segment.Scope == VectorSegmentScope.Snapshot ||
                    !string.Equals(segment.Root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            await PersistScopedSegmentAsync(
                    nextDocuments,
                    retainedSegments,
                    VectorSegmentScope.Root,
                    normalizedRoot,
                    fileId: null,
                    filePath: string.Empty,
                    replacedFileIds,
                    Array.Empty<VectorDocument>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VectorIndexStats> GetStatsAsync(
        IReadOnlyCollection<long> contentUnitIds,
        CancellationToken cancellationToken,
        EmbeddingModelInfo? model = null,
        IReadOnlyCollection<string>? roots = null)
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

        return CreateStats(snapshot, idSet, model, NormalizeRoots(roots));
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var path = NormalizeIndexPath();
        var lastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        if (_loaded &&
            string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) &&
            _loadedLastWriteUtc == lastWriteUtc)
        {
            return;
        }

        _loaded = true;
        _loadedFormatVersion = 0;
        _loadedPath = path;
        _loadedLastWriteUtc = lastWriteUtc;
        _documents.Clear();
        _segments.Clear();
        _searchIndex = VectorSearchIndex.Empty;
        _searchIndexDirty = true;
        if (!File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var jsonDocument = JsonDocument.Parse(json);
            var formatVersion = jsonDocument.RootElement.TryGetProperty(nameof(VectorIndexManifest.FormatVersion), out var versionElement)
                ? versionElement.GetInt32()
                : LegacyJsonFormatVersion;
            _loadedFormatVersion = formatVersion;

            switch (formatVersion)
            {
                case LegacyJsonFormatVersion:
                    LoadLegacyJson(json);
                    break;
                case MonolithicBinaryFormatVersion:
                    await LoadMonolithicBinaryAsync(json, path, cancellationToken).ConfigureAwait(false);
                    break;
                case FormatVersion:
                    await LoadManifestAsync(json, path, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    _logger.LogWarning("Ignoring unsupported vector index format at {Path}.", path);
                    _documents.Clear();
                    _segments.Clear();
                    break;
            }

            _searchIndexDirty = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            _logger.LogWarning(ex, "Could not load vector index at {Path}; starting with an empty vector index.", path);
            _documents.Clear();
            _segments.Clear();
            _searchIndex = VectorSearchIndex.Empty;
            _searchIndexDirty = true;
        }
    }

    private void LoadLegacyJson(string json)
    {
        var store = JsonSerializer.Deserialize<LegacyVectorIndexStore>(json, s_jsonOptions);
        if (store?.FormatVersion != LegacyJsonFormatVersion)
            throw new InvalidDataException("Legacy vector index has an invalid format version.");

        foreach (var document in store.Documents.Select(record => record.ToDocument()))
            _documents[CreateDocumentKey(document)] = document;
    }

    private async Task LoadMonolithicBinaryAsync(
        string json,
        string indexPath,
        CancellationToken cancellationToken)
    {
        var store = JsonSerializer.Deserialize<MonolithicVectorIndexStore>(json, s_jsonOptions)
            ?? throw new InvalidDataException("Vector index metadata is invalid.");
        var binaryPath = ResolveSiblingPath(indexPath, store.VectorFileName);
        if (!File.Exists(binaryPath))
            throw new InvalidDataException($"Vector index binary store is missing at {binaryPath}.");

        foreach (var document in await ReadFloatDocumentsAsync(store.Documents, binaryPath, cancellationToken).ConfigureAwait(false))
            _documents[CreateDocumentKey(document)] = document;
    }

    private async Task LoadManifestAsync(
        string json,
        string indexPath,
        CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<VectorIndexManifest>(json, s_jsonOptions)
            ?? throw new InvalidDataException("Vector index manifest is invalid.");
        if (manifest.FormatVersion != FormatVersion)
            throw new InvalidDataException("Vector index manifest has an invalid format version.");

        var segmentDirectory = GetSegmentDirectory(indexPath);
        foreach (var descriptor in manifest.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplySegmentRemoval(_documents, descriptor);
            var metadataPath = ResolveSegmentPath(segmentDirectory, descriptor.MetadataFileName);
            var binaryPath = ResolveSegmentPath(segmentDirectory, descriptor.VectorFileName);
            var segmentJson = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            var segment = JsonSerializer.Deserialize<VectorSegmentStore>(segmentJson, s_jsonOptions)
                ?? throw new InvalidDataException("Vector segment metadata is invalid.");
            if (segment.FormatVersion != SegmentFormatVersion)
                throw new InvalidDataException("Vector segment format is unsupported.");
            var segmentDocuments = segment.Encoding switch
            {
                VectorStorageEncoding.Float32 =>
                    await ReadFloatDocumentsAsync(segment.Documents, binaryPath, cancellationToken).ConfigureAwait(false),
                VectorStorageEncoding.Int8Symmetric =>
                    await ReadQuantizedDocumentsAsync(segment.Documents, binaryPath, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Vector segment encoding is unsupported."),
            };
            foreach (var document in segmentDocuments)
                _documents[CreateDocumentKey(document)] = document;
        }

        _segments = manifest.Segments.ToList();
        CleanupOrphanedSegments(indexPath, _segments);
    }

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_loadedFormatVersion == FormatVersion)
            return;

        if (_loadedFormatVersion == 0 && _documents.Count == 0 && _segments.Count == 0)
        {
            _loadedFormatVersion = FormatVersion;
            return;
        }

        var migratedDocuments = CloneDocuments();
        RemoveDocuments(migratedDocuments, document => string.IsNullOrEmpty(document.Root));
        await PersistSnapshotAsync(migratedDocuments, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSnapshotAsync(
        Dictionary<string, VectorDocument> nextDocuments,
        CancellationToken cancellationToken)
    {
        var descriptor = await WriteSegmentAsync(
                VectorSegmentScope.Snapshot,
                root: string.Empty,
                fileId: null,
                filePath: string.Empty,
                replacedFileIds: Array.Empty<long>(),
                nextDocuments.Values,
                cancellationToken)
            .ConfigureAwait(false);
        var nextSegments = new List<VectorSegmentDescriptor> { descriptor };
        try
        {
            await CommitManifestAsync(nextSegments, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteSegmentFiles(NormalizeIndexPath(), descriptor);
            throw;
        }

        if (_options.UseInt8Storage)
            QuantizeDocuments(nextDocuments);
        PublishState(nextDocuments, nextSegments);
        CleanupOrphanedSegments(NormalizeIndexPath(), nextSegments);
    }

    private async Task PersistScopedSegmentAsync(
        Dictionary<string, VectorDocument> nextDocuments,
        List<VectorSegmentDescriptor> retainedSegments,
        VectorSegmentScope scope,
        string root,
        long? fileId,
        string filePath,
        IReadOnlyCollection<long> replacedFileIds,
        IEnumerable<VectorDocument> segmentDocuments,
        CancellationToken cancellationToken)
    {
        var descriptor = await WriteSegmentAsync(
                scope,
                root,
                fileId,
                filePath,
                replacedFileIds,
                segmentDocuments,
                cancellationToken)
            .ConfigureAwait(false);
        var nextSegments = retainedSegments.Append(descriptor).ToList();
        try
        {
            await CommitManifestAsync(nextSegments, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteSegmentFiles(NormalizeIndexPath(), descriptor);
            throw;
        }

        if (_options.UseInt8Storage)
            QuantizeDocuments(nextDocuments);
        PublishState(nextDocuments, nextSegments);
        CleanupOrphanedSegments(NormalizeIndexPath(), nextSegments);
    }

    private async Task<VectorSegmentDescriptor> WriteSegmentAsync(
        VectorSegmentScope scope,
        string root,
        long? fileId,
        string filePath,
        IReadOnlyCollection<long> replacedFileIds,
        IEnumerable<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        var indexPath = NormalizeIndexPath();
        var segmentDirectory = GetSegmentDirectory(indexPath);
        Directory.CreateDirectory(segmentDirectory);
        var id = Guid.NewGuid().ToString("N");
        var metadataFileName = $"{id}.json";
        var vectorFileName = $"{id}.bin";
        var metadataPath = Path.Combine(segmentDirectory, metadataFileName);
        var binaryPath = Path.Combine(segmentDirectory, vectorFileName);
        var tempMetadataPath = $"{metadataPath}.tmp";
        var tempBinaryPath = $"{binaryPath}.tmp";
        var orderedDocuments = documents
            .OrderBy(document => document.Root, StringComparer.OrdinalIgnoreCase)
            .ThenBy(document => document.Id, StringComparer.Ordinal)
            .ToArray();
        var resolvedFilePath = scope == VectorSegmentScope.File
            ? string.IsNullOrWhiteSpace(filePath)
                ? orderedDocuments
                    .Select(document => document.FilePath)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty
                : IndexPath.NormalizeFile(filePath)
            : string.Empty;

        try
        {
            var records = new List<VectorDocumentRecord>(orderedDocuments.Length);
            await using (var binaryStream = new FileStream(tempBinaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var document in orderedDocuments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = binaryStream.Position;
                    if (_options.UseInt8Storage)
                    {
                        var vector = document.Vector as QuantizedVector ?? QuantizedVector.Create(document.Vector);
                        var bytes = new byte[vector.Count];
                        vector.Values.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(bytes.AsSpan()));
                        await binaryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        records.Add(VectorDocumentRecord.FromDocument(
                            document,
                            offset,
                            vector.Count,
                            vector.Scale,
                            vector.SquaredNorm));
                    }
                    else
                    {
                        var vector = document.Vector.ToArray();
                        var bytes = new byte[vector.Length * sizeof(float)];
                        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                        await binaryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        records.Add(VectorDocumentRecord.FromDocument(document, offset, vector.Length));
                    }
                }
            }

            var segment = new VectorSegmentStore(
                SegmentFormatVersion,
                _options.UseInt8Storage
                    ? VectorStorageEncoding.Int8Symmetric
                    : VectorStorageEncoding.Float32,
                records.ToArray());
            await using (var metadataStream = new FileStream(tempMetadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(metadataStream, segment, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempBinaryPath, binaryPath);
            File.Move(tempMetadataPath, metadataPath);
            return new VectorSegmentDescriptor(
                id,
                scope,
                root,
                fileId,
                replacedFileIds.Where(value => value > 0).Distinct().ToArray(),
                metadataFileName,
                vectorFileName,
                resolvedFilePath);
        }
        catch
        {
            DeleteIfExists(tempMetadataPath);
            DeleteIfExists(tempBinaryPath);
            DeleteIfExists(metadataPath);
            DeleteIfExists(binaryPath);
            throw;
        }
    }

    private async Task CommitManifestAsync(
        IReadOnlyCollection<VectorSegmentDescriptor> segments,
        CancellationToken cancellationToken)
    {
        var path = NormalizeIndexPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var manifest = new VectorIndexManifest(FormatVersion, segments.ToArray());
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            PublishFile(tempPath, path);
            _loadedPath = path;
            _loadedLastWriteUtc = File.GetLastWriteTimeUtc(path);
            _loadedFormatVersion = FormatVersion;
        }
        finally
        {
            DeleteIfExists(tempPath);
        }
    }

    private void PublishState(
        Dictionary<string, VectorDocument> nextDocuments,
        List<VectorSegmentDescriptor> nextSegments)
    {
        _documents.Clear();
        foreach (var pair in nextDocuments)
            _documents[pair.Key] = pair.Value;
        _segments = nextSegments;
        _searchIndexDirty = true;
    }

    private VectorSearchIndex GetSearchIndex()
    {
        if (!_searchIndexDirty)
            return _searchIndex;

        _searchIndex = new VectorSearchIndex(_documents.Values.ToArray(), _options);
        _searchIndexDirty = false;
        return _searchIndex;
    }

    private Dictionary<string, VectorDocument> CloneDocuments() =>
        new(_documents, StringComparer.Ordinal);

    private static void AddDocuments(
        Dictionary<string, VectorDocument> target,
        IEnumerable<VectorDocument> documents,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target[CreateDocumentKey(document)] = document;
        }
    }

    private static bool RemoveDocuments(
        Dictionary<string, VectorDocument> target,
        Func<VectorDocument, bool> predicate)
    {
        var keys = target
            .Where(pair => predicate(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
            target.Remove(key);
        return keys.Length > 0;
    }

    private static void ApplySegmentRemoval(
        Dictionary<string, VectorDocument> documents,
        VectorSegmentDescriptor descriptor)
    {
        switch (descriptor.Scope)
        {
            case VectorSegmentScope.Snapshot:
                documents.Clear();
                break;
            case VectorSegmentScope.Root:
                var replacedFileIds = descriptor.ReplacedFileIds.ToHashSet();
                RemoveDocuments(
                    documents,
                    document =>
                        string.Equals(document.Root, descriptor.Root, StringComparison.OrdinalIgnoreCase) ||
                        replacedFileIds.Contains(document.FileId));
                break;
            case VectorSegmentScope.File:
                if (descriptor.FileId is null or <= 0)
                    throw new InvalidDataException("File vector segment is missing a file ID.");
                RemoveDocuments(
                    documents,
                    document =>
                        document.FileId == descriptor.FileId.Value ||
                        !string.IsNullOrEmpty(descriptor.FilePath) &&
                        string.Equals(document.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase));
                break;
            default:
                throw new InvalidDataException("Vector segment has an unsupported scope.");
        }
    }

    private static async Task<IReadOnlyList<VectorDocument>> ReadFloatDocumentsAsync(
        IReadOnlyList<VectorDocumentRecord> records,
        string binaryPath,
        CancellationToken cancellationToken)
    {
        var documents = new List<VectorDocument>(records.Count);
        await using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var record in records)
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

    private static async Task<IReadOnlyList<VectorDocument>> ReadQuantizedDocumentsAsync(
        IReadOnlyList<VectorDocumentRecord> records,
        string binaryPath,
        CancellationToken cancellationToken)
    {
        var documents = new List<VectorDocument>(records.Count);
        await using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.VectorOffset < 0 || record.VectorLength <= 0 ||
                !float.IsFinite(record.VectorScale) || record.VectorScale <= 0)
            {
                throw new InvalidDataException("Vector index record has invalid quantized vector metadata.");
            }

            var byteLength = record.VectorLength;
            if (byteLength > stream.Length || record.VectorOffset > stream.Length - byteLength)
                throw new InvalidDataException("Vector index binary store is truncated.");
            stream.Position = record.VectorOffset;
            var bytes = new byte[byteLength];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var values = new sbyte[byteLength];
            Buffer.BlockCopy(bytes, 0, values, 0, byteLength);
            documents.Add(record.ToDocument(new QuantizedVector(
                values,
                record.VectorScale,
                record.VectorSquaredNorm)));
        }

        return documents;
    }

    private static void QuantizeDocuments(Dictionary<string, VectorDocument> documents)
    {
        foreach (var key in documents.Keys.ToArray())
        {
            var document = documents[key];
            if (document.Vector is not QuantizedVector)
                documents[key] = document.WithVector(QuantizedVector.Create(document.Vector));
        }
    }

    private string NormalizeIndexPath()
    {
        if (string.IsNullOrWhiteSpace(_options.IndexPath))
            throw new InvalidOperationException("Vector index path is required.");
        return Path.GetFullPath(_options.IndexPath);
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

    private static string GetSegmentDirectory(string indexPath)
    {
        var directory = Path.GetDirectoryName(indexPath) ?? string.Empty;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(indexPath)}.segments");
    }

    private static string ResolveSiblingPath(string indexPath, string fileName)
    {
        ValidateFileName(fileName);
        return Path.Combine(Path.GetDirectoryName(indexPath) ?? string.Empty, fileName);
    }

    private static string ResolveSegmentPath(string segmentDirectory, string fileName)
    {
        ValidateFileName(fileName);
        var path = Path.Combine(segmentDirectory, fileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"Vector segment file is missing at {path}.");
        return path;
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Vector index contains an invalid file name.");
        }
    }

    private static void PublishFile(string tempPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tempPath, destinationPath);
    }

    private static void CleanupOrphanedSegments(
        string indexPath,
        IReadOnlyCollection<VectorSegmentDescriptor> segments)
    {
        var directory = GetSegmentDirectory(indexPath);
        if (!Directory.Exists(directory))
            return;

        var referenced = segments
            .SelectMany(segment => new[] { segment.MetadataFileName, segment.VectorFileName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!referenced.Contains(Path.GetFileName(path)) && File.GetLastWriteTimeUtc(path) < cutoff)
                DeleteIfExists(path);
        }
    }

    private static void DeleteSegmentFiles(
        string indexPath,
        IEnumerable<VectorSegmentDescriptor> segments)
    {
        foreach (var segment in segments)
            DeleteSegmentFiles(indexPath, segment);
    }

    private static void DeleteSegmentFiles(
        string indexPath,
        VectorSegmentDescriptor segment)
    {
        var directory = GetSegmentDirectory(indexPath);
        DeleteIfExists(Path.Combine(directory, segment.MetadataFileName));
        DeleteIfExists(Path.Combine(directory, segment.VectorFileName));
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CreateDocumentKey(VectorDocument document) =>
        $"{document.Root}\0{document.Id}";

    private static bool MatchesModel(EmbeddingModelInfo documentModel, EmbeddingModelInfo? queryModel) =>
        queryModel is null ||
        documentModel.Dimension == queryModel.Dimension &&
        string.Equals(documentModel.ModelId, queryModel.ModelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.ModelVersion, queryModel.ModelVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentModel.QuantizationVersion, queryModel.QuantizationVersion, StringComparison.OrdinalIgnoreCase);

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

    private enum VectorSegmentScope
    {
        Snapshot,
        Root,
        File,
    }

    private enum VectorStorageEncoding
    {
        Float32,
        Int8Symmetric,
    }

    private sealed record VectorIndexManifest(
        int FormatVersion,
        VectorSegmentDescriptor[] Segments);

    private sealed record VectorSegmentDescriptor(
        string Id,
        VectorSegmentScope Scope,
        string Root,
        long? FileId,
        long[] ReplacedFileIds,
        string MetadataFileName,
        string VectorFileName,
        string FilePath = "");

    private sealed record VectorSegmentStore(
        int FormatVersion,
        VectorStorageEncoding Encoding,
        VectorDocumentRecord[] Documents);

    private sealed record MonolithicVectorIndexStore(
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
        int VectorLength,
        string Root = "",
        float VectorScale = 1,
        long VectorSquaredNorm = 0,
        string FilePath = "")
    {
        public static VectorDocumentRecord FromDocument(
            VectorDocument document,
            long vectorOffset,
            int vectorLength,
            float vectorScale = 1,
            long vectorSquaredNorm = 0) =>
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
                vectorLength,
                document.Root,
                vectorScale,
                vectorSquaredNorm,
                document.FilePath);

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
                Locator,
                Root,
                FilePath);

        public VectorDocument ToDocument(QuantizedVector vector) =>
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
