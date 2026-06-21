using FileSearch.Core.Engine;
using System.Text.Json;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class VectorIndexTests
{
    private static readonly EmbeddingModelInfo s_model = new("test-embedding", "1", 3);

    [Fact]
    public async Task SearchAsync_ReturnsCosineRankedMatches()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                CreateDocument("b", new float[] { 0, 1, 0 }, 2),
                CreateDocument("a", new float[] { 1, 0, 0 }, 1),
            },
            TestContext.Current.CancellationToken);

        var matches = await index.SearchAsync(
            new float[] { 0.9f, 0.1f, 0 },
            count: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal("a", matches[0].Id);
        Assert.Equal("b", matches[1].Id);
        Assert.True(matches[0].Score > matches[1].Score);
        Assert.Equal(new long[] { 1 }, matches[0].ContentUnitIds);
        Assert.Equal(s_model, matches[0].Model);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocumentsContainingContentUnitIds()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                CreateDocument("keep", new float[] { 1, 0, 0 }, 1),
                CreateDocument("remove", new float[] { 1, 0, 0 }, 2),
            },
            TestContext.Current.CancellationToken);

        await index.DeleteAsync(new long[] { 2 }, TestContext.Current.CancellationToken);

        var match = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken));
        Assert.Equal("keep", match.Id);
    }

    [Fact]
    public void VectorDocument_RejectsDimensionMismatch()
    {
        Assert.Throws<ArgumentException>(() => new VectorDocument(
            "bad",
            VectorDocumentKind.ContentChunk,
            fileId: 10,
            new long[] { 1 },
            new float[] { 1, 0 },
            s_model,
            ContentUnitChunker.ChunkerVersion,
            "checksum"));
    }

    [Fact]
    public async Task SearchAsync_IgnoresDocumentsWithDifferentDimensions()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                CreateDocument("three", new float[] { 1, 0, 0 }, 1),
                new VectorDocument(
                    "two",
                    VectorDocumentKind.ContentChunk,
                    fileId: 10,
                    new long[] { 2 },
                    new float[] { 1, 0 },
                    new EmbeddingModelInfo("test-embedding", "small", 2),
                    ContentUnitChunker.ChunkerVersion,
                    "checksum-2"),
            },
            TestContext.Current.CancellationToken);

        var match = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken));
        Assert.Equal("three", match.Id);
    }

    [Fact]
    public async Task SearchAsync_WithModelFilter_IgnoresOtherModelsWithSameDimension()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                CreateDocument("current", new float[] { 1, 0, 0 }, 1),
                new VectorDocument(
                    "stale",
                    VectorDocumentKind.ContentChunk,
                    fileId: 10,
                    new long[] { 2 },
                    new float[] { 1, 0, 0 },
                    new EmbeddingModelInfo("other-embedding", "1", 3),
                    ContentUnitChunker.ChunkerVersion,
                    "checksum-2"),
            },
            TestContext.Current.CancellationToken);

        var match = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken,
            s_model));

        Assert.Equal("current", match.Id);
    }

    [Fact]
    public async Task SearchAsync_CanFilterByKindAndFileIds()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                new VectorDocument(
                    "file-20",
                    VectorDocumentKind.File,
                    fileId: 20,
                    new long[] { 2 },
                    new float[] { 1, 0, 0 },
                    s_model,
                    ContentUnitChunker.ChunkerVersion,
                    "file-checksum"),
                CreateDocument("chunk-10", new float[] { 1, 0, 0 }, 1),
                new VectorDocument(
                    "chunk-20",
                    VectorDocumentKind.ContentChunk,
                    fileId: 20,
                    new long[] { 2 },
                    new float[] { 1, 0, 0 },
                    s_model,
                    ContentUnitChunker.ChunkerVersion,
                    "chunk-checksum"),
            },
            TestContext.Current.CancellationToken);

        var fileMatch = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken,
            s_model,
            VectorDocumentKind.File));
        var chunkMatch = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken,
            s_model,
            VectorDocumentKind.ContentChunk,
            new long[] { 20 }));

        Assert.Equal("file-20", fileMatch.Id);
        Assert.Equal("chunk-20", chunkMatch.Id);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyForZeroQuery()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[] { CreateDocument("a", new float[] { 1, 0, 0 }, 1) },
            TestContext.Current.CancellationToken);

        var matches = await index.SearchAsync(
            new float[] { 0, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task FileVectorIndex_PersistsDocumentsAcrossInstances()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var first = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await first.UpsertAsync(
                new[] { CreateDocument("a", new float[] { 1, 0, 0 }, 1) },
                TestContext.Current.CancellationToken);

            var second = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var match = Assert.Single(await second.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 10,
                TestContext.Current.CancellationToken));

            Assert.Equal("a", match.Id);
            Assert.Equal(s_model, match.Model);
            Assert.Equal(ContentUnitChunker.ChunkerVersion, match.ChunkerVersion);
            Assert.Equal("checksum-1", match.ContentChecksum);
            Assert.NotNull(match.Locator);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_WritesMetadataAndBinaryVectorStore()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        var binaryPath = Path.ChangeExtension(path, ".bin");
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await index.UpsertAsync(
                new[] { CreateDocument("a", new float[] { 1, 0, 0 }, 1) },
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(binaryPath));

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(2, json.RootElement.GetProperty("FormatVersion").GetInt32());
            var record = json.RootElement.GetProperty("Documents")[0];
            Assert.False(record.TryGetProperty("Vector", out _));
            Assert.Equal(0, record.GetProperty("VectorOffset").GetInt64());
            Assert.Equal(3, record.GetProperty("VectorLength").GetInt32());
            Assert.Equal(3 * sizeof(float), new FileInfo(binaryPath).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_ReadsLegacyJsonVectorStore()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var legacy = new
            {
                FormatVersion = 1,
                Documents = new[]
                {
                    new
                    {
                        Id = "legacy",
                        Kind = VectorDocumentKind.ContentChunk,
                        FileId = 10L,
                        ContentUnitIds = new long[] { 3 },
                        Vector = new float[] { 0, 1, 0 },
                        Model = s_model,
                        ChunkerVersion = ContentUnitChunker.ChunkerVersion,
                        ContentChecksum = "checksum-3",
                        Locator = new SourceLocator(StartLine: 3, EndLine: 3),
                    },
                },
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(legacy),
                TestContext.Current.CancellationToken);

            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var match = Assert.Single(await index.SearchAsync(
                new float[] { 0, 1, 0 },
                count: 10,
                TestContext.Current.CancellationToken,
                s_model));

            Assert.Equal("legacy", match.Id);
            Assert.Equal(new long[] { 3 }, match.ContentUnitIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_SearchAsync_FiltersByKindAndFileIds()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await index.UpsertAsync(
                new[]
                {
                    new VectorDocument(
                        "file-30",
                        VectorDocumentKind.File,
                        fileId: 30,
                        new long[] { 3 },
                        new float[] { 1, 0, 0 },
                        s_model,
                        ContentUnitChunker.ChunkerVersion,
                        "file-checksum"),
                    CreateDocument("chunk-10", new float[] { 1, 0, 0 }, 1),
                    new VectorDocument(
                        "chunk-30",
                        VectorDocumentKind.ContentChunk,
                        fileId: 30,
                        new long[] { 3 },
                        new float[] { 1, 0, 0 },
                        s_model,
                        ContentUnitChunker.ChunkerVersion,
                        "chunk-checksum"),
                },
                TestContext.Current.CancellationToken);

            var reloaded = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var fileMatch = Assert.Single(await reloaded.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.File));
            var chunkMatch = Assert.Single(await reloaded.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                new long[] { 30 }));

            Assert.Equal("file-30", fileMatch.Id);
            Assert.Equal("chunk-30", chunkMatch.Id);
            Assert.True(reloaded.LastSearchDiagnostics.UsedFileIdIndex);
            Assert.Equal("file-filter-exact", reloaded.LastSearchDiagnostics.Strategy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_SearchAsync_UsesApproximateIndexForLargePartitions()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions
            {
                IndexPath = path,
                ApproximateSearchMinimumDocuments = 32,
                ApproximateSearchTargetCandidates = 16,
            });
            var documents = new List<VectorDocument>
            {
                CreateDocument("best", new float[] { 1, 0, 0 }, 1),
            };
            for (var i = 2; i <= 512; i++)
                documents.Add(CreateDocument($"other-{i:000}", CreateDistributedVector(i), i));

            await index.UpsertAsync(documents, TestContext.Current.CancellationToken);

            var reloaded = new FileVectorIndex(new VectorIndexOptions
            {
                IndexPath = path,
                ApproximateSearchMinimumDocuments = 32,
                ApproximateSearchTargetCandidates = 16,
            });
            var match = Assert.Single(await reloaded.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 1,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk));

            Assert.Equal("best", match.Id);
            Assert.True(reloaded.LastSearchDiagnostics.UsedApproximateIndex);
            Assert.Equal("lsh", reloaded.LastSearchDiagnostics.Strategy);
            Assert.True(reloaded.LastSearchDiagnostics.CandidateDocuments < reloaded.LastSearchDiagnostics.TotalDocuments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_DeletePersistsAcrossInstances()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var first = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await first.UpsertAsync(
                new[]
                {
                    CreateDocument("keep", new float[] { 1, 0, 0 }, 1),
                    CreateDocument("remove", new float[] { 1, 0, 0 }, 2),
                },
                TestContext.Current.CancellationToken);

            await first.DeleteAsync(new long[] { 2 }, TestContext.Current.CancellationToken);

            var second = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var match = Assert.Single(await second.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 10,
                TestContext.Current.CancellationToken));

            Assert.Equal("keep", match.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VectorIndexOptions_DefaultPathFollowsDatabaseDirectory()
    {
        var original = Environment.GetEnvironmentVariable("FILESEARCH_VECTOR_INDEX_PATH");
        Environment.SetEnvironmentVariable("FILESEARCH_VECTOR_INDEX_PATH", null);
        try
        {
            var path = VectorIndexOptions.GetDefaultIndexPath(@"C:\indexes\filesearch.db");

            Assert.Equal(@"C:\indexes\filesearch.vectors.json", path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILESEARCH_VECTOR_INDEX_PATH", original);
        }
    }

    private static VectorDocument CreateDocument(string id, float[] vector, long contentUnitId) =>
        new(
            id,
            VectorDocumentKind.ContentChunk,
            fileId: 10,
            new[] { contentUnitId },
            vector,
            s_model,
            ContentUnitChunker.ChunkerVersion,
            $"checksum-{contentUnitId}",
            new SourceLocator(StartLine: (int)contentUnitId, EndLine: (int)contentUnitId));

    private static float[] CreateDistributedVector(int seed)
    {
        var x = (float)Math.Sin(seed * 12.9898);
        var y = (float)Math.Sin(seed * 78.233);
        var z = (float)Math.Sin(seed * 37.719);
        if (Math.Abs(x) > 0.8f && y <= 0)
            y = 1;
        return new[] { x, y, z };
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-vector-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
