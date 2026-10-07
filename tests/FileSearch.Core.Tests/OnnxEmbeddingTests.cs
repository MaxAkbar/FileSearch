using System.Text.Json;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Tests;

public sealed class OnnxEmbeddingTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Embeddings", name);

    [Theory]
    [InlineData(EmbeddingModelPooling.Cls, 2f)]
    [InlineData(EmbeddingModelPooling.Mean, 3.5f)]
    public async Task BertPoolingPreservesBgeAndMiniLmBehavior(EmbeddingModelPooling pooling, float first)
    {
        using var scratch = new TestPack(pooling);
        using var embedder = new OnnxTextEmbedder(scratch);
        var embedding = await embedder.EmbedAsync("hello world", TestContext.Current.CancellationToken);
        var norm = Math.Sqrt(first * first + 5);
        Assert.Equal(first / norm, embedding.Vector.Span[0], precision: 6);
        Assert.Equal(1 / norm, embedding.Vector.Span[1], precision: 6);
        Assert.Equal(2 / norm, embedding.Vector.Span[2], precision: 6);
    }

    [Fact]
    public async Task ExplicitSentenceOutputAndModelSwitchDoNotReuseOldSession()
    {
        using var scratch = new TestPack(EmbeddingModelPooling.Cls);
        using var embedder = new OnnxTextEmbedder(scratch);
        var first = await embedder.EmbedAsync("hello world", TestContext.Current.CancellationToken);
        scratch.Manifest = scratch.Manifest with { Version = "2", Pooling = EmbeddingModelPooling.SentenceEmbedding, OutputName = "sentence_embedding" };
        var second = await embedder.EmbedAsync("hello world", TestContext.Current.CancellationToken);
        Assert.Equal("2", second.Model.ModelVersion);
        Assert.NotEqual(first.Vector.ToArray(), second.Vector.ToArray());
        Assert.Equal(3.5 / Math.Sqrt(17.25), second.Vector.Span[0], precision: 6);
        scratch.Selected = false;
        Assert.False((await embedder.GetAvailabilityAsync(TestContext.Current.CancellationToken)).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync("hello", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("missing", EmbeddingModelPooling.SentenceEmbedding)]
    [InlineData("last_hidden_state", EmbeddingModelPooling.SentenceEmbedding)]
    public async Task ExplicitOutputNeverFallsBackToFirstOrMeanPooling(string output, EmbeddingModelPooling pooling)
    {
        using var scratch = new TestPack(pooling);
        scratch.Manifest = scratch.Manifest with { OutputName = output };
        using var embedder = new OnnxTextEmbedder(scratch);
        await Assert.ThrowsAsync<InvalidDataException>(() => embedder.EmbedAsync("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TokenBudgetSplitsWithoutDroppingTextOrPrefixes()
    {
        using var scratch = new TestPack(EmbeddingModelPooling.Cls);
        scratch.Manifest = scratch.Manifest with { MaxTokens = 8, DocumentPrefix = "hello " };
        using var tokenizer = await EmbeddingTokenizer.LoadAsync(await scratch.GetSelectedPackAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException(), TestContext.Current.CancellationToken);
        var text = string.Concat(Enumerable.Repeat("world 🦊 hello ", 80));
        var parts = OnnxTextEmbedder.SplitDocument(text, tokenizer, scratch.Manifest, TestContext.Current.CancellationToken);
        Assert.True(parts.Count > 1);
        Assert.Equal(text, string.Concat(parts));
        foreach (var part in parts)
        {
            Assert.True(tokenizer.Encode(scratch.Manifest.DocumentPrefix + part, 9).InputIds.Length <= 8);
            Assert.False(char.IsHighSurrogate(part[^1]));
        }
    }

    [Fact]
    public async Task CancelledInitializationCanRetryAndDisposedEmbedderCannotRun()
    {
        using var scratch = new TestPack(EmbeddingModelPooling.Cls);
        var embedder = new OnnxTextEmbedder(scratch);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embedder.EmbedAsync("hello", cancelled.Token));
        Assert.True((await embedder.GetAvailabilityAsync(TestContext.Current.CancellationToken)).IsAvailable);
        embedder.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => embedder.EmbedAsync("hello", TestContext.Current.CancellationToken));
        embedder.Dispose();
    }

    [Fact]
    public async Task SharedSettingsReflectModelSwitchWithoutWritingSettings()
    {
        using var scratch = new TestPack(EmbeddingModelPooling.Cls);
        var path = Path.Combine(scratch.ModelPacksDirectory, "settings.json");
        var json = JsonSerializer.Serialize(new { SemanticModelPackId = "a", SemanticModelPacksDirectory = scratch.ModelPacksDirectory, Manual = "keep" });
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        var options = EmbeddingModelSettings.Load(path);
        var store = new EmbeddingModelPackStore(options);
        Assert.Equal("a", store.SelectedModelPackId);
        Assert.Equal(json, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(path, "{\"SemanticModelPackId\":\"b\"}", TestContext.Current.CancellationToken);
        Assert.Equal("b", store.SelectedModelPackId);
        Assert.Equal("a", options.SelectedModelPackId);
    }

    private sealed class TestPack : IEmbeddingModelPackStore, IDisposable
    {
        public EmbeddingModelPackManifest Manifest { get; set; }
        public bool Selected { get; set; } = true;
        public string ModelPacksDirectory { get; } = Path.Combine(Path.GetTempPath(), "FileSearch.OnnxTests", Guid.NewGuid().ToString("N"));
        public TestPack(EmbeddingModelPooling pooling)
        {
            Directory.CreateDirectory(ModelPacksDirectory);
            File.Copy(Fixture("authored-embedding.onnx"), Path.Combine(ModelPacksDirectory, "model.onnx"));
            File.WriteAllText(Path.Combine(ModelPacksDirectory, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\nworld\n");
            Manifest = new EmbeddingModelPackManifest { Id = "test", Version = "1", Dimension = 3, ModelFile = "model.onnx", Pooling = pooling };
        }
        public void Dispose() => Directory.Delete(ModelPacksDirectory, recursive: true);
        public Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Selected ? new InstalledEmbeddingModelPack(Manifest, ModelPacksDirectory, true, "Installed") : null);
        public Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public string GetPackDirectory(string modelId) => ModelPacksDirectory;
    }
}
