using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Engine;

public sealed record SemanticIndexBuildResult(
    long FileId,
    bool IsAvailable,
    int ContentUnitCount,
    int ChunkCount,
    int VectorCount,
    EmbeddingModelInfo? Model,
    string Message)
{
    public bool WasIndexed => IsAvailable && VectorCount > 0;

    public static SemanticIndexBuildResult Unavailable(long fileId, string message) =>
        new(fileId, false, 0, 0, 0, null, message);

    public static SemanticIndexBuildResult Completed(
        long fileId,
        int contentUnitCount,
        int chunkCount,
        int vectorCount,
        EmbeddingModelInfo? model,
        string message) =>
        new(fileId, true, contentUnitCount, chunkCount, vectorCount, model, message);
}

public sealed record SemanticVectorBatchBuildResult(
    string Root,
    bool IsAvailable,
    IReadOnlyList<SemanticIndexBuildResult> Files,
    IReadOnlyList<VectorDocument> Documents,
    string Message)
{
    public int IndexedFileCount => Files.Count(file => file.WasIndexed);

    public int VectorCount => Documents.Count;
}

public interface ISemanticIndexBuilder
{
    Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken);

    Task<SemanticIndexBuildResult> UpsertFileAsync(
        string root,
        long fileId,
        CancellationToken cancellationToken) =>
        UpsertFileAsync(fileId, cancellationToken);
}

public interface ISemanticBatchIndexBuilder
{
    Task<SemanticVectorBatchBuildResult> BuildFilesAsync(
        string root,
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken);
}

public sealed class SemanticIndexBuilder : ISemanticIndexBuilder, ISemanticBatchIndexBuilder
{
    private const int MaximumChunksPerEmbeddingRequest = 64;

    private readonly IContentUnitReader _contentUnits;
    private readonly IContentChunker _chunker;
    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _vectorIndex;

    public SemanticIndexBuilder(
        IContentUnitReader contentUnits,
        IContentChunker chunker,
        ITextEmbedder embedder,
        IVectorIndex vectorIndex)
    {
        _contentUnits = contentUnits ?? throw new ArgumentNullException(nameof(contentUnits));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
    }

