using System.Net;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Engine;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class EmbeddingModelPackTests
{
    [Fact]
    public void BundledLicenseResourcesMatchPinnedPackBytes()
    {
        var manifest = new EmbeddingModelPackCatalog().GetById("embeddinggemma-300m-q4-onnx")!.Manifest;
        foreach (var file in manifest.Files.Where(file => file.DownloadUrl.StartsWith("embedded:", StringComparison.Ordinal)))
        {
            var bytes = Encoding.UTF8.GetBytes(EmbeddingLicenseResources.GetText(file.DownloadUrl[9..]));
            Assert.Equal(file.SizeBytes, bytes.LongLength);
            Assert.Equal(file.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        string[] names = ["Gemma-NOTICE.txt", "Gemma-Terms-of-Use.txt", "Gemma-Prohibited-Use-Policy.txt",
            "Tokenizers-DotNet-MIT.txt", "HuggingFace-Tokenizers-Apache-2.0.txt", "Oniguruma-BSD.txt", "Mimalloc-MIT.txt"];
        foreach (var name in names)
            Assert.Equal(Encoding.UTF8.GetBytes(EmbeddingLicenseResources.GetText(name)),
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "docs", "licenses", name)));
    }

    [Fact]
    public void OnnxTextEmbedder_BatchRangesRespectPaddedTokenBudget()
    {
        var ranges = OnnxTextEmbedder.CreateBatchRanges(
            new[] { 50, 50, 200, 20, 20 },
            maximumBatchSize: 4,
            maximumPaddedTokens: 200);

        Assert.Equal(
            new[]
            {
                new EmbeddingBatchRange(0, 2, 50),
                new EmbeddingBatchRange(2, 1, 200),
                new EmbeddingBatchRange(3, 2, 20),
            },
            ranges);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    [Fact]
    public void GemmaCatalogPinsSupportedArtifactsAndFullCompatibilityIdentity()
    {
        var entry = new EmbeddingModelPackCatalog().GetById("embeddinggemma-300m-q4-onnx")!;
        var manifest = entry.Manifest;
        Assert.False(entry.IsRecommended);
        Assert.Equal(768, manifest.Dimension);
        Assert.Equal(2048, manifest.MaxTokens);
        Assert.Equal("sentence_embedding", manifest.OutputName);
        Assert.Equal(EmbeddingModelPooling.SentenceEmbedding, manifest.Pooling);
        Assert.Equal("huggingface-gemma", manifest.TokenizerKind);
        Assert.True(manifest.RequiresLicenseAcceptance);
        Assert.Equal("Gemma Terms of Use", manifest.License);
        Assert.All(manifest.Files, file =>
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.True(file.SizeBytes > 0);
            Assert.DoesNotContain("fp16", file.RelativePath);
            if (!file.DownloadUrl.StartsWith("embedded:", StringComparison.Ordinal))
                Assert.Contains(manifest.Version, file.DownloadUrl, StringComparison.Ordinal);
        });
        Assert.Contains(manifest.Files, file => file.RelativePath == "onnx/model_q4.onnx_data");
        Assert.Contains(manifest.Files, file => file.RelativePath == "NOTICE");
        var stamp = EmbeddingModelPackValidationStamp.FromResult(manifest, EmbeddingModelPackValidationResult.Passed("validated"));
        foreach (var changed in new[]
        {
            manifest with { QueryPrefix = "changed " }, manifest with { DocumentPrefix = "changed " },
            manifest with { OutputName = "last_hidden_state" }, manifest with { QuantizationVersion = "q8" },
            manifest with { TokenizerKind = "bert-wordpiece" }, manifest with { Dimension = 512 },
        })
        {
            Assert.False(stamp.Matches(changed));
            Assert.NotEqual(manifest.ToModelInfo(), changed.ToModelInfo());
        }
    }

    [Fact]
    public async Task FailedReinstallPreservesPreviouslyValidatedPack()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("atomic-test");
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions { ModelPacksDirectory = directory, SelectedModelPackId = manifest.Id });
            using var http = new HttpClient(new StaticHttpMessageHandler("file contents"));
            var validator = new StubValidator(EmbeddingModelPackValidationResult.Passed("validated"));
            using var first = new EmbeddingModelPackInstaller(new StubCatalog(new EmbeddingModelPackCatalogEntry(manifest, false, "1 KB")), store, http, validator);
            await first.InstallAsync(manifest.Id, null, TestContext.Current.CancellationToken);
            var changed = manifest with { Files = manifest.Files.Select(file => file with { Sha256 = new string('0', 64) }).ToArray() };
            using var second = new EmbeddingModelPackInstaller(new StubCatalog(new EmbeddingModelPackCatalogEntry(changed, false, "1 KB")), store, http, validator);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.InstallAsync(manifest.Id, null, TestContext.Current.CancellationToken));
            var installed = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(installed);
            Assert.True(installed.IsUsable);
            Assert.Equal("file contents", await File.ReadAllTextAsync(installed.ModelPath, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task GemmaLicenseMustBeAcceptedBeforeAnyDownload()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var http = new HttpClient(new ThrowingHttpMessageHandler(new Exception("Must not download")));
            using var installer = new EmbeddingModelPackInstaller(new EmbeddingModelPackCatalog(),
                new EmbeddingModelPackStore(new EmbeddingModelPackOptions { ModelPacksDirectory = directory }), http);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync("embeddinggemma-300m-q4-onnx", null, TestContext.Current.CancellationToken));
            Assert.Contains("Review and accept", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingExternalWeightsAndUnvalidatedVersionTwoPackAreUnavailable()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("artifact-test") with { FormatVersion = 2 };
            var packDirectory = Path.Combine(directory, manifest.Id);
            Directory.CreateDirectory(Path.Combine(packDirectory, "onnx"));
            await File.WriteAllTextAsync(Path.Combine(packDirectory, manifest.ModelFile), "model", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(packDirectory, manifest.VocabularyFile), "vocab", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(packDirectory, EmbeddingModelPackManifest.FileName), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions { ModelPacksDirectory = directory, SelectedModelPackId = manifest.Id });
            var selected = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(selected);
            Assert.False(selected.IsUsable);
            Assert.Contains("Smoke validation has not run", selected.Status, StringComparison.Ordinal);
            manifest = manifest with { Files = [.. manifest.Files, new("onnx/model.onnx_data", "https://example.invalid/weights")] };
            await File.WriteAllTextAsync(Path.Combine(packDirectory, EmbeddingModelPackManifest.FileName), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            selected = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(selected);
            Assert.False(selected.IsUsable);
            Assert.Contains("model.onnx_data", selected.Status, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Catalog_ExposesRecommendedAndLightweightModelPacks()
    {
        var catalog = new EmbeddingModelPackCatalog();

        var recommended = Assert.Single(catalog.Entries, entry => entry.IsRecommended);
        Assert.Equal("bge-small-en-v1.5-onnx", recommended.Id);
        Assert.Contains(catalog.Entries, entry => entry.Id == "all-minilm-l6-v2-onnx");
    }

    [Fact]
    public async Task Store_ReturnsSelectedInstalledPack()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("test-model");
            var packDirectory = Path.Combine(directory, manifest.Id);
            Directory.CreateDirectory(Path.Combine(packDirectory, "onnx"));
            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, EmbeddingModelPackManifest.FileName),
                JsonSerializer.Serialize(manifest, s_jsonOptions),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, manifest.ModelFile),
                "model",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, manifest.VocabularyFile),
                "[PAD]\n[UNK]\n[CLS]\n[SEP]\n",
                TestContext.Current.CancellationToken);

            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions
            {
                ModelPacksDirectory = directory,
                SelectedModelPackId = manifest.Id,
            });

            var selected = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(selected);
            Assert.True(selected.IsUsable);
            Assert.Contains("Smoke validation has not run", selected.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(manifest.Id, selected.Manifest.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OnnxTextEmbedder_ReportsUnavailableUntilPackIsSelected()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions
            {
                ModelPacksDirectory = directory,
            });
            var embedder = new OnnxTextEmbedder(store);

            var availability = await embedder.GetAvailabilityAsync(TestContext.Current.CancellationToken);

            Assert.False(availability.IsAvailable);
            Assert.Contains("No local embedding model", availability.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Installer_DownloadsFilesAndWritesManifest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("download-test");
            var catalog = new StubCatalog(new EmbeddingModelPackCatalogEntry(manifest, IsRecommended: true, "1 KB"));
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions
            {
                ModelPacksDirectory = directory,
                SelectedModelPackId = manifest.Id,
            });
            using var httpClient = new HttpClient(new StaticHttpMessageHandler("file contents"));
            var validator = new StubValidator(EmbeddingModelPackValidationResult.Passed("Smoke validation passed."));
            var installer = new EmbeddingModelPackInstaller(catalog, store, httpClient, validator);

            var installed = await installer.InstallAsync(
                manifest.Id,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(installed.IsUsable);
            Assert.Equal("Smoke validation passed.", installed.Status);
            Assert.True(File.Exists(Path.Combine(installed.DirectoryPath, EmbeddingModelPackManifest.FileName)));
            Assert.True(File.Exists(Path.Combine(installed.DirectoryPath, EmbeddingModelPackValidationStamp.FileName)));
            Assert.Equal("file contents", await File.ReadAllTextAsync(installed.ModelPath, TestContext.Current.CancellationToken));
            Assert.Equal("file contents", await File.ReadAllTextAsync(installed.VocabularyPath, TestContext.Current.CancellationToken));

            var selected = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(selected);
            Assert.True(selected.IsUsable);
            Assert.Equal("Smoke validation passed.", selected.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Installer_RecordsFailedSmokeValidation()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("download-failure-test");
            var catalog = new StubCatalog(new EmbeddingModelPackCatalogEntry(manifest, IsRecommended: true, "1 KB"));
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions
            {
                ModelPacksDirectory = directory,
                SelectedModelPackId = manifest.Id,
            });
            using var httpClient = new HttpClient(new StaticHttpMessageHandler("file contents"));
            var installer = new EmbeddingModelPackInstaller(
                catalog,
                store,
                httpClient,
                new StubValidator(EmbeddingModelPackValidationResult.Failed("Smoke validation failed: bad model.")));

            var installed = await installer.InstallAsync(
                manifest.Id,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.False(installed.IsUsable);
            Assert.Equal("Smoke validation failed: bad model.", installed.Status);

            var selected = await store.GetSelectedPackAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(selected);
            Assert.False(selected.IsUsable);
            Assert.Equal("Smoke validation failed: bad model.", selected.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Installer_ReportsHostAndUnderlyingHttpsError()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("https-failure-test");
            var catalog = new StubCatalog(new EmbeddingModelPackCatalogEntry(manifest, IsRecommended: true, "1 KB"));
            var store = new EmbeddingModelPackStore(new EmbeddingModelPackOptions
            {
                ModelPacksDirectory = directory,
                SelectedModelPackId = manifest.Id,
            });
            var transportException = new HttpRequestException(
                "The SSL connection could not be established.",
                new IOException("An existing connection was forcibly closed by the remote host."));
            using var httpClient = new HttpClient(new ThrowingHttpMessageHandler(transportException));
            var installer = new EmbeddingModelPackInstaller(catalog, store, httpClient, new StubValidator(
                EmbeddingModelPackValidationResult.Passed("Smoke validation passed.")));

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(
                manifest.Id,
                progress: null,
                TestContext.Current.CancellationToken));

            Assert.Contains("example.invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("firewall", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("forcibly closed", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OnnxValidator_ReturnsFailureForInvalidModelFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = CreateManifest("invalid-onnx");
            var packDirectory = Path.Combine(directory, manifest.Id);
            Directory.CreateDirectory(Path.Combine(packDirectory, "onnx"));
            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, manifest.ModelFile),
                "not an onnx model",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, manifest.VocabularyFile),
                "[PAD]\n[UNK]\n[CLS]\n[SEP]\nfile\nsearch\nsmoke\nvalidation\n",
                TestContext.Current.CancellationToken);
            var pack = new InstalledEmbeddingModelPack(manifest, packDirectory, true, "Installed.");
            var validator = new OnnxEmbeddingModelPackValidator();

            var result = await validator.ValidateAsync(pack, TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
            Assert.Contains("Smoke validation failed", result.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EmbeddingModelPackManifest CreateManifest(string id) =>
        new()
        {
            Id = id,
            DisplayName = id,
            Version = "1",
            License = "test",
            SourceUrl = "https://example.invalid/model",
            ModelFile = "onnx/model.onnx",
            VocabularyFile = "vocab.txt",
            Files =
            [
                new EmbeddingModelPackFile("onnx/model.onnx", "https://example.invalid/model.onnx"),
                new EmbeddingModelPackFile("vocab.txt", "https://example.invalid/vocab.txt"),
            ],
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-model-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubCatalog(params EmbeddingModelPackCatalogEntry[] entries) : IEmbeddingModelPackCatalog
    {
        public IReadOnlyList<EmbeddingModelPackCatalogEntry> Entries { get; } = entries;

        public EmbeddingModelPackCatalogEntry? GetById(string id) =>
            Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubValidator(EmbeddingModelPackValidationResult result) : IEmbeddingModelPackValidator
    {
        public Task<EmbeddingModelPackValidationResult> ValidateAsync(
            InstalledEmbeddingModelPack pack,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class StaticHttpMessageHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
            });
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
