using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class SemanticIndexingCoordinatorTests
{
    [Fact]
    public async Task UpsertFileAsync_DelegatesToSemanticIndexBuilder()
    {
        var builder = new RecordingSemanticIndexBuilder();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(),
            builder,
            new StubTextEmbedder(isAvailable: true),
            new RecordingVectorIndex());

        var result = await coordinator.UpsertFileAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(42, builder.UpsertedFileId);
        Assert.True(result.IsAvailable);
        Assert.Equal(42, result.FileId);
        Assert.Equal("delegated", result.Message);
    }

    [Fact]
    public async Task UpsertFileAsync_PathResolvesFileIdAndDelegatesToBuilder()
    {
        var builder = new RecordingSemanticIndexBuilder();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(
                new[] { new KeyValuePair<string, long>(@"C:\root|C:\root\a.txt", 42) },
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>(),
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>()),
            builder,
            new StubTextEmbedder(isAvailable: true),
            new RecordingVectorIndex());

        var result = await coordinator.UpsertFileAsync(
            @"C:\root",
            @"C:\root\a.txt",
            TestContext.Current.CancellationToken);

        Assert.Equal(42, builder.UpsertedFileId);
        Assert.Equal(42, result.FileId);
    }

    [Fact]
    public async Task UpsertRootAsync_UnavailableEmbedderSkipsPerFileBuilds()
    {
        var builder = new RecordingSemanticIndexBuilder();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(
                Array.Empty<KeyValuePair<string, long>>(),
                new[] { new KeyValuePair<string, IReadOnlyList<long>>(@"C:\root", new long[] { 42, 43 }) },
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>()),
            builder,
            new StubTextEmbedder(isAvailable: false),
            new RecordingVectorIndex());

        var result = await coordinator.UpsertRootAsync(@"C:\root", TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Equal(2, result.FileCount);
        Assert.Null(builder.UpsertedFileId);
    }

    [Fact]
    public async Task DeleteFileAsync_DeletesVectorsForExistingContentUnits()
    {
        var vectorIndex = new RecordingVectorIndex();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(
                CreateUnit(1, 42),
                CreateUnit(2, 42),
                CreateUnit(2, 42),
                CreateUnit(3, 99)),
            new RecordingSemanticIndexBuilder(),
            new StubTextEmbedder(isAvailable: true),
            vectorIndex);

        var result = await coordinator.DeleteFileAsync(42, TestContext.Current.CancellationToken);

        Assert.True(result.DeletedAny);
        Assert.Equal(42, result.FileId);
        Assert.Equal(2, result.ContentUnitCount);
        Assert.Equal(new long[] { 1, 2 }, result.ContentUnitIds);
        Assert.Equal(new long[] { 1, 2 }, vectorIndex.DeletedContentUnitIds);
    }

    [Fact]
    public async Task DeleteFileAsync_NoContentUnits_DoesNotCallVectorIndex()
    {
        var vectorIndex = new RecordingVectorIndex();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(CreateUnit(3, 99)),
            new RecordingSemanticIndexBuilder(),
            new StubTextEmbedder(isAvailable: true),
            vectorIndex);

        var result = await coordinator.DeleteFileAsync(42, TestContext.Current.CancellationToken);

        Assert.False(result.DeletedAny);
        Assert.Equal(42, result.FileId);
        Assert.Equal(0, result.ContentUnitCount);
        Assert.Empty(vectorIndex.DeletedContentUnitIds);
    }

    [Fact]
    public async Task DeleteContentUnitsAsync_FiltersInvalidAndDuplicateIds()
    {
        var vectorIndex = new RecordingVectorIndex();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(),
            new RecordingSemanticIndexBuilder(),
            new StubTextEmbedder(isAvailable: true),
            vectorIndex);

        var result = await coordinator.DeleteContentUnitsAsync(
            new long[] { 0, 5, -1, 5, 8 },
            TestContext.Current.CancellationToken);

        Assert.True(result.DeletedAny);
        Assert.Null(result.FileId);
        Assert.Equal(new long[] { 5, 8 }, result.ContentUnitIds);
        Assert.Equal(new long[] { 5, 8 }, vectorIndex.DeletedContentUnitIds);
    }

    [Fact]
    public async Task DeleteContentUnitsAsync_NoValidIds_DoesNotCallVectorIndex()
    {
        var vectorIndex = new RecordingVectorIndex();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(),
            new RecordingSemanticIndexBuilder(),
            new StubTextEmbedder(isAvailable: true),
            vectorIndex);

        var result = await coordinator.DeleteContentUnitsAsync(
            new long[] { 0, -1 },
            TestContext.Current.CancellationToken);

        Assert.False(result.DeletedAny);
        Assert.Equal(0, result.ContentUnitCount);
        Assert.Empty(vectorIndex.DeletedContentUnitIds);
    }

    [Fact]
    public async Task DeleteRootAsync_DeletesVectorsForRootContentUnits()
    {
        var vectorIndex = new RecordingVectorIndex();
        var coordinator = new SemanticIndexingCoordinator(
            new StubContentUnitReader(
                Array.Empty<KeyValuePair<string, long>>(),
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>(),
                new[] { new KeyValuePair<string, IReadOnlyList<long>>(@"C:\root", new long[] { 1, 2, 2 }) }),
            new RecordingSemanticIndexBuilder(),
            new StubTextEmbedder(isAvailable: true),
            vectorIndex);

        var result = await coordinator.DeleteRootAsync(@"C:\root", TestContext.Current.CancellationToken);

        Assert.True(result.DeletedAny);
        Assert.Equal(@"C:\root", result.Root);
        Assert.Equal(new long[] { 1, 2 }, result.ContentUnitIds);
        Assert.Equal(new long[] { 1, 2 }, vectorIndex.DeletedContentUnitIds);
    }

    private static ContentUnit CreateUnit(long id, long fileId) =>
        new(
            id,
            fileId,
            ContentUnitKind.Text,
            new SourceLocator(StartLine: (int)id, EndLine: (int)id),
            $"text-{id}",
            $"hash-{id}",
            "en",
            "plain",
            "1");

    private sealed class RecordingSemanticIndexBuilder : ISemanticIndexBuilder
    {
        public long? UpsertedFileId { get; private set; }

        public Task<SemanticIndexBuildResult> UpsertFileAsync(
            long fileId,
            CancellationToken cancellationToken)
        {
            UpsertedFileId = fileId;
            return Task.FromResult(SemanticIndexBuildResult.Completed(
                fileId,
                contentUnitCount: 1,
                chunkCount: 1,
                vectorCount: 1,
                model: null,
                "delegated"));
        }
    }

    private sealed class StubContentUnitReader : IContentUnitReader
    {
        private readonly IReadOnlyList<ContentUnit> _units;
        private readonly IReadOnlyDictionary<string, long> _fileIdsByPath;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<long>> _fileIdsByRoot;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<long>> _contentUnitIdsByRoot;

        public StubContentUnitReader(
            params ContentUnit[] units)
            : this(
                Array.Empty<KeyValuePair<string, long>>(),
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>(),
                Array.Empty<KeyValuePair<string, IReadOnlyList<long>>>(),
                units)
        {
        }

        public StubContentUnitReader(
            IReadOnlyCollection<KeyValuePair<string, long>> fileIdsByPath,
            IReadOnlyCollection<KeyValuePair<string, IReadOnlyList<long>>> fileIdsByRoot,
            IReadOnlyCollection<KeyValuePair<string, IReadOnlyList<long>>> contentUnitIdsByRoot,
            params ContentUnit[] units)
        {
            _units = units;
            _fileIdsByPath = fileIdsByPath.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            _fileIdsByRoot = fileIdsByRoot.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            _contentUnitIdsByRoot = contentUnitIdsByRoot.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<ContentUnit>> GetContentUnitsForFileAsync(
            long fileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContentUnit>>(
                _units.Where(unit => unit.FileId == fileId).ToArray());

        public Task<long?> GetFileIdAsync(
            string root,
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult<long?>(_fileIdsByPath.TryGetValue($"{root}|{path}", out var fileId) ? fileId : null);

        public Task<IReadOnlyList<long>> GetFileIdsForRootAsync(
            string root,
            CancellationToken cancellationToken) =>
            Task.FromResult(_fileIdsByRoot.TryGetValue(root, out var ids) ? ids : Array.Empty<long>());

        public Task<IReadOnlyList<long>> GetContentUnitIdsForRootAsync(
            string root,
            CancellationToken cancellationToken) =>
            Task.FromResult(_contentUnitIdsByRoot.TryGetValue(root, out var ids) ? ids : Array.Empty<long>());
    }

    private sealed class StubTextEmbedder(bool isAvailable) : ITextEmbedder
    {
        public Task<TextEmbedderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(isAvailable
                ? TextEmbedderAvailability.Available
                : TextEmbedderAvailability.Unavailable("unavailable"));

        public Task<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(new TextEmbedding(new float[] { 1, 0 }, new EmbeddingModelInfo("test", "1", 2)));
    }

    private sealed class RecordingVectorIndex : IVectorIndex
    {
        public List<long> DeletedContentUnitIds { get; } = new();

        public Task UpsertAsync(
            IReadOnlyCollection<VectorDocument> documents,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

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
