using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Engine;

public enum EmbeddingModelPooling
{
    Mean,
    Cls,
}

public sealed class EmbeddingModelPackOptions
{
    public string ModelPacksDirectory { get; set; } = GetDefaultModelPacksDirectory();

    public string SelectedModelPackId { get; set; } = string.Empty;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(SelectedModelPackId);

    public static string GetDefaultModelPacksDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("FILESEARCH_MODEL_PACKS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch",
            "Models");
    }
}

public sealed record EmbeddingModelPackFile(
    string RelativePath,
    string DownloadUrl,
    string Sha256 = "",
    long SizeBytes = 0);

public sealed record EmbeddingModelPackManifest
{
    public const string FileName = "model-pack.json";

    public int FormatVersion { get; init; } = 1;

    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;

    public string LicenseUrl { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public string ModelFile { get; init; } = "onnx/model.onnx";

    public string VocabularyFile { get; init; } = "vocab.txt";

    public string TokenizerKind { get; init; } = "bert-wordpiece";

    public bool DoLowerCase { get; init; } = true;

    public int Dimension { get; init; } = 384;

    public int MaxTokens { get; init; } = 512;

    public string QueryPrefix { get; init; } = string.Empty;

    public string DocumentPrefix { get; init; } = string.Empty;

    public string InputIdsName { get; init; } = "input_ids";

    public string AttentionMaskName { get; init; } = "attention_mask";

    public string TokenTypeIdsName { get; init; } = "token_type_ids";

    public string OutputName { get; init; } = string.Empty;

    public EmbeddingModelPooling Pooling { get; init; } = EmbeddingModelPooling.Mean;

    public bool Normalize { get; init; } = true;

    public string QuantizationVersion { get; init; } = string.Empty;

    public IReadOnlyList<EmbeddingModelPackFile> Files { get; init; } = Array.Empty<EmbeddingModelPackFile>();

    public EmbeddingModelInfo ToModelInfo() =>
        new(Id, Version, Dimension, QuantizationVersion);
}

public sealed record EmbeddingModelPackCatalogEntry(
    EmbeddingModelPackManifest Manifest,
    bool IsRecommended,
    string InstallSizeLabel)
{
    public string Id => Manifest.Id;

    public string DisplayName => Manifest.DisplayName;

    public string Summary => Manifest.Description;
}

public sealed record InstalledEmbeddingModelPack(
    EmbeddingModelPackManifest Manifest,
    string DirectoryPath,
    bool IsUsable,
    string Status)
{
    public string ModelPath => Path.Combine(DirectoryPath, Manifest.ModelFile);

    public string VocabularyPath => Path.Combine(DirectoryPath, Manifest.VocabularyFile);
}

public sealed record EmbeddingModelPackValidationResult(
    bool IsValid,
    string Status)
{
    public static EmbeddingModelPackValidationResult Passed(string status) =>
        new(true, string.IsNullOrWhiteSpace(status) ? "Smoke validation passed." : status);

    public static EmbeddingModelPackValidationResult Failed(string status) =>
        new(false, string.IsNullOrWhiteSpace(status) ? "Smoke validation failed." : status);
}

public sealed record EmbeddingModelPackValidationStamp(
    int FormatVersion,
    string ModelId,
    string ModelVersion,
    int Dimension,
    bool IsValid,
    string Status,
    DateTime ValidatedUtc)
{
    public const string FileName = "model-pack.validation.json";

    public static EmbeddingModelPackValidationStamp FromResult(
        EmbeddingModelPackManifest manifest,
        EmbeddingModelPackValidationResult result) =>
        new(
            1,
            manifest.Id,
            manifest.Version,
            manifest.Dimension,
            result.IsValid,
            result.Status,
            DateTime.UtcNow);

    public bool Matches(EmbeddingModelPackManifest manifest) =>
        FormatVersion == 1 &&
        Dimension == manifest.Dimension &&
        string.Equals(ModelId, manifest.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ModelVersion, manifest.Version, StringComparison.OrdinalIgnoreCase);
}

public sealed record EmbeddingModelInstallProgress(
    string ModelId,
    string CurrentFile,
    int CompletedFiles,
    int TotalFiles,
    long BytesReceived,
    long? TotalBytes);

public interface IEmbeddingModelPackCatalog
{
    IReadOnlyList<EmbeddingModelPackCatalogEntry> Entries { get; }

    EmbeddingModelPackCatalogEntry? GetById(string id);
}

public sealed class EmbeddingModelPackCatalog : IEmbeddingModelPackCatalog
{
    private static readonly string s_bgeRepository =
        "https://huggingface.co/onnx-community/bge-small-en-v1.5-ONNX";

