using Tokenizers.DotNet;

namespace FileSearch.Core.Engine;

internal interface IEmbeddingTokenizer : IDisposable
{
    TokenizedText Encode(string text, int maxTokens);
}

internal static class EmbeddingTokenizer
{
    public static async Task<IEmbeddingTokenizer> LoadAsync(
        InstalledEmbeddingModelPack pack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return pack.Manifest.TokenizerKind switch
        {
            "bert-wordpiece" => await BertWordPieceTokenizer.LoadAsync(
                pack.VocabularyPath, pack.Manifest.DoLowerCase, cancellationToken).ConfigureAwait(false),
            "huggingface-gemma" => new GemmaEmbeddingTokenizer(pack.VocabularyPath),
            _ => throw new InvalidOperationException($"Unsupported embedding tokenizer '{pack.Manifest.TokenizerKind}'."),
        };
    }
}

/// <summary>Uses the pinned Hugging Face JSON, including normalization, BPE, byte fallback and added tokens.</summary>
internal sealed class GemmaEmbeddingTokenizer : IEmbeddingTokenizer
{
    private readonly Tokenizer _tokenizer;

    public GemmaEmbeddingTokenizer(string tokenizerPath) => _tokenizer = new Tokenizer(tokenizerPath);

    public TokenizedText Encode(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 3);
        var encoded = _tokenizer.Encode(text ?? string.Empty);
        if (encoded.Length < 2 || encoded[0] != 2 || encoded[^1] != 1)
            throw new InvalidDataException("EmbeddingGemma tokenizer must add BOS (2) and EOS (1).");

        var ids = encoded.Take(maxTokens).Select(id => (long)id).ToArray();
        // Match HF right truncation of the content while retaining the post-processor EOS.
        ids[^1] = 1;
        return new TokenizedText(ids, Enumerable.Repeat(1L, ids.Length).ToArray(), new long[ids.Length]);
    }

    public void Dispose() => _tokenizer.Dispose();
}
