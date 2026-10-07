namespace FileSearch.Core.Engine;

public sealed record TextEmbedding(
    ReadOnlyMemory<float> Vector,
    EmbeddingModelInfo Model);

public enum TextEmbeddingInputKind
{
    Document,
    Query,
}

public sealed record TextEmbedderAvailability(bool IsAvailable, string Message)
{
    public static TextEmbedderAvailability Available { get; } = new(true, string.Empty);

    public static TextEmbedderAvailability Unavailable(string message) =>
        new(false, string.IsNullOrWhiteSpace(message) ? "Text embeddings are unavailable." : message);
}

public interface ITextEmbedder
{
    Task<EmbeddingModelInfo?> GetModelInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EmbeddingModelInfo?>(null);

    Task<IReadOnlyList<string>> SplitDocumentAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { text });

    Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken);

    Task<TextEmbedding> EmbedAsync(
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken) =>
        EmbedAsync(text, cancellationToken);

    async Task<IReadOnlyList<TextEmbedding>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<TextEmbedding>();

        var embeddings = new TextEmbedding[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings[i] = await EmbedAsync(texts[i], inputKind, cancellationToken).ConfigureAwait(false);
        }

        return embeddings;
    }
}

public sealed class UnavailableTextEmbedder : ITextEmbedder
{
    public const string Message =
        "No local embedding model is configured. Install or configure a local semantic model pack to use semantic search.";

    public Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TextEmbedderAvailability.Unavailable(Message));
    }

    public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    public Task<TextEmbedding> EmbedAsync(
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    public Task<IReadOnlyList<TextEmbedding>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);
}