    private static readonly string s_miniLmRepository =
        "https://huggingface.co/onnx-community/all-MiniLM-L6-v2-ONNX";

    public IReadOnlyList<EmbeddingModelPackCatalogEntry> Entries { get; } =
    [
        new(
            new EmbeddingModelPackManifest
            {
                Id = "bge-small-en-v1.5-onnx",
                DisplayName = "BGE small English v1.5",
                Version = "onnx-community-main",
                Description = "Recommended local semantic search model. English, 384 dimensions, runs with ONNX Runtime.",
                License = "MIT base model, ONNX port hosted by onnx-community",
                LicenseUrl = "https://huggingface.co/BAAI/bge-small-en-v1.5",
                SourceUrl = s_bgeRepository,
                ModelFile = "onnx/model.onnx",
                VocabularyFile = "vocab.txt",
                Dimension = 384,
                MaxTokens = 512,
                QueryPrefix = "Represent this sentence for searching relevant passages: ",
                Pooling = EmbeddingModelPooling.Cls,
                Normalize = true,
                QuantizationVersion = "onnx-community",
                Files =
                [
                    File(s_bgeRepository, "onnx/model.onnx"),
                    File(s_bgeRepository, "onnx/model.onnx_data"),
                    File(s_bgeRepository, "config.json"),
                    File(s_bgeRepository, "tokenizer_config.json"),
                    File(s_bgeRepository, "special_tokens_map.json"),
                    File(s_bgeRepository, "vocab.txt"),
                ],
            },
            IsRecommended: true,
            InstallSizeLabel: "About 331 MB"),
        new(
            new EmbeddingModelPackManifest
            {
                Id = "all-minilm-l6-v2-onnx",
                DisplayName = "all-MiniLM-L6-v2",
                Version = "onnx-community-main",
                Description = "Lightweight compatibility model. English, 384 dimensions, runs with ONNX Runtime.",
                License = "Apache-2.0",
                LicenseUrl = "https://huggingface.co/onnx-community/all-MiniLM-L6-v2-ONNX",
                SourceUrl = s_miniLmRepository,
                ModelFile = "onnx/model.onnx",
                VocabularyFile = "vocab.txt",
                Dimension = 384,
                MaxTokens = 512,
                Pooling = EmbeddingModelPooling.Mean,
                Normalize = true,
                QuantizationVersion = "onnx-community",
                Files =
                [
                    File(s_miniLmRepository, "onnx/model.onnx"),
                    File(s_miniLmRepository, "onnx/model.onnx_data"),
                    File(s_miniLmRepository, "config.json"),
                    File(s_miniLmRepository, "tokenizer_config.json"),
                    File(s_miniLmRepository, "special_tokens_map.json"),
                    File(s_miniLmRepository, "vocab.txt"),
                ],
            },
            IsRecommended: false,
            InstallSizeLabel: "About 221 MB"),
    ];

    public EmbeddingModelPackCatalogEntry? GetById(string id) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    private static EmbeddingModelPackFile File(string repository, string relativePath) =>
        new(relativePath, $"{repository}/resolve/main/{relativePath}");
}

public interface IEmbeddingModelPackStore
{
    string ModelPacksDirectory { get; }

    Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(CancellationToken cancellationToken);

    Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken);

    string GetPackDirectory(string modelId);
}

