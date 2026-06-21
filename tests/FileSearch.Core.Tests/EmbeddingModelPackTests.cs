using System.Net;
using System.Text;
using System.Text.Json;
using FileSearch.Core.Engine;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class EmbeddingModelPackTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

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
}
