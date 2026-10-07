using System.Diagnostics;
using System.Text.Json;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Tests;

/// <summary>Opt-in real CPU tests. No downloads or user settings/index access.</summary>
public sealed class EmbeddingGemmaIntegrationTests
{
    public static bool ModelsAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FILESEARCH_EMBEDDING_TEST_MODELS"));
    private static EmbeddingModelPackStore CreateStore() => new(new EmbeddingModelPackOptions
    {
        SelectedModelPackId = "embeddinggemma-300m-q4-onnx",
        ModelPacksDirectory = Environment.GetEnvironmentVariable("FILESEARCH_EMBEDDING_TEST_MODELS") ?? throw new InvalidOperationException(),
    });

    [Fact(Skip = "Set FILESEARCH_EMBEDDING_TEST_MODELS to an explicitly installed scratch pack directory.", SkipUnless = nameof(ModelsAvailable))]
    public async Task FullPinnedTokenizerMatchesReferenceIncludingTruncationAndUnicode()
    {
        var pack = await CreateStore().GetSelectedPackAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(pack);
        Assert.True(pack.IsUsable, pack.Status);
        using var tokenizer = await EmbeddingTokenizer.LoadAsync(pack, TestContext.Current.CancellationToken);
        using var reference = ReadReference();
        foreach (var fixture in reference.RootElement.GetProperty("TokenCases").EnumerateArray())
        {
            var encoded = tokenizer.Encode(fixture.GetProperty("Text").GetString()!, fixture.GetProperty("MaxTokens").GetInt32());
            Assert.Equal(fixture.GetProperty("InputIds").EnumerateArray().Select(id => id.GetInt64()), encoded.InputIds);
            Assert.All(encoded.AttentionMask, value => Assert.Equal(1, value));
        }
    }

    [Fact(Skip = "Set FILESEARCH_EMBEDDING_TEST_MODELS to an explicitly installed scratch pack directory.", SkipUnless = nameof(ModelsAvailable))]
    public async Task RealCpuEmbeddingsMatchReferenceAndMixedBatches()
    {
        using var embedder = new OnnxTextEmbedder(CreateStore());
        using var reference = ReadReference();
        foreach (var fixture in reference.RootElement.GetProperty("EmbeddingCases").EnumerateArray())
        {
            var kind = Enum.Parse<TextEmbeddingInputKind>(fixture.GetProperty("Kind").GetString()!);
            var result = await embedder.EmbedAsync(fixture.GetProperty("Text").GetString()!, kind, TestContext.Current.CancellationToken);
            var expected = fixture.GetProperty("Vector").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            Assert.Equal(768, result.Vector.Length);
            var cosine = Cosine(result.Vector.ToArray(), expected);
            var error = result.Vector.ToArray().Zip(expected).Max(pair => Math.Abs(pair.First - pair.Second));
            Assert.True(cosine >= 0.9999, $"{kind}: cosine {cosine:R}, maximum absolute error {error:R}.");
            Assert.True(error <= 0.0002, $"{kind}: cosine {cosine:R}, maximum absolute error {error:R}.");
        }
        string[] texts = ["database recovery", "", "a much longer document about restoring committed database transactions using a write ahead log", "中文搜索文件"];
        var singles = new List<TextEmbedding>();
        foreach (var text in texts)
            singles.Add(await embedder.EmbedAsync(text, TestContext.Current.CancellationToken));
        var batch = await embedder.EmbedBatchAsync(texts, TextEmbeddingInputKind.Document, TestContext.Current.CancellationToken);
        for (var i = 0; i < texts.Length; i++)
            Assert.True(Cosine(singles[i].Vector.ToArray(), batch[i].Vector.ToArray()) >= 0.9999);
        Assert.True(embedder.LargestInferenceBatchSize > 1);
        Assert.False(embedder.UsedBatchCompatibilityFallback);
    }

