using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FileSearch.Core.Engine;

public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    private const int MaximumBatchSize = 32;
    private const int MaximumPaddedTokensPerBatch = 4096;

    private readonly IEmbeddingModelPackStore _modelPacks;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger _logger;
    private InstalledEmbeddingModelPack? _pack;
    private BertWordPieceTokenizer? _tokenizer;
    private InferenceSession? _session;
    private bool _initializationAttempted;
    private int _batchingDisabled;
    private int _largestInferenceBatchSize;

    public OnnxTextEmbedder(
        IEmbeddingModelPackStore modelPacks,
        ILogger<OnnxTextEmbedder>? logger = null)
    {
        _modelPacks = modelPacks ?? throw new ArgumentNullException(nameof(modelPacks));
        _logger = logger ?? NullLogger<OnnxTextEmbedder>.Instance;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initializationGate.Dispose();
    }

    internal int LargestInferenceBatchSize => Volatile.Read(ref _largestInferenceBatchSize);

    internal bool UsedBatchCompatibilityFallback => Volatile.Read(ref _batchingDisabled) != 0;

    public async Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var pack = await _modelPacks.GetSelectedPackAsync(cancellationToken).ConfigureAwait(false);
        if (pack is null)
            return TextEmbedderAvailability.Unavailable(UnavailableTextEmbedder.Message);

        if (!pack.IsUsable)
            return TextEmbedderAvailability.Unavailable(pack.Status);

        return TextEmbedderAvailability.Available;
    }

    public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken) =>
        EmbedAsync(text, TextEmbeddingInputKind.Document, cancellationToken);

    public async Task<TextEmbedding> EmbedAsync(
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken)
    {
        var embeddings = await EmbedBatchAsync(new[] { text }, inputKind, cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    public async Task<IReadOnlyList<TextEmbedding>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<TextEmbedding>();

        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var manifest = state.Pack.Manifest;
        var prefix = inputKind == TextEmbeddingInputKind.Query
            ? manifest.QueryPrefix
            : manifest.DocumentPrefix;
        var tokenized = texts
            .Select((text, index) => new IndexedTokenizedText(
                index,
                state.Tokenizer.Encode($"{prefix}{text}", manifest.MaxTokens)))
            .ToArray();
        var embeddings = new TextEmbedding[texts.Count];
        var supportsBatching = Volatile.Read(ref _batchingDisabled) == 0 &&
                               SupportsBatching(state.Session, manifest);
        var batchRanges = CreateBatchRanges(
            tokenized.Select(item => item.Tokens.InputIds.Length).ToArray(),
            supportsBatching ? MaximumBatchSize : 1,
            MaximumPaddedTokensPerBatch);
        foreach (var range in batchRanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = tokenized.AsSpan(range.Start, range.Count).ToArray();
            float[][] vectors;
            try
            {
                vectors = await Task.Run(
                        () => RunInferenceBatch(
                            state.Session,
                            manifest,
                            batch.Select(item => item.Tokens).ToArray()),
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordLargestBatch(batch.Length);
            }
            catch (OnnxRuntimeException ex) when (batch.Length > 1)
            {
                Interlocked.Exchange(ref _batchingDisabled, 1);
                _logger.LogWarning(
                    ex,
                    "Embedding model {ModelId} rejected batched inference; using batch size 1.",
                    manifest.Id);
                vectors = await Task.Run(
                        () => batch
                            .Select(item => RunInferenceBatch(state.Session, manifest, new[] { item.Tokens })[0])
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordLargestBatch(1);
            }

            for (var i = 0; i < batch.Length; i++)
            {
                var vector = vectors[i];
                if (manifest.Normalize)
                    Normalize(vector);
                embeddings[batch[i].Index] = new TextEmbedding(vector, manifest.ToModelInfo());
            }
        }

        return embeddings;
    }

    private void RecordLargestBatch(int batchSize)
    {
        var current = Volatile.Read(ref _largestInferenceBatchSize);
        while (batchSize > current)
        {
            var observed = Interlocked.CompareExchange(ref _largestInferenceBatchSize, batchSize, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private async Task<EmbedderState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (_pack is not null && _tokenizer is not null && _session is not null)
            return new EmbedderState(_pack, _tokenizer, _session);

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pack is not null && _tokenizer is not null && _session is not null)
                return new EmbedderState(_pack, _tokenizer, _session);

            if (_initializationAttempted)
                throw new InvalidOperationException("The selected embedding model pack could not be initialized.");

            _initializationAttempted = true;
            var pack = await _modelPacks.GetSelectedPackAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(UnavailableTextEmbedder.Message);
            if (!pack.IsUsable)
                throw new InvalidOperationException(pack.Status);

            try
            {
                var tokenizer = await BertWordPieceTokenizer.LoadAsync(
                        pack.VocabularyPath,
                        pack.Manifest.DoLowerCase,
                        cancellationToken)
                    .ConfigureAwait(false);
                var session = new InferenceSession(pack.ModelPath);
                _pack = pack;
                _tokenizer = tokenizer;
                _session = session;
                return new EmbedderState(pack, tokenizer, session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OnnxRuntimeException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not initialize embedding model pack {ModelId}.", pack.Manifest.Id);
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    internal static IReadOnlyList<EmbeddingBatchRange> CreateBatchRanges(
        IReadOnlyList<int> tokenCounts,
        int maximumBatchSize = MaximumBatchSize,
        int maximumPaddedTokens = MaximumPaddedTokensPerBatch)
    {
        ArgumentNullException.ThrowIfNull(tokenCounts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPaddedTokens);

        var ranges = new List<EmbeddingBatchRange>();
        var start = 0;
        while (start < tokenCounts.Count)
        {
            var count = 0;
            var maximumTokensInBatch = 0;
            while (start + count < tokenCounts.Count && count < maximumBatchSize)
            {
                var tokenCount = Math.Max(1, tokenCounts[start + count]);
                var nextMaximum = Math.Max(maximumTokensInBatch, tokenCount);
                var nextCount = count + 1;
                if (count > 0 && (long)nextMaximum * nextCount > maximumPaddedTokens)
                    break;

                maximumTokensInBatch = nextMaximum;
                count = nextCount;
            }

            ranges.Add(new EmbeddingBatchRange(start, count, maximumTokensInBatch));
            start += count;
        }

        return ranges;
    }

    private static bool SupportsBatching(
        InferenceSession session,
        EmbeddingModelPackManifest manifest)
    {
        if (!session.InputMetadata.TryGetValue(manifest.InputIdsName, out var metadata))
            return false;
        var dimensions = metadata.Dimensions;
        return dimensions.Length == 0 || dimensions[0] != 1;
    }

    private static float[][] RunInferenceBatch(
        InferenceSession session,
        EmbeddingModelPackManifest manifest,
        TokenizedText[] tokenized)
    {
        if (tokenized.Length == 0)
            return Array.Empty<float[]>();

        var length = tokenized.Max(item => item.InputIds.Length);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                manifest.InputIdsName,
                ToTensor(tokenized, item => item.InputIds, length)),
        };

        if (session.InputMetadata.ContainsKey(manifest.AttentionMaskName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                manifest.AttentionMaskName,
                ToTensor(tokenized, item => item.AttentionMask, length)));
        }

        if (!string.IsNullOrWhiteSpace(manifest.TokenTypeIdsName) &&
            session.InputMetadata.ContainsKey(manifest.TokenTypeIdsName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                manifest.TokenTypeIdsName,
                ToTensor(tokenized, item => item.TokenTypeIds, length)));
        }

        using var results = session.Run(inputs);
        var output = SelectOutput(results, manifest.OutputName);
        var tensor = output.AsTensor<float>();
        return PoolBatch(tensor, tokenized, manifest.Pooling);
    }

    private static DisposableNamedOnnxValue SelectOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        string outputName)
    {
        DisposableNamedOnnxValue? first = null;
        foreach (var result in results)
        {
            first ??= result;
            if (!string.IsNullOrWhiteSpace(outputName) &&
                string.Equals(result.Name, outputName, StringComparison.Ordinal))
            {
                return result;
            }
        }

        return first ?? throw new InvalidOperationException("Embedding model returned no outputs.");
    }

    private static DenseTensor<long> ToTensor(
        TokenizedText[] tokenized,
        Func<TokenizedText, long[]> selector,
        int length)
    {
        var tensor = new DenseTensor<long>(new[] { tokenized.Length, length });
        for (var batch = 0; batch < tokenized.Length; batch++)
        {
            var values = selector(tokenized[batch]);
            for (var i = 0; i < values.Length; i++)
                tensor[batch, i] = values[i];
        }

        return tensor;
    }

    private static float[][] PoolBatch(
        Tensor<float> tensor,
        TokenizedText[] tokenized,
        EmbeddingModelPooling pooling)
    {
        var dimensions = tensor.Dimensions.ToArray();
        var values = tensor.ToArray();
        if (dimensions.Length == 2)
        {
            if (dimensions[0] != tokenized.Length)
                throw new InvalidOperationException("Embedding model returned an unexpected batch size.");
            var outputDimension = dimensions[1];
            return Enumerable.Range(0, tokenized.Length)
                .Select(batch => values.AsSpan(batch * outputDimension, outputDimension).ToArray())
                .ToArray();
        }

        if (dimensions.Length != 3)
            throw new InvalidOperationException("Embedding model output must have rank 2 or 3.");

        if (dimensions[0] != tokenized.Length)
            throw new InvalidOperationException("Embedding model returned an unexpected batch size.");
        var tokenCount = dimensions[1];
        var dimension = dimensions[2];
        var vectors = new float[tokenized.Length][];
        for (var batch = 0; batch < tokenized.Length; batch++)
        {
            var vector = new float[dimension];
            vectors[batch] = vector;
            var batchOffset = batch * tokenCount * dimension;
            if (pooling == EmbeddingModelPooling.Cls)
            {
                Array.Copy(values, batchOffset, vector, 0, dimension);
                continue;
            }

            var attentionMask = tokenized[batch].AttentionMask;
            var included = 0;
            for (var token = 0; token < tokenCount && token < attentionMask.Length; token++)
            {
                if (attentionMask[token] == 0)
                    continue;

                included++;
                var offset = batchOffset + (token * dimension);
                for (var i = 0; i < dimension; i++)
                    vector[i] += values[offset + i];
            }

            if (included == 0)
                continue;

            for (var i = 0; i < vector.Length; i++)
                vector[i] /= included;
        }

        return vectors;
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
            sum += vector[i] * vector[i];

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    private sealed record EmbedderState(
        InstalledEmbeddingModelPack Pack,
        BertWordPieceTokenizer Tokenizer,
        InferenceSession Session);

    private sealed record IndexedTokenizedText(int Index, TokenizedText Tokens);
}

internal readonly record struct EmbeddingBatchRange(int Start, int Count, int PaddedTokenCount);