public sealed class EmbeddingModelPackStore : IEmbeddingModelPackStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly EmbeddingModelPackOptions _options;
    private readonly ILogger _logger;

    public EmbeddingModelPackStore(
        EmbeddingModelPackOptions? options = null,
        ILogger<EmbeddingModelPackStore>? logger = null)
    {
        _options = options ?? new EmbeddingModelPackOptions();
        _logger = logger ?? NullLogger<EmbeddingModelPackStore>.Instance;
    }

    public string ModelPacksDirectory => NormalizeModelPacksDirectory(_options.ModelPacksDirectory);

    public async Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(
        CancellationToken cancellationToken)
    {
        var root = ModelPacksDirectory;
        if (!Directory.Exists(root))
            return Array.Empty<InstalledEmbeddingModelPack>();

        var packs = new List<InstalledEmbeddingModelPack>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, EmbeddingModelPackManifest.FileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = await ReadInstalledPackAsync(Path.GetDirectoryName(manifestPath) ?? root, cancellationToken)
                .ConfigureAwait(false);
            if (pack is not null)
                packs.Add(pack);
        }

        return packs
            .OrderBy(pack => pack.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
            return null;

        var directory = GetPackDirectory(_options.SelectedModelPackId);
        return await ReadInstalledPackAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    public string GetPackDirectory(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID is required.", nameof(modelId));

        return Path.Combine(ModelPacksDirectory, SanitizePathSegment(modelId));
    }

    private async Task<InstalledEmbeddingModelPack?> ReadInstalledPackAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, EmbeddingModelPackManifest.FileName);
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manifest = await JsonSerializer.DeserializeAsync<EmbeddingModelPackManifest>(
                    stream,
                    s_jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
                return null;

            var isUsable = IsUsable(manifest, directory, out var status);
            if (isUsable)
            {
                var validation = await ReadValidationStampAsync(directory, cancellationToken).ConfigureAwait(false);
                if (validation is not null && validation.Matches(manifest))
                {
                    isUsable = validation.IsValid;
                    status = validation.Status;
                }
                else
                {
                    status = "Installed. Smoke validation has not run.";
                }
            }

            return new InstalledEmbeddingModelPack(
                manifest,
                directory,
                isUsable,
                status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read embedding model pack manifest at {Path}.", manifestPath);
            return null;
        }
    }

    private static async Task<EmbeddingModelPackValidationStamp?> ReadValidationStampAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var validationPath = Path.Combine(directory, EmbeddingModelPackValidationStamp.FileName);
        if (!File.Exists(validationPath))
            return null;

        await using var stream = new FileStream(validationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<EmbeddingModelPackValidationStamp>(
                stream,
                s_jsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Trim()
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '-' : ch)
            .ToArray();
        return new string(chars);
    }

    internal static string NormalizeModelPacksDirectory(string directory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(directory)
            ? EmbeddingModelPackOptions.GetDefaultModelPacksDirectory()
            : directory);

    private static bool IsUsable(EmbeddingModelPackManifest manifest, string directory, out string status)
    {
        var modelPath = Path.Combine(directory, manifest.ModelFile);
        if (!File.Exists(modelPath))
        {
            status = "Model file is missing.";
            return false;
        }

        var vocabularyPath = Path.Combine(directory, manifest.VocabularyFile);
        if (!File.Exists(vocabularyPath))
        {
            status = "Vocabulary file is missing.";
            return false;
        }

        status = "Installed.";
        return true;
    }
}

public interface IEmbeddingModelPackInstaller
{
    Task<InstalledEmbeddingModelPack> InstallAsync(
        string modelId,
        IProgress<EmbeddingModelInstallProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IEmbeddingModelPackValidator
{
    Task<EmbeddingModelPackValidationResult> ValidateAsync(
        InstalledEmbeddingModelPack pack,
        CancellationToken cancellationToken);
}

public sealed class OnnxEmbeddingModelPackValidator : IEmbeddingModelPackValidator
{
    private const string DocumentSmokeText = "FileSearch local embedding model smoke validation document.";
    private const string QuerySmokeText = "FileSearch local embedding model smoke validation query.";

    public async Task<EmbeddingModelPackValidationResult> ValidateAsync(
        InstalledEmbeddingModelPack pack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        cancellationToken.ThrowIfCancellationRequested();

        if (!pack.IsUsable)
            return EmbeddingModelPackValidationResult.Failed(pack.Status);

        try
        {
            using var embedder = new OnnxTextEmbedder(new SinglePackStore(pack));
            var documentEmbedding = await embedder
                .EmbedAsync(DocumentSmokeText, TextEmbeddingInputKind.Document, cancellationToken)
                .ConfigureAwait(false);
            var queryEmbedding = await embedder
                .EmbedAsync(QuerySmokeText, TextEmbeddingInputKind.Query, cancellationToken)
                .ConfigureAwait(false);

            ValidateEmbedding(pack.Manifest, documentEmbedding, "document");
            ValidateEmbedding(pack.Manifest, queryEmbedding, "query");
            return EmbeddingModelPackValidationResult.Passed(
                $"Smoke validation passed ({pack.Manifest.Dimension:n0} dimensions).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EmbeddingModelPackValidationResult.Failed($"Smoke validation failed: {ex.Message}");
        }
    }

    private static void ValidateEmbedding(
        EmbeddingModelPackManifest manifest,
        TextEmbedding embedding,
        string inputKind)
    {
        if (embedding.Model.Dimension != manifest.Dimension ||
            embedding.Vector.Length != manifest.Dimension)
        {
            throw new InvalidOperationException(
                $"{inputKind} embedding dimension {embedding.Vector.Length:n0} did not match manifest dimension {manifest.Dimension:n0}.");
        }

        var vector = embedding.Vector.Span;
        var nonZero = false;
        for (var i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i]))
                throw new InvalidOperationException($"{inputKind} embedding contains a non-finite value.");
            nonZero |= vector[i] != 0;
        }

        if (!nonZero)
            throw new InvalidOperationException($"{inputKind} embedding is all zero.");
    }

