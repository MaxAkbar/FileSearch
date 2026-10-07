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
                    "checksum",
                    root: @"C:\Docs"),
            },
            TestContext.Current.CancellationToken);
        var service = new SemanticIndexStatusService(
            new StubModelPackStore(new InstalledEmbeddingModelPack(manifest, @"C:\Models\test-model", true, "Installed.")),
            new StubContentUnitReader(new long[] { 7 }, new long[] { 10, 11, 12 }, new long[] { 10, 11 }),
            vectorIndex);

        var status = await service.GetRootStatusAsync(@"C:\Docs", TestContext.Current.CancellationToken);

        Assert.True(status.IsModelAvailable);
        Assert.True(status.IsReady);
        Assert.Equal("test-model", status.ModelId);
        Assert.Equal(1, status.VectorCount);
        Assert.Equal(2, status.CoveredContentUnitCount);
        Assert.Contains("Smart Search ready", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRootStatusAsync_RootlessLegacyVectors_DoNotReportReady()
    {
        var manifest = new EmbeddingModelPackManifest
        {
            Id = "test-model",
            DisplayName = "Test model",
            Version = "1",
            Dimension = 3,
        };
        var vectorIndex = new InMemoryVectorIndex();
        await vectorIndex.UpsertAsync(
            new[]
            {
                new VectorDocument(
                    "legacy-rootless",
                    VectorDocumentKind.ContentChunk,
                    fileId: 7,
                    new long[] { 10 },
                    new float[] { 1, 0, 0 },
                    manifest.ToModelInfo(),
                    ContentUnitChunker.ChunkerVersion,
                    "checksum"),
            },
            TestContext.Current.CancellationToken);
        var service = new SemanticIndexStatusService(
            new StubModelPackStore(new InstalledEmbeddingModelPack(manifest, @"C:\Models\test-model", true, "Installed.")),
            new StubContentUnitReader(new long[] { 7 }, new long[] { 10 }),
            vectorIndex);

        var status = await service.GetRootStatusAsync(@"C:\Docs", TestContext.Current.CancellationToken);

        Assert.False(status.IsReady);
        Assert.Equal(0, status.VectorCount);
        Assert.Contains("not built", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("variant")]
    [InlineData("prefix")]
    public async Task SameDimensionVectorsFromAnotherIdentityDoNotReportReady(string changedField)
    {
        var directory = Path.Combine(Path.GetTempPath(), "FileSearch.ModelSwitchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var previous = new EmbeddingModelPackManifest { FormatVersion = 2, Id = "document-model", Version = "1", Dimension = 3, QuantizationVersion = "q4" };
            using var vectors = new FileVectorIndex(new VectorIndexOptions { IndexPath = Path.Combine(directory, "vectors.json") });
            await vectors.UpsertAsync(new[] { new VectorDocument("chunk", VectorDocumentKind.ContentChunk, 7, new long[] { 10 },
                new float[] { 1, 0, 0 }, previous.ToModelInfo(), "1", "checksum", root: @"C:\Docs") }, TestContext.Current.CancellationToken);
            var selected = changedField switch
            {
                "id" => previous with { Id = "another-model" },
                "version" => previous with { Version = "2" },
                "variant" => previous with { QuantizationVersion = "q8" },
                _ => previous with { DocumentPrefix = "changed " },
            };
            var status = new SemanticIndexStatusService(new StubModelPackStore(new(selected, directory, true, "Installed")),
                new StubContentUnitReader(new long[] { 7 }, new long[] { 10 }), vectors);
            var root = await status.GetRootStatusAsync(@"C:\Docs", TestContext.Current.CancellationToken);
            Assert.False(root.IsReady);
            Assert.Equal(0, root.VectorCount);
            Assert.Equal(1, (await vectors.GetStatsAsync(new long[] { 10 }, TestContext.Current.CancellationToken)).DocumentCount);
        }
        finally { Directory.Delete(directory, recursive: true); }
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
        IReadOnlyList<long>? contentUnitIds = null,
        IReadOnlyList<long>? semanticContentUnitIds = null) : IContentUnitReader
    {
        public Task<IReadOnlyList<long>> GetFileIdsForRootAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(fileIds ?? Array.Empty<long>());

        public Task<IReadOnlyList<long>> GetContentUnitIdsForRootAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(contentUnitIds ?? Array.Empty<long>());

        public Task<IReadOnlyList<long>> GetSemanticContentUnitIdsForRootAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(semanticContentUnitIds ?? contentUnitIds ?? Array.Empty<long>());
    }
}
