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
    private IEmbeddingTokenizer? _tokenizer;
    private InferenceSession? _session;
    private bool _disposed;
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
        _initializationGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _tokenizer?.Dispose();
        }
        finally { _initializationGate.Release(); }
    }

    internal int LargestInferenceBatchSize => Volatile.Read(ref _largestInferenceBatchSize);

    internal bool UsedBatchCompatibilityFallback => Volatile.Read(ref _batchingDisabled) != 0;

    public async Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await GetStateAsync(cancellationToken).ConfigureAwait(false);
            return TextEmbedderAvailability.Available;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            return TextEmbedderAvailability.Unavailable(ex.Message);
        }
        finally { _initializationGate.Release(); }
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
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await EmbedBatchCoreAsync(texts, inputKind, cancellationToken).ConfigureAwait(false);
        }
        finally { _initializationGate.Release(); }
    }

    private async Task<IReadOnlyList<TextEmbedding>> EmbedBatchCoreAsync(
        IReadOnlyList<string> texts, TextEmbeddingInputKind inputKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<TextEmbedding>();

        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var manifest = state.Pack.Manifest;
        var prefix = inputKind == TextEmbeddingInputKind.Query
            ? manifest.QueryPrefix
            : manifest.DocumentPrefix;
        var tokenized = new IndexedTokenizedText[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tokenized[i] = new IndexedTokenizedText(i, state.Tokenizer.Encode($"{prefix}{texts[i]}", manifest.MaxTokens));
        }
        var embeddings = new TextEmbedding[texts.Count];
        var supportsBatching = Volatile.Read(ref _batchingDisabled) == 0 &&
                               SupportsBatching(state.Session, manifest);
        var batchRanges = CreateBatchRanges(
            tokenized.Select(item => item.Tokens.InputIds.Length).ToArray(),
            supportsBatching ? Math.Clamp(manifest.MaximumBatchSize, 1, MaximumBatchSize) : 1,
            Math.Clamp(manifest.MaximumPaddedTokensPerBatch, manifest.MaxTokens, MaximumPaddedTokensPerBatch));
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
                            batch.Select(item => item.Tokens).ToArray(), cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordLargestBatch(batch.Length);
            }
            catch (OnnxRuntimeException ex) when (batch.Length > 1 && !cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _batchingDisabled, 1);
                _logger.LogWarning(
                    ex,
                    "Embedding model {ModelId} rejected batched inference; using batch size 1.",
                    manifest.Id);
                vectors = await Task.Run(
                        () => batch
                            .Select(item => RunInferenceBatch(state.Session, manifest, new[] { item.Tokens }, cancellationToken)[0])
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                RecordLargestBatch(1);
            }

            for (var i = 0; i < batch.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = vectors[i];
                if (vector.Length != manifest.Dimension || vector.Any(value => !float.IsFinite(value)) || !vector.Any(value => value != 0))
                    throw new InvalidDataException("Embedding output must match the model dimension and contain finite, nonzero values.");
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

    // Called only while the operation gate is held: selection changes and disposal cannot race an active run.
    private async Task<EmbedderState> GetStateAsync(CancellationToken cancellationToken)
    {
        var pack = await _modelPacks.GetSelectedPackAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(_modelPacks.SelectedModelPackId)
                ? UnavailableTextEmbedder.Message
                : $"Selected model '{_modelPacks.SelectedModelPackId}' is not installed. Use Install model first.");
        if (!pack.IsUsable)
            throw new InvalidOperationException(pack.Status);
        if (_pack?.DirectoryPath == pack.DirectoryPath && _pack.Manifest.Fingerprint == pack.Manifest.Fingerprint &&
            _tokenizer is not null && _session is not null)
            return new EmbedderState(_pack, _tokenizer, _session);

        _session?.Dispose();
        _tokenizer?.Dispose();
        _session = null;
        _tokenizer = null;
        _pack = null;
        _batchingDisabled = 0;
        _largestInferenceBatchSize = 0;
        IEmbeddingTokenizer? tokenizer = null;
        InferenceSession? session = null;
        try
        {
            await EmbeddingModelPackStore.VerifyArtifactsAsync(pack, cancellationToken).ConfigureAwait(false);
            tokenizer = await EmbeddingTokenizer.LoadAsync(pack, cancellationToken).ConfigureAwait(false);
            using var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            session = await Task.Run(() => new InferenceSession(pack.ModelPath, options), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(pack.Manifest.OutputName) && !session.OutputMetadata.ContainsKey(pack.Manifest.OutputName))
                throw new InvalidDataException($"Model output '{pack.Manifest.OutputName}' is missing.");
            _pack = pack;
            _tokenizer = tokenizer;
            _session = session;
            return new EmbedderState(pack, tokenizer, session);
        }
        catch (Exception ex)
        {
            tokenizer?.Dispose();
            session?.Dispose();
            if (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not initialize embedding model pack {ModelId}.", pack.Manifest.Id);
            }
            throw;
        }
    }

    public async Task<EmbeddingModelInfo?> GetModelInfoAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (await GetStateAsync(cancellationToken).ConfigureAwait(false)).Pack.Manifest.ToModelInfo();
        }
        finally { _initializationGate.Release(); }
    }

    public async Task<IReadOnlyList<string>> SplitDocumentAsync(string text, CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            return SplitDocument(text, state.Tokenizer, state.Pack.Manifest, cancellationToken);
        }
        finally { _initializationGate.Release(); }
    }

    internal static IReadOnlyList<string> SplitDocument(
        string text, IEmbeddingTokenizer tokenizer, EmbeddingModelPackManifest manifest, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var start = 0;
        // A character boundary is retained instead of decoding token IDs, so added tokens and all source text survive.
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(text.Length - start, manifest.MaxTokens * 4);
            if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1])) count--;
            while (tokenizer.Encode(string.Concat(manifest.DocumentPrefix.AsSpan(), text.AsSpan(start, count)), manifest.MaxTokens + 1).InputIds.Length > manifest.MaxTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count /= 2;
                if (count > 0 && start + count < text.Length && char.IsHighSurrogate(text[start + count - 1])) count--;
                if (count <= 0) throw new InvalidDataException("Document prefix leaves no token budget for content.");
            }
            parts.Add(text.Substring(start, count));
            start += count;
        }
        return parts.Count == 0 ? new[] { text } : parts;
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
        TokenizedText[] tokenized,
        CancellationToken cancellationToken)
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

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        cancellationToken.ThrowIfCancellationRequested();
        var outputNames = string.IsNullOrWhiteSpace(manifest.OutputName) ? session.OutputNames : new[] { manifest.OutputName };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try { results = session.Run(inputs, outputNames, runOptions); }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        using var ownedResults = results;
        cancellationToken.ThrowIfCancellationRequested();
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

        if (!string.IsNullOrWhiteSpace(outputName))
            throw new InvalidDataException($"Model output '{outputName}' is missing.");
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

        if (pooling == EmbeddingModelPooling.SentenceEmbedding)
            throw new InvalidDataException("The sentence_embedding output must have rank 2.");
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
        IEmbeddingTokenizer Tokenizer,
        InferenceSession Session);

    private sealed record IndexedTokenizedText(int Index, TokenizedText Tokens);
}

internal readonly record struct EmbeddingBatchRange(int Start, int Count, int PaddedTokenCount);