    public Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken) =>
        UpsertFileCoreAsync(string.Empty, fileId, cancellationToken);

    public Task<SemanticIndexBuildResult> UpsertFileAsync(
        string root,
        long fileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));
        return UpsertFileCoreAsync(IndexPath.NormalizeRoot(root), fileId, cancellationToken);
    }

    public Task<SemanticVectorBatchBuildResult> BuildFilesAsync(
        string root,
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root is required.", nameof(root));
        ArgumentNullException.ThrowIfNull(fileIds);
        return BuildFilesCoreAsync(IndexPath.NormalizeRoot(root), fileIds, cancellationToken);
    }

    private async Task<SemanticIndexBuildResult> UpsertFileCoreAsync(
        string normalizedRoot,
        long fileId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        var batch = await BuildFilesCoreAsync(normalizedRoot, new[] { fileId }, cancellationToken)
            .ConfigureAwait(false);
        var result = batch.Files.Single();
        if (!result.IsAvailable)
            return result;

        var documents = batch.Documents.Where(document => document.FileId == fileId).ToArray();
        if (!string.IsNullOrEmpty(normalizedRoot) && _vectorIndex is IVectorIndexBulkMutations bulkMutations)
        {
            var filePath = documents
                .Select(document => document.FilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? await _contentUnits.GetFilePathAsync(fileId, cancellationToken).ConfigureAwait(false);
            await bulkMutations.ReplaceFileAsync(normalizedRoot, fileId, filePath, documents, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        var units = await _contentUnits.GetContentUnitsForFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var unitIds = units.Select(unit => unit.Id).Where(id => id > 0).Distinct().ToArray();
        if (unitIds.Length > 0)
            await _vectorIndex.DeleteAsync(unitIds, cancellationToken).ConfigureAwait(false);
        if (documents.Length > 0)
            await _vectorIndex.UpsertAsync(documents, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<SemanticVectorBatchBuildResult> BuildFilesCoreAsync(
        string normalizedRoot,
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken)
    {
        var normalizedFileIds = fileIds.Where(id => id > 0).Distinct().ToArray();
        if (normalizedFileIds.Length == 0)
        {
            return new SemanticVectorBatchBuildResult(
                normalizedRoot,
                true,
                Array.Empty<SemanticIndexBuildResult>(),
                Array.Empty<VectorDocument>(),
                "No indexed files found for semantic indexing.");
        }

        var availability = await _embedder.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return new SemanticVectorBatchBuildResult(
                normalizedRoot,
                false,
                normalizedFileIds
                    .Select(fileId => SemanticIndexBuildResult.Unavailable(fileId, availability.Message))
                    .ToArray(),
                Array.Empty<VectorDocument>(),
                availability.Message);
        }

        var states = new List<FileBuildState>(normalizedFileIds.Length);
        var pendingChunks = new List<PendingChunk>(MaximumChunksPerEmbeddingRequest);
        EmbeddingModelInfo? batchModel = await _embedder.GetModelInfoAsync(cancellationToken).ConfigureAwait(false);

        foreach (var fileId in normalizedFileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var units = await _contentUnits.GetContentUnitsForFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            var filePath = await _contentUnits.GetFilePathAsync(fileId, cancellationToken).ConfigureAwait(false);
            var sourceChunks = _chunker.CreateChunks(units);
            var chunks = new List<ContentChunk>();
            foreach (var chunk in sourceChunks)
            {
                var parts = await _embedder.SplitDocumentAsync(chunk.Text, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < parts.Count; i++)
                {
                    if (parts.Count == 1)
                    {
                        chunks.Add(chunk);
                        continue;
                    }
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts[i]))).ToLowerInvariant();
                    chunks.Add(chunk with
                    {
                        ChunkKey = $"{chunk.ChunkKey}:token-part:{i}:{hash[..16]}",
                        Text = parts[i],
                        ContentHash = hash,
                        ChunkerVersion = $"{chunk.ChunkerVersion}+token-budget-v1",
                    });
                }
            }
            var state = new FileBuildState(
                fileId,
                filePath,
                units.Count,
                chunks.Count,
                chunks.SelectMany(chunk => chunk.ContentUnitIds).Distinct().ToArray(),
                chunks.Count == 0 ? string.Empty : CreateFileChecksum(chunks),
                CreateChunkerVersion(chunks));
            states.Add(state);

            foreach (var chunk in chunks)
            {
                pendingChunks.Add(new PendingChunk(state, chunk));
                if (pendingChunks.Count >= MaximumChunksPerEmbeddingRequest)
                {
                    batchModel = await EmbedPendingChunksAsync(
                            normalizedRoot,
                            pendingChunks,
                            batchModel,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (pendingChunks.Count > 0)
        {
            batchModel = await EmbedPendingChunksAsync(
                    normalizedRoot,
                    pendingChunks,
                    batchModel,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var documents = new List<VectorDocument>();
        var results = new List<SemanticIndexBuildResult>(states.Count);
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.ChunkCount == 0)
            {
                var message = state.ContentUnitCount == 0
                    ? "No content units found for semantic indexing."
                    : "No non-empty chunks found for semantic indexing.";
                results.Add(SemanticIndexBuildResult.Completed(
                    state.FileId,
                    state.ContentUnitCount,
                    0,
                    0,
                    null,
                    message));
                continue;
            }

            if (state.ChunkDocuments.Count != state.ChunkCount)
                throw new InvalidDataException("Semantic embedding batch returned an incomplete file result.");
            var model = state.ChunkDocuments[0].Model;
            documents.Add(new VectorDocument(
                CreateFileVectorKey(state.FileId, state.FileChecksum),
                VectorDocumentKind.File,
                state.FileId,
                state.ContentUnitIds,
                CreateFileVector(state.ChunkDocuments),
                model,
                state.ChunkerVersion,
                state.FileChecksum,
                root: normalizedRoot,
                filePath: state.FilePath));
            documents.AddRange(state.ChunkDocuments);
            results.Add(SemanticIndexBuildResult.Completed(
                state.FileId,
                state.ContentUnitCount,
                state.ChunkCount,
                state.ChunkDocuments.Count + 1,
                model,
                $"Indexed {state.ChunkDocuments.Count + 1:n0} semantic vector(s)."));
        }

        return new SemanticVectorBatchBuildResult(
            normalizedRoot,
            true,
            results,
            documents,
            $"Built {documents.Count:n0} semantic vector(s) for {states.Count:n0} file(s).");
    }

    private async Task<EmbeddingModelInfo?> EmbedPendingChunksAsync(
        string normalizedRoot,
        List<PendingChunk> pendingChunks,
        EmbeddingModelInfo? expectedModel,
        CancellationToken cancellationToken)
    {
        var embeddings = await _embedder.EmbedBatchAsync(
                pendingChunks.Select(item => item.Chunk.Text).ToArray(),
                TextEmbeddingInputKind.Document,
                cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != pendingChunks.Count)
            throw new InvalidDataException("Text embedder returned an unexpected batch size.");

        for (var i = 0; i < pendingChunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = pendingChunks[i];
            var embedding = embeddings[i];
            expectedModel ??= embedding.Model;
            if (!ModelsMatch(expectedModel, embedding.Model))
                throw new InvalidDataException("Text embedder changed models during a semantic index build.");

            pending.State.ChunkDocuments.Add(new VectorDocument(
                pending.Chunk.ChunkKey,
                VectorDocumentKind.ContentChunk,
                pending.Chunk.FileId,
                pending.Chunk.ContentUnitIds,
                embedding.Vector,
                embedding.Model,
                pending.Chunk.ChunkerVersion,
                pending.Chunk.ContentHash,
                pending.Chunk.Locator,
                normalizedRoot,
                pending.State.FilePath));
        }

        pendingChunks.Clear();
        return expectedModel;
    }

    private static bool ModelsMatch(EmbeddingModelInfo left, EmbeddingModelInfo right) =>
        left.Dimension == right.Dimension &&
        string.Equals(left.ModelId, right.ModelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ModelVersion, right.ModelVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.QuantizationVersion, right.QuantizationVersion, StringComparison.OrdinalIgnoreCase);

    private static string CreateFileVectorKey(long fileId, string contentChecksum) =>
        $"file:{fileId}:{contentChecksum[..16]}";

    private static string CreateFileChecksum(IReadOnlyList<ContentChunk> chunks)
    {
        var builder = new StringBuilder();
        builder.Append("semantic-file-vector").Append('\n');
        foreach (var chunk in chunks.OrderBy(chunk => chunk.ChunkKey, StringComparer.Ordinal))
            builder.Append(chunk.ChunkKey).Append(':').Append(chunk.ContentHash).Append('\n');

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateChunkerVersion(IReadOnlyList<ContentChunk> chunks)
    {
        var versions = chunks
            .Select(chunk => chunk.ChunkerVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToArray();
        return versions.Length == 0 ? string.Empty : string.Join("+", versions);
    }

    private static float[] CreateFileVector(List<VectorDocument> chunkDocuments)
    {
        var dimension = chunkDocuments[0].Vector.Count;
        var vector = new float[dimension];
        foreach (var document in chunkDocuments)
        {
            for (var i = 0; i < dimension; i++)
                vector[i] += document.Vector[i];
        }

        for (var i = 0; i < dimension; i++)
            vector[i] /= chunkDocuments.Count;

        Normalize(vector);
        return vector;
    }

    private static void Normalize(float[] vector)
    {
        var sum = TensorPrimitives.Dot(vector, vector);
        var norm = Math.Sqrt(sum);
        if (norm <= 0)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    private sealed class FileBuildState
    {
        public FileBuildState(
            long fileId,
            string? filePath,
            int contentUnitCount,
            int chunkCount,
            long[] contentUnitIds,
            string fileChecksum,
            string chunkerVersion)
        {
            FileId = fileId;
            FilePath = filePath ?? string.Empty;
            ContentUnitCount = contentUnitCount;
            ChunkCount = chunkCount;
            ContentUnitIds = contentUnitIds;
            FileChecksum = fileChecksum;
            ChunkerVersion = chunkerVersion;
        }

        public long FileId { get; }

        public string FilePath { get; }

        public int ContentUnitCount { get; }

        public int ChunkCount { get; }

        public long[] ContentUnitIds { get; }

        public string FileChecksum { get; }

        public string ChunkerVersion { get; }

        public List<VectorDocument> ChunkDocuments { get; } = new();
    }

    private sealed record PendingChunk(FileBuildState State, ContentChunk Chunk);
}
