using System.Diagnostics;
using System.Numerics.Tensors;
using FileSearch.Core.Engine;

namespace FileSearch.Benchmarks;

internal sealed record SemanticModelSmokeReport(
    string ModelId,
    int TextCount,
    int LargestInferenceBatchSize,
    bool UsedCompatibilityFallback,
    double SingleElapsedMilliseconds,
    double BatchElapsedMilliseconds,
    double MinimumCosineSimilarity);

internal sealed class SemanticModelSmokeRunner
{
    private static readonly string[] s_texts =
    [
        "authentication migration plan",
        "database transaction recovery",
        "fast file name lookup",
        "watcher event freshness and indexing",
        "regular expression candidate narrowing",
        "local semantic search over source code",
        "vector quantization quality regression",
        "a longer passage that verifies padded token batches preserve the same embedding produced by individual model inference",
    ];

    public async Task<SemanticModelSmokeReport> RunAsync(
        string modelId,
        string modelDirectory,
        bool installModel,
        CancellationToken cancellationToken)
    {
        var options = new EmbeddingModelPackOptions
        {
            ModelPacksDirectory = modelDirectory,
            SelectedModelPackId = modelId,
        };
        var catalog = new EmbeddingModelPackCatalog();
        var store = new EmbeddingModelPackStore(options);
        if (installModel)
        {
            using var installer = new EmbeddingModelPackInstaller(catalog, store);
            var installed = await installer.InstallAsync(modelId, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (!installed.IsUsable)
                throw new InvalidOperationException(installed.Status);
        }

        var selected = await store.GetSelectedPackAsync(cancellationToken).ConfigureAwait(false);
        if (selected is null || !selected.IsUsable)
            throw new InvalidOperationException(selected?.Status ?? "The selected semantic model is not installed.");

        using var embedder = new OnnxTextEmbedder(store);
        var singles = new TextEmbedding[s_texts.Length];
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < s_texts.Length; i++)
        {
            singles[i] = await embedder.EmbedAsync(
                    s_texts[i],
                    TextEmbeddingInputKind.Document,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        var singleElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        started = Stopwatch.GetTimestamp();
        var batch = await embedder.EmbedBatchAsync(
                s_texts,
                TextEmbeddingInputKind.Document,
                cancellationToken)
            .ConfigureAwait(false);
        var batchElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (batch.Count != singles.Length)
            throw new InvalidDataException("Batched model smoke returned an unexpected result count.");

        var minimumCosine = 1f;
        for (var i = 0; i < singles.Length; i++)
            minimumCosine = Math.Min(minimumCosine, Cosine(singles[i].Vector.Span, batch[i].Vector.Span));

        return new SemanticModelSmokeReport(
            selected.Manifest.Id,
            s_texts.Length,
            embedder.LargestInferenceBatchSize,
            embedder.UsedBatchCompatibilityFallback,
            singleElapsed,
            batchElapsed,
            minimumCosine);
    }

    private static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0)
            return 0;
        var denominator = Math.Sqrt(
            TensorPrimitives.Dot(left, left) *
            TensorPrimitives.Dot(right, right));
        return denominator <= 0 ? 0 : (float)(TensorPrimitives.Dot(left, right) / denominator);
    }
}
