using System.Text.Json;
using FileSearch.Core.Engine;
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
    public async Task SearchAsync_WithRoots_ExcludesDocumentsFromOtherRoots()
    {
        var index = new InMemoryVectorIndex();
        await index.UpsertAsync(
            new[]
            {
                CreateDocument("selected", new float[] { 0.9f, 0.1f, 0 }, 1, @"C:\selected"),
                CreateDocument("other", new float[] { 1, 0, 0 }, 2, @"C:\other"),
                CreateDocument("legacy-rootless", new float[] { 1, 0, 0 }, 3),
            },
            TestContext.Current.CancellationToken);

        var match = Assert.Single(await index.SearchAsync(
            new float[] { 1, 0, 0 },
            count: 10,
            TestContext.Current.CancellationToken,
            s_model,
            VectorDocumentKind.ContentChunk,
            fileIds: null,
            new[] { @"C:\selected\" }));

        Assert.Equal("selected", match.Id);
        Assert.Equal(@"C:\selected", match.Root);
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
    public async Task FileVectorIndex_PersistsAndFiltersDocumentRoots()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        try
        {
            var first = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await first.UpsertAsync(
                new[]
                {
                    CreateDocument("selected", new float[] { 1, 0, 0 }, 1, @"C:\selected"),
                    CreateDocument("other", new float[] { 1, 0, 0 }, 2, @"C:\other"),
                },
                TestContext.Current.CancellationToken);

            var second = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var match = Assert.Single(await second.SearchAsync(
                new float[] { 1, 0, 0 },
                count: 10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                fileIds: null,
                new[] { @"C:\selected" }));

            Assert.Equal("selected", match.Id);
            Assert.Equal(@"C:\selected", match.Root);
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
        var segmentDirectory = Path.Combine(directory, "vectors.segments");
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await index.UpsertAsync(
                new[] { CreateDocument("a", new float[] { 1, 0, 0 }, 1) },
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            Assert.True(Directory.Exists(segmentDirectory));

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(3, json.RootElement.GetProperty("FormatVersion").GetInt32());
            var segmentDescriptor = json.RootElement.GetProperty("Segments")[0];
            var metadataPath = Path.Combine(
                segmentDirectory,
                segmentDescriptor.GetProperty("MetadataFileName").GetString()!);
            var binaryPath = Path.Combine(
                segmentDirectory,
                segmentDescriptor.GetProperty("VectorFileName").GetString()!);
            Assert.True(File.Exists(metadataPath));
            Assert.True(File.Exists(binaryPath));

            using var segmentJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(metadataPath, TestContext.Current.CancellationToken));
            Assert.Equal(1, segmentJson.RootElement.GetProperty("Encoding").GetInt32());
            var record = segmentJson.RootElement.GetProperty("Documents")[0];
            Assert.False(record.TryGetProperty("Vector", out _));
            Assert.Equal(0, record.GetProperty("VectorOffset").GetInt64());
            Assert.Equal(3, record.GetProperty("VectorLength").GetInt32());
            Assert.True(record.GetProperty("VectorScale").GetSingle() > 0);
            Assert.True(record.GetProperty("VectorSquaredNorm").GetInt64() > 0);
            Assert.Equal(3, new FileInfo(binaryPath).Length);
            Assert.Equal(3, index.InMemoryVectorPayloadBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_FileReplacementWritesSmallOverlayAndPreservesOtherFiles()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        var root = @"C:\root";
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var first = CreateDocumentForFile(
                "first",
                new float[] { 1, 0, 0 },
                10,
                1,
                root,
                @"C:\root\first.txt");
            var second = CreateDocumentForFile(
                "second",
                new float[] { 0, 1, 0 },
                20,
                2,
                root,
                @"C:\root\second.txt");
            await index.ReplaceRootAsync(
                root,
                new long[] { 10, 20 },
                new[] { first, second },
                TestContext.Current.CancellationToken);

            var replacement = CreateDocumentForFile(
                "replacement",
                new float[] { 0, 0, 1 },
                11,
                3,
                root,
                @"C:\root\first.txt");
            await index.ReplaceFileAsync(root, 11, new[] { replacement }, TestContext.Current.CancellationToken);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(2, manifest.RootElement.GetProperty("Segments").GetArrayLength());
            var fileSegment = manifest.RootElement.GetProperty("Segments")[1];
            Assert.Equal(2, fileSegment.GetProperty("Scope").GetInt32());
            Assert.Equal(@"C:\root\first.txt", fileSegment.GetProperty("FilePath").GetString());
            var segmentDirectory = Path.Combine(directory, "vectors.segments");
            var overlayBinaryPath = Path.Combine(
                segmentDirectory,
                fileSegment.GetProperty("VectorFileName").GetString()!);
            Assert.Equal(3, new FileInfo(overlayBinaryPath).Length);

            var reloaded = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var firstFileMatch = Assert.Single(await reloaded.SearchAsync(
                new float[] { 0, 0, 1 },
                10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                new long[] { 11 },
                new[] { root }));
            var secondFileMatch = Assert.Single(await reloaded.SearchAsync(
                new float[] { 0, 1, 0 },
                10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                new long[] { 20 },
                new[] { root }));

            Assert.Equal("replacement", firstFileMatch.Id);
            Assert.Equal("second", secondFileMatch.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_EmptyReplacementUsesPathToRemovePreviousFileGeneration()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        const string root = @"C:\root";
        const string filePath = @"C:\root\empty.txt";
        try
        {
            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var oldDocument = CreateDocumentForFile(
                "old",
                new float[] { 1, 0, 0 },
                10,
                1,
                root,
                filePath);
            await index.ReplaceRootAsync(
                root,
                new long[] { 10 },
                new[] { oldDocument },
                TestContext.Current.CancellationToken);

            await index.ReplaceFileAsync(
                root,
                fileId: 11,
                filePath,
                Array.Empty<VectorDocument>(),
                TestContext.Current.CancellationToken);

            var reloaded = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var matches = await reloaded.SearchAsync(
                new float[] { 1, 0, 0 },
                10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                roots: new[] { root });
            Assert.Empty(matches);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorIndex_Int8ExactSearchPreservesFloatRankingQuality()
    {
        const int dimension = 64;
        const int documentCount = 500;
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        var model = new EmbeddingModelInfo("quality-gate", "1", dimension);
        var random = new Random(0x51A7);
        try
        {
            var documents = Enumerable.Range(1, documentCount)
                .Select(id => new VectorDocument(
                    $"doc-{id:0000}",
                    VectorDocumentKind.ContentChunk,
                    id,
                    new long[] { id },
                    CreateRandomUnitVector(random, dimension),
                    model,
                    ContentUnitChunker.ChunkerVersion,
                    $"checksum-{id}",
                    root: @"C:\quality"))
                .ToArray();
            var floatIndex = new InMemoryVectorIndex();
            await floatIndex.UpsertAsync(documents, TestContext.Current.CancellationToken);
            var quantizedIndex = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            await quantizedIndex.ReplaceRootAsync(
                @"C:\quality",
                documents.Select(document => document.FileId).ToArray(),
                documents,
                TestContext.Current.CancellationToken);

            for (var queryIndex = 0; queryIndex < 10; queryIndex++)
            {
                var query = CreateRandomUnitVector(random, dimension);
                var expected = await floatIndex.SearchAsync(
                    query,
                    10,
                    TestContext.Current.CancellationToken,
                    model,
                    VectorDocumentKind.ContentChunk,
                    roots: new[] { @"C:\quality" });
                var actual = await quantizedIndex.SearchAsync(
                    query,
                    10,
                    TestContext.Current.CancellationToken,
                    model,
                    VectorDocumentKind.ContentChunk,
                    roots: new[] { @"C:\quality" });

                var overlap = actual.Select(match => match.Id).Intersect(expected.Select(match => match.Id)).Count();
                Assert.True(overlap >= 9, $"Top-10 overlap was {overlap}/10 for query {queryIndex}.");
                var expectedScores = expected.ToDictionary(match => match.Id, match => match.Score);
                Assert.All(actual.Where(match => expectedScores.ContainsKey(match.Id)), match =>
                    Assert.InRange(Math.Abs(match.Score - expectedScores[match.Id]), 0, 0.01f));
            }

            Assert.Equal((long)documentCount * dimension, quantizedIndex.InMemoryVectorPayloadBytes);
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
    public async Task FileVectorIndex_MigratesVersion2StoreOnFirstScopedMutation()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "vectors.json");
        var binaryPath = Path.ChangeExtension(path, ".bin");
        try
        {
            var vectors = new float[] { 1, 0, 0, 0, 0, 1 };
            var bytes = new byte[vectors.Length * sizeof(float)];
            Buffer.BlockCopy(vectors, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(binaryPath, bytes, TestContext.Current.CancellationToken);
            var version2 = new
            {
                FormatVersion = 2,
                VectorFileName = Path.GetFileName(binaryPath),
                Documents = new[]
                {
                    new
                    {
                        Id = "old",
                        Kind = VectorDocumentKind.ContentChunk,
                        FileId = 10L,
                        ContentUnitIds = new long[] { 1 },
                        Model = s_model,
                        ChunkerVersion = ContentUnitChunker.ChunkerVersion,
                        ContentChecksum = "old-checksum",
                        Locator = (SourceLocator?)null,
                        VectorOffset = 0L,
                        VectorLength = 3,
                        Root = @"C:\root",
                    },
                    new
                    {
                        Id = "legacy-rootless",
                        Kind = VectorDocumentKind.ContentChunk,
                        FileId = 99L,
                        ContentUnitIds = new long[] { 99 },
                        Model = s_model,
                        ChunkerVersion = ContentUnitChunker.ChunkerVersion,
                        ContentChecksum = "legacy-checksum",
                        Locator = (SourceLocator?)null,
                        VectorOffset = 3L * sizeof(float),
                        VectorLength = 3,
                        Root = string.Empty,
                    },
                },
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(version2),
                TestContext.Current.CancellationToken);

            var index = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var replacement = CreateDocumentForFile(
                "replacement",
                new float[] { 0, 1, 0 },
                10,
                2,
                @"C:\root");
            await index.ReplaceFileAsync(
                @"C:\root",
                10,
                new[] { replacement },
                TestContext.Current.CancellationToken);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(3, manifest.RootElement.GetProperty("FormatVersion").GetInt32());
            Assert.Equal(2, manifest.RootElement.GetProperty("Segments").GetArrayLength());
            var reloaded = new FileVectorIndex(new VectorIndexOptions { IndexPath = path });
            var match = Assert.Single(await reloaded.SearchAsync(
                new float[] { 0, 1, 0 },
                10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk,
                roots: new[] { @"C:\root" }));
            Assert.Equal("replacement", match.Id);
            Assert.Empty(await reloaded.SearchAsync(
                new float[] { 0, 0, 1 },
                10,
                TestContext.Current.CancellationToken,
                s_model,
                VectorDocumentKind.ContentChunk));
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
                UseApproximateSearch = true,
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
                UseApproximateSearch = true,
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

    private static VectorDocument CreateDocument(
        string id,
        float[] vector,
        long contentUnitId,
        string root = "") =>
        new(
            id,
            VectorDocumentKind.ContentChunk,
            fileId: 10,
            new[] { contentUnitId },
            vector,
            s_model,
            ContentUnitChunker.ChunkerVersion,
            $"checksum-{contentUnitId}",
            new SourceLocator(StartLine: (int)contentUnitId, EndLine: (int)contentUnitId),
            root);

    private static VectorDocument CreateDocumentForFile(
        string id,
        float[] vector,
        long fileId,
        long contentUnitId,
        string root,
        string filePath = "") =>
        new(
            id,
            VectorDocumentKind.ContentChunk,
            fileId,
            new[] { contentUnitId },
            vector,
            s_model,
            ContentUnitChunker.ChunkerVersion,
            $"checksum-{contentUnitId}",
            new SourceLocator(StartLine: (int)contentUnitId, EndLine: (int)contentUnitId),
            root,
            filePath);

    private static float[] CreateDistributedVector(int seed)
    {
        var x = (float)Math.Sin(seed * 12.9898);
        var y = (float)Math.Sin(seed * 78.233);
        var z = (float)Math.Sin(seed * 37.719);
        if (Math.Abs(x) > 0.8f && y <= 0)
            y = 1;
        return new[] { x, y, z };
    }

    private static float[] CreateRandomUnitVector(Random random, int dimension)
    {
        var vector = new float[dimension];
        double squaredNorm = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (random.NextSingle() * 2) - 1;
            squaredNorm += vector[i] * vector[i];
        }

        var norm = Math.Sqrt(squaredNorm);
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-vector-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
