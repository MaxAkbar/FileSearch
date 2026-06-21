using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class SemanticIndexStatusServiceTests
{
    [Fact]
    public async Task GetRootStatusAsync_NoSelectedModel_ReturnsUnavailableStatus()
    {
        var service = new SemanticIndexStatusService(
            new StubModelPackStore(null),
            new StubContentUnitReader(),
            new InMemoryVectorIndex());

        var status = await service.GetRootStatusAsync(@"C:\Docs", TestContext.Current.CancellationToken);

        Assert.False(status.IsModelAvailable);
        Assert.False(status.IsReady);
        Assert.Contains("No local embedding model", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRootStatusAsync_WithMatchingVectors_ReturnsReadyStatus()
    {
        var manifest = new EmbeddingModelPackManifest
        {
            Id = "test-model",
            DisplayName = "Test model",
            Version = "1",
            Dimension = 3,
        };
        var model = manifest.ToModelInfo();
        var vectorIndex = new InMemoryVectorIndex();
        await vectorIndex.UpsertAsync(
            new[]
            {
                new VectorDocument(
                    "chunk-1",
                    VectorDocumentKind.ContentChunk,
                    fileId: 7,
                    new long[] { 10, 11 },
                    new float[] { 1, 0, 0 },
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    "checksum"),
            },
            TestContext.Current.CancellationToken);
        var service = new SemanticIndexStatusService(
            new StubModelPackStore(new InstalledEmbeddingModelPack(manifest, @"C:\Models\test-model", true, "Installed.")),
            new StubContentUnitReader(new long[] { 7 }, new long[] { 10, 11 }),
            vectorIndex);

        var status = await service.GetRootStatusAsync(@"C:\Docs", TestContext.Current.CancellationToken);

        Assert.True(status.IsModelAvailable);
        Assert.True(status.IsReady);
        Assert.Equal("test-model", status.ModelId);
        Assert.Equal(1, status.VectorCount);
        Assert.Equal(2, status.CoveredContentUnitCount);
        Assert.Contains("Smart Search ready", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubModelPackStore(InstalledEmbeddingModelPack? selected) : IEmbeddingModelPackStore
    {
        public string ModelPacksDirectory => @"C:\Models";

        public Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledEmbeddingModelPack>>(
                selected is null ? Array.Empty<InstalledEmbeddingModelPack>() : new[] { selected });

        public Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken) =>
            Task.FromResult(selected);

        public string GetPackDirectory(string modelId) => Path.Combine(ModelPacksDirectory, modelId);
    }

    private sealed class StubContentUnitReader(
        IReadOnlyList<long>? fileIds = null,
        IReadOnlyList<long>? contentUnitIds = null) : IContentUnitReader
    {
        public Task<IReadOnlyList<long>> GetFileIdsForRootAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(fileIds ?? Array.Empty<long>());

        public Task<IReadOnlyList<long>> GetContentUnitIdsForRootAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(contentUnitIds ?? Array.Empty<long>());
    }
}