    private sealed class SinglePackStore(InstalledEmbeddingModelPack pack) : IEmbeddingModelPackStore
    {
        public string ModelPacksDirectory => pack.DirectoryPath;

        public Task<IReadOnlyList<InstalledEmbeddingModelPack>> GetInstalledPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledEmbeddingModelPack>>(new[] { pack });

        public Task<InstalledEmbeddingModelPack?> GetSelectedPackAsync(CancellationToken cancellationToken) =>
            Task.FromResult<InstalledEmbeddingModelPack?>(pack);

        public string GetPackDirectory(string modelId) => pack.DirectoryPath;
    }
}

public sealed class EmbeddingModelPackInstaller : IEmbeddingModelPackInstaller, IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly IEmbeddingModelPackCatalog _catalog;
    private readonly IEmbeddingModelPackStore _store;
    private readonly IEmbeddingModelPackValidator _validator;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public EmbeddingModelPackInstaller(
        IEmbeddingModelPackCatalog catalog,
        IEmbeddingModelPackStore store,
        HttpClient? httpClient = null,
        IEmbeddingModelPackValidator? validator = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? new OnnxEmbeddingModelPackValidator();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public async Task<InstalledEmbeddingModelPack> InstallAsync(
        string modelId,
        IProgress<EmbeddingModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entry = _catalog.GetById(modelId)
            ?? throw new ArgumentException($"Unknown embedding model pack '{modelId}'.", nameof(modelId));

        var directory = _store.GetPackDirectory(entry.Id);
        Directory.CreateDirectory(directory);

        var files = entry.Manifest.Files.ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new EmbeddingModelInstallProgress(entry.Id, file.RelativePath, i, files.Length, 0, file.SizeBytes > 0 ? file.SizeBytes : null));
            await DownloadFileAsync(directory, entry.Id, file, i, files.Length, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var manifestPath = Path.Combine(directory, EmbeddingModelPackManifest.FileName);
        await using (var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, entry.Manifest, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var installed = new InstalledEmbeddingModelPack(
            entry.Manifest,
            directory,
            IsUsable(entry.Manifest, directory, out var status),
            status);
        if (!installed.IsUsable)
            return installed;

        var validation = await _validator.ValidateAsync(installed, cancellationToken).ConfigureAwait(false);
        await WriteValidationStampAsync(directory, entry.Manifest, validation, cancellationToken).ConfigureAwait(false);
        return installed with
        {
            IsUsable = validation.IsValid,
            Status = validation.Status,
        };
    }

    private async Task DownloadFileAsync(
        string directory,
        string modelId,
        EmbeddingModelPackFile file,
        int index,
        int fileCount,
        IProgress<EmbeddingModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.DownloadUrl))
            throw new InvalidOperationException($"Download URL is missing for {file.RelativePath}.");

        using var response = await _httpClient.GetAsync(
                file.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var targetPath = Path.Combine(directory, file.RelativePath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string hash;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var sha256 = SHA256.Create();
                var buffer = new byte[1024 * 128];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                    total += read;
                    progress?.Report(new EmbeddingModelInstallProgress(
                        modelId,
                        file.RelativePath,
                        index,
                        fileCount,
                        total,
                        response.Content.Headers.ContentLength ?? (file.SizeBytes > 0 ? file.SizeBytes : null)));
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256) &&
                !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Checksum mismatch for {file.RelativePath}.");
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool IsUsable(EmbeddingModelPackManifest manifest, string directory, out string status)
    {
        var modelPath = Path.Combine(directory, manifest.ModelFile);
        var vocabularyPath = Path.Combine(directory, manifest.VocabularyFile);
        if (!File.Exists(modelPath))
        {
            status = "Model file is missing.";
            return false;
        }

        if (!File.Exists(vocabularyPath))
        {
            status = "Vocabulary file is missing.";
            return false;
        }

        status = "Installed.";
        return true;
    }

    private static async Task WriteValidationStampAsync(
        string directory,
        EmbeddingModelPackManifest manifest,
        EmbeddingModelPackValidationResult validation,
        CancellationToken cancellationToken)
    {
        var validationPath = Path.Combine(directory, EmbeddingModelPackValidationStamp.FileName);
        await using var stream = new FileStream(validationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
                stream,
                EmbeddingModelPackValidationStamp.FromResult(manifest, validation),
                s_jsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