    [Fact(Skip = "Set FILESEARCH_EMBEDDING_TEST_MODELS to an explicitly installed scratch pack directory.", SkipUnless = nameof(ModelsAvailable))]
    public async Task NativeCancellationReturnsPromptlyAndNextRunSucceeds()
    {
        using var embedder = new OnnxTextEmbedder(CreateStore());
        await embedder.EmbedAsync("warm up", TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        var watch = Stopwatch.StartNew();
        var longText = string.Concat(Enumerable.Repeat("database recovery ", 3000));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embedder.EmbedAsync(longText, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {watch.Elapsed}.");
        var next = await embedder.EmbedAsync("database recovery", TestContext.Current.CancellationToken);
        Assert.Equal(768, next.Vector.Length);
    }

    [Fact(Skip = "Set FILESEARCH_EMBEDDING_TEST_MODELS to an explicitly installed scratch pack directory.", SkipUnless = nameof(ModelsAvailable))]
    public async Task ScratchContentAndVectorIndexRetrievesAndRequiresRebuildAfterSwitch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "FileSearch.GemmaIndexTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "documents");
        Directory.CreateDirectory(root);
        try
        {
            var recoveryPath = Path.Combine(root, "recovery.txt");
            await File.WriteAllTextAsync(recoveryPath,
                "Committed database transactions can be recovered after a crash by replaying the write ahead log.", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "garden.txt"),
                "Tomatoes need sunlight, regular watering and well drained soil in a summer vegetable garden.", cancellationToken);
            var plain = new PlainTextExtractor();
            using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = Path.Combine(directory, "content.db") },
                new FileWalker(), new ExtractorRegistry(new ITextExtractor[] { plain }, plain));
            await index.BuildOrRefreshAsync(new IndexRequest(root, new WalkerOptions()), cancellationToken);
            var options = new EmbeddingModelPackOptions
            {
                SelectedModelPackId = "embeddinggemma-300m-q4-onnx",
                ModelPacksDirectory = Environment.GetEnvironmentVariable("FILESEARCH_EMBEDDING_TEST_MODELS")!,
            };
            var store = new EmbeddingModelPackStore(options);
            using var embedder = new OnnxTextEmbedder(store);
            using var vectors = new FileVectorIndex(new VectorIndexOptions { IndexPath = Path.Combine(directory, "vectors.json") });
            var status = new SemanticIndexStatusService(store, index, vectors);
            Assert.False((await status.GetRootStatusAsync(root, cancellationToken)).IsReady);
            var builder = new SemanticIndexBuilder(index, new ContentUnitChunker(), embedder, vectors);
            var coordinator = new SemanticIndexingCoordinator(index, builder, embedder, vectors);
            var build = await coordinator.UpsertRootAsync(root, cancellationToken);
            Assert.True(build.WasIndexed, build.Message);
            Assert.Equal(2, build.IndexedFileCount);
            Assert.True((await status.GetRootStatusAsync(root, cancellationToken)).IsReady);
            var provider = new SemanticCandidateProvider(embedder, vectors, index, status);
            var request = new SearchRequest(new QueryFactory().Build("recovering committed transactions after a crash", QueryMode.Semantic, false),
                new[] { root }, new WalkerOptions(), UseIndex: true, Mode: QueryMode.Semantic,
                SemanticOptions: new SemanticSearchOptions(0, 25));
            var plan = new QueryPlanner().CreatePlan(request);
            var candidates = new List<SearchCandidate>();
            await foreach (var candidate in provider.FindAsync(plan, cancellationToken)) candidates.Add(candidate);
            Assert.NotEmpty(candidates);
            Assert.Equal(recoveryPath, candidates[0].Path);
            Assert.True(candidates[0].ContentUnitId > 0);
            options.SelectedModelPackId = "bge-small-en-v1.5-onnx";
            var changed = await status.GetRootStatusAsync(root, cancellationToken);
            Assert.True(changed.IsModelAvailable);
            Assert.False(changed.IsReady);
            Assert.Equal(0, changed.VectorCount);
            Assert.Equal(384, (await embedder.EmbedAsync("database recovery", cancellationToken)).Vector.Length);
            await foreach (var candidate in provider.FindAsync(plan, cancellationToken))
                Assert.Fail($"Old-model vector was returned for {candidate.Path}.");
            options.SelectedModelPackId = "embeddinggemma-300m-q4-onnx";
            Assert.True((await status.GetRootStatusAsync(root, cancellationToken)).IsReady);
        }
        finally
        {
            // The shared content database retires its native handle asynchronously on Dispose.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(directory, recursive: true); break; }
                catch (IOException) when (attempt < 49) { await Task.Delay(20, CancellationToken.None); }
            }
        }
    }

    private static JsonDocument ReadReference() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Embeddings", "embeddinggemma-reference.json")));

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; }
        return dot / Math.Sqrt(a * b);
    }
}
