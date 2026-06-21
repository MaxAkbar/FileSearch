using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FileSearch.Core.Engine;

public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    private readonly IEmbeddingModelPackStore _modelPacks;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger _logger;
    private InstalledEmbeddingModelPack? _pack;
    private BertWordPieceTokenizer? _tokenizer;
    private InferenceSession? _session;
    private bool _initializationAttempted;

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
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        var manifest = state.Pack.Manifest;
        var prefix = inputKind == TextEmbeddingInputKind.Query
            ? manifest.QueryPrefix
            : manifest.DocumentPrefix;
        var tokenized = state.Tokenizer.Encode($"{prefix}{text}", manifest.MaxTokens);
        var vector = RunInference(state.Session, manifest, tokenized);
        if (manifest.Normalize)
            Normalize(vector);

        return new TextEmbedding(vector, manifest.ToModelInfo());
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

    private static float[] RunInference(
        InferenceSession session,
        EmbeddingModelPackManifest manifest,
        TokenizedText tokenized)
    {
        var length = tokenized.InputIds.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                manifest.InputIdsName,
                ToTensor(tokenized.InputIds, length)),
        };

        if (session.InputMetadata.ContainsKey(manifest.AttentionMaskName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                manifest.AttentionMaskName,
                ToTensor(tokenized.AttentionMask, length)));
        }

        if (!string.IsNullOrWhiteSpace(manifest.TokenTypeIdsName) &&
            session.InputMetadata.ContainsKey(manifest.TokenTypeIdsName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                manifest.TokenTypeIdsName,
                ToTensor(tokenized.TokenTypeIds, length)));
        }

        using var results = session.Run(inputs);
        var output = SelectOutput(results, manifest.OutputName);
        var tensor = output.AsTensor<float>();
        return Pool(tensor, tokenized.AttentionMask, manifest.Pooling);
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

    private static DenseTensor<long> ToTensor(long[] values, int length)
    {
        var tensor = new DenseTensor<long>(new[] { 1, length });
        for (var i = 0; i < length; i++)
            tensor[0, i] = values[i];
        return tensor;
    }

    private static float[] Pool(Tensor<float> tensor, long[] attentionMask, EmbeddingModelPooling pooling)
    {
        var dimensions = tensor.Dimensions.ToArray();
        var values = tensor.ToArray();
        if (dimensions.Length == 2)
            return values;

        if (dimensions.Length != 3)
            throw new InvalidOperationException("Embedding model output must have rank 2 or 3.");

        var tokenCount = dimensions[1];
        var dimension = dimensions[2];
        var vector = new float[dimension];
        if (pooling == EmbeddingModelPooling.Cls)
        {
            Array.Copy(values, 0, vector, 0, dimension);
            return vector;
        }

        var included = 0;
        for (var token = 0; token < tokenCount && token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
                continue;

            included++;
            var offset = token * dimension;
            for (var i = 0; i < dimension; i++)
                vector[i] += values[offset + i];
        }

        if (included == 0)
            return vector;

        for (var i = 0; i < vector.Length; i++)
            vector[i] /= included;
        return vector;
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
}
