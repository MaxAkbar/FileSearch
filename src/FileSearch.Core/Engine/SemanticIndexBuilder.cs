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

public interface ISemanticIndexBuilder
{
    Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken);
}

public sealed class SemanticIndexBuilder : ISemanticIndexBuilder
{
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

    public async Task<SemanticIndexBuildResult> UpsertFileAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);

        var availability = await _embedder.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
            return SemanticIndexBuildResult.Unavailable(fileId, availability.Message);

        var units = await _contentUnits.GetContentUnitsForFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var unitIds = units.Select(unit => unit.Id).Where(id => id > 0).ToArray();
        if (unitIds.Length > 0)
            await _vectorIndex.DeleteAsync(unitIds, cancellationToken).ConfigureAwait(false);

        if (units.Count == 0)
        {
            return SemanticIndexBuildResult.Completed(
                fileId,
                0,
                0,
                0,
                null,
                "No content units found for semantic indexing.");
        }

        var chunks = _chunker.CreateChunks(units);
        if (chunks.Count == 0)
        {
            return SemanticIndexBuildResult.Completed(
                fileId,
                units.Count,
                0,
                0,
                null,
                "No non-empty chunks found for semantic indexing.");
        }

        var documents = new List<VectorDocument>(chunks.Count + 1);
        var chunkDocuments = new List<VectorDocument>(chunks.Count);
        EmbeddingModelInfo? model = null;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await _embedder.EmbedAsync(chunk.Text, cancellationToken).ConfigureAwait(false);
            model = embedding.Model;
            var document = new VectorDocument(
                chunk.ChunkKey,
                VectorDocumentKind.ContentChunk,
                chunk.FileId,
                chunk.ContentUnitIds,
                embedding.Vector,
                embedding.Model,
                chunk.ChunkerVersion,
                chunk.ContentHash,
                chunk.Locator);
            chunkDocuments.Add(document);
        }

        if (model is not null && chunkDocuments.Count > 0)
        {
            var fileChecksum = CreateFileChecksum(chunks);
            documents.Add(new VectorDocument(
                CreateFileVectorKey(fileId, fileChecksum),
                VectorDocumentKind.File,
                fileId,
                chunks.SelectMany(chunk => chunk.ContentUnitIds).Distinct().ToArray(),
                CreateFileVector(chunkDocuments),
                model,
                CreateChunkerVersion(chunks),
                fileChecksum));
        }

        documents.AddRange(chunkDocuments);
        await _vectorIndex.UpsertAsync(documents, cancellationToken).ConfigureAwait(false);
        return SemanticIndexBuildResult.Completed(
            fileId,
            units.Count,
            chunks.Count,
            documents.Count,
            model,
            $"Indexed {documents.Count:n0} semantic vector(s).");
    }

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
        double sum = 0;
        foreach (var value in vector)
            sum += value * value;

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }
}
