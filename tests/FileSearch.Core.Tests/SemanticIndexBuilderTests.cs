using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class SemanticIndexBuilderTests
{
    private static readonly EmbeddingModelInfo s_model = new("test-embedding", "1", 2);

    [Fact]
    public async Task UpsertFileAsync_SkipsWhenEmbedderUnavailable()
    {
        var vectorIndex = new RecordingVectorIndex();
        var builder = new SemanticIndexBuilder(
            new StubContentUnitReader(CreateUnit(1, 10, "alpha")),
            new ContentUnitChunker(),
            new UnavailableTextEmbedder(),
            vectorIndex);

        var result = await builder.UpsertFileAsync(10, TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.False(result.WasIndexed);
        Assert.Contains("No local embedding model", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vectorIndex.UpsertedDocuments);
        Assert.Empty(vectorIndex.DeletedContentUnitIds);
    }

    [Fact]
    public async Task UpsertFileAsync_ChunksEmbedsAndUpsertsVectors()
    {
        var vectorIndex = new RecordingVectorIndex();
        var embedder = new StubTextEmbedder(s_model, text => text.Contains("alpha", StringComparison.Ordinal) ? new float[] { 1, 0 } : new float[] { 0, 1 });
        var builder = new SemanticIndexBuilder(
            new StubContentUnitReader(
                CreateUnit(1, 10, new string('a', 900)),
                CreateUnit(2, 10, new string('b', 900)),
                CreateUnit(3, 10, new string('c', 900))),
            new ContentUnitChunker(),
            embedder,
            vectorIndex);

        var result = await builder.UpsertFileAsync(10, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.True(result.WasIndexed);
        Assert.Equal(3, result.ContentUnitCount);
        Assert.Equal(2, result.ChunkCount);
        Assert.Equal(3, result.VectorCount);
        Assert.Equal(s_model, result.Model);
        Assert.Equal(new long[] { 1, 2, 3 }, vectorIndex.DeletedContentUnitIds);
        Assert.Equal(3, vectorIndex.UpsertedDocuments.Count);
        var fileDocument = Assert.Single(vectorIndex.UpsertedDocuments, document => document.Kind == VectorDocumentKind.File);
        Assert.Equal(10, fileDocument.FileId);
        Assert.Equal(new long[] { 1, 2, 3 }, fileDocument.ContentUnitIds);
        Assert.Equal(s_model, fileDocument.Model);
        Assert.StartsWith("file:10:", fileDocument.Id, StringComparison.Ordinal);
        Assert.Equal(2, fileDocument.Vector.Count);
        Assert.Equal(2, vectorIndex.UpsertedDocuments.Count(document => document.Kind == VectorDocumentKind.ContentChunk));
        Assert.All(vectorIndex.UpsertedDocuments.Where(document => document.Kind == VectorDocumentKind.ContentChunk), document =>
        {
            Assert.Equal(10, document.FileId);
            Assert.Equal(s_model, document.Model);
            Assert.Equal(ContentUnitChunker.ChunkerVersion, document.ChunkerVersion);
            Assert.NotNull(document.Locator);
        });
        Assert.Equal(2, embedder.EmbeddedTexts.Count);
    }

    [Fact]
    public async Task UpsertFileAsync_DeletesCurrentUnitsEvenWhenChunksAreEmpty()
    {
        var vectorIndex = new RecordingVectorIndex();
        var builder = new SemanticIndexBuilder(
            new StubContentUnitReader(
                CreateUnit(1, 10, " "),
                CreateUnit(2, 10, string.Empty)),
            new ContentUnitChunker(),
            new StubTextEmbedder(s_model, _ => new float[] { 1, 0 }),
            vectorIndex);

        var result = await builder.UpsertFileAsync(10, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.False(result.WasIndexed);
        Assert.Equal(2, result.ContentUnitCount);
        Assert.Equal(0, result.ChunkCount);
        Assert.Equal(new long[] { 1, 2 }, vectorIndex.DeletedContentUnitIds);
        Assert.Empty(vectorIndex.UpsertedDocuments);
    }

    private static ContentUnit CreateUnit(long id, long fileId, string text) =>
        new(
            id,
            fileId,
            ContentUnitKind.Text,
            new SourceLocator(StartLine: (int)id, EndLine: (int)id),
            text,
            $"hash-{id}",
            "en",
            "plain",
            "1");

    private sealed class StubContentUnitReader : IContentUnitReader
    {
        private readonly IReadOnlyList<ContentUnit> _units;

        public StubContentUnitReader(params ContentUnit[] units) =>
            _units = units;

        public Task<IReadOnlyList<ContentUnit>> GetContentUnitsForFileAsync(
            long fileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContentUnit>>(
                _units.Where(unit => unit.FileId == fileId).ToArray());
    }

    private sealed class StubTextEmbedder : ITextEmbedder
    {
        private readonly EmbeddingModelInfo _model;
        private readonly Func<string, float[]> _embed;

        public StubTextEmbedder(EmbeddingModelInfo model, Func<string, float[]> embed)
        {
            _model = model;
            _embed = embed;
        }

        public List<string> EmbeddedTexts { get; } = new();

        public Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TextEmbedderAvailability.Available);

        public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbeddedTexts.Add(text);
            return Task.FromResult(new TextEmbedding(_embed(text), _model));
        }
    }

    private sealed class RecordingVectorIndex : IVectorIndex
    {
        public List<long> DeletedContentUnitIds { get; } = new();

        public List<VectorDocument> UpsertedDocuments { get; } = new();

        public Task UpsertAsync(
            IReadOnlyCollection<VectorDocument> documents,
            CancellationToken cancellationToken)
        {
            UpsertedDocuments.AddRange(documents);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            IReadOnlyCollection<long> contentUnitIds,
            CancellationToken cancellationToken)
        {
            DeletedContentUnitIds.AddRange(contentUnitIds);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorMatch>> SearchAsync(
            ReadOnlyMemory<float> queryVector,
            int count,
            CancellationToken cancellationToken,
            EmbeddingModelInfo? model = null,
            VectorDocumentKind? kind = null,
            IReadOnlyCollection<long>? fileIds = null) =>
            Task.FromResult<IReadOnlyList<VectorMatch>>(Array.Empty<VectorMatch>());
    }
}
