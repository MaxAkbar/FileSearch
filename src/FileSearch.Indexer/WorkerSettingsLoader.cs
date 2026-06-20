using System.Text.Json;
using System.Text.Json.Serialization;
using FileSearch.Core;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;
using FileSearch.WindowsOcr;

namespace FileSearch.Indexer;

internal sealed class WorkerSettingsLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] s_documentExtensions =
    [
        ".pdf", ".docx", ".xlsx", ".pptx", ".rtf", ".odt", ".ods", ".odp", ".epub", ".eml",
    ];

    private static readonly char[] s_folderSeparators = [';', ',', '\r', '\n', '\t'];

    private readonly IExtractorRegistry _extractorRegistry;
    private readonly string _settingsPath;

    public WorkerSettingsLoader(IExtractorRegistry extractorRegistry, string? settingsPath = null)
    {
        _extractorRegistry = extractorRegistry;
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath) ? GetDefaultSettingsPath() : settingsPath;
    }

    public WorkerIndexingSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return WorkerIndexingSettings.Empty;

        var settings = LoadAppSettings(_settingsPath);
        var locations = LoadLocations(settings).ToList();
        return new WorkerIndexingSettings(
            NormalizeResourceProfile(settings.IndexerResourceProfile),
            new IndexerRuntimeOptions(
                settings.PauseIndexingOnBattery,
                settings.IndexOnlyWhenIdle,
                settings.IndexerCpuLimitPercent,
                settings.IndexerDiskPauseMilliseconds).Normalize(),
            locations);
    }

    public WorkerOcrSettings LoadOcrSettings()
    {
        if (!File.Exists(_settingsPath))
            return WorkerOcrSettings.Default;

        var settings = LoadAppSettings(_settingsPath);
        return new WorkerOcrSettings(
            settings.OcrLanguageTag ?? string.Empty,
            Math.Max(0, settings.OcrMaxPdfPages));
    }

    private IEnumerable<IndexedLocation> LoadLocations(WorkerAppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in settings.IndexedLocations)
        {
            if (string.IsNullOrWhiteSpace(location.Root))
                continue;

            var root = IndexPath.NormalizeRoot(location.Root);
            if (!seen.Add(root))
                continue;

            yield return new IndexedLocation(
                root,
                BuildWalkerOptions(location, settings.AdditionalPlainTextExtensions),
                location.WatchEnabled);
        }

        if (!string.IsNullOrWhiteSpace(settings.LastIndexedRoot))
        {
            var legacyRoot = IndexPath.NormalizeRoot(settings.LastIndexedRoot);
            if (seen.Add(legacyRoot))
                yield return new IndexedLocation(legacyRoot, new WalkerOptions(), WatchEnabled: true);
        }
    }

    private WalkerOptions BuildWalkerOptions(WorkerIndexedLocation location, string additionalPlainTextExtensions) =>
        new()
        {
            IncludeGlobs = Array.Empty<string>(),
            ExcludeGlobs = Array.Empty<string>(),
            IncludeExtensions = BuildIncludedExtensions(location, additionalPlainTextExtensions),
            ExcludeExtensions = BuildExcludedExtensions(location),
            IncludeDirectories = ParseFolders(location.IncludedFolders).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ExcludeDirectories = BuildExcludedDirectories(location),
            Recursive = location.Recursive,
            IncludeHidden = location.IncludeHidden,
            MinFileSizeBytes = 0,
            MaxFileSizeBytes = 0,
            ModifiedAfterUtc = null,
            ModifiedBeforeUtc = null,
            EnableOcr = location.EnableImageOcr,
        };

    private HashSet<string> BuildIncludedExtensions(WorkerIndexedLocation location, string additionalPlainTextExtensions)
    {
        var explicitIncludes = ExtensionList.Parse(location.IncludedExtensions);
        if (explicitIncludes.Length > 0)
            return explicitIncludes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!location.SkipUnknownFileTypes)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var extensions = _extractorRegistry.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!location.EnableImageOcr)
        {
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
                extensions.Remove(extension);
        }

        foreach (var extension in ExtensionList.Parse(additionalPlainTextExtensions))
            extensions.Add(extension);
        return extensions;
    }

    private static HashSet<string> BuildExcludedExtensions(WorkerIndexedLocation location)
    {
        var extensions = ExtensionList.Parse(location.ExcludedExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!location.EnableDocumentExtraction)
            foreach (var extension in s_documentExtensions)
                extensions.Add(extension);

        if (!location.EnableImageOcr)
            foreach (var extension in ImageOcrFileTypes.SupportedExtensions)
                extensions.Add(extension);

        return extensions;
    }

    private static HashSet<string> BuildExcludedDirectories(WorkerIndexedLocation location)
    {
        var directories = WalkerOptions.DefaultExcludeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ParseFolders(location.ExcludedFolders))
            directories.Add(folder);
        return directories;
    }

    private static IEnumerable<string> ParseFolders(string? raw) =>
        (raw ?? string.Empty)
            .Split(s_folderSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(folder => folder.Length > 0);

    private static string GetDefaultSettingsPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("FILESEARCH_WORKER_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FileSearch");
        return Path.Combine(folder, "settings.json");
    }

    private static WorkerAppSettings LoadAppSettings(string settingsPath)
    {
        var json = File.ReadAllText(settingsPath);
        return JsonSerializer.Deserialize<WorkerAppSettings>(json, s_jsonOptions) ?? new WorkerAppSettings();
    }

    private static IndexerResourceProfile NormalizeResourceProfile(IndexerResourceProfile profile) =>
        Enum.IsDefined(profile) ? profile : IndexerResourceProfile.Balanced;

    private sealed class WorkerAppSettings
    {
        public IndexerResourceProfile IndexerResourceProfile { get; set; } = IndexerResourceProfile.Balanced;

        public List<WorkerIndexedLocation> IndexedLocations { get; set; } = new();

        public string LastIndexedRoot { get; set; } = string.Empty;

        public string AdditionalPlainTextExtensions { get; set; } = string.Empty;

        public bool PauseIndexingOnBattery { get; set; }

        public bool IndexOnlyWhenIdle { get; set; }

        public int IndexerCpuLimitPercent { get; set; }

        public int IndexerDiskPauseMilliseconds { get; set; }

        public string OcrLanguageTag { get; set; } = string.Empty;

        public int OcrMaxPdfPages { get; set; } = 50;
    }

    private sealed class WorkerIndexedLocation
    {
        public string Root { get; set; } = string.Empty;

        public bool Recursive { get; set; } = true;

        public bool IncludeHidden { get; set; }

        public bool EnableDocumentExtraction { get; set; } = true;

        public bool EnableImageOcr { get; set; }

        public bool SkipUnknownFileTypes { get; set; }

        public string IncludedExtensions { get; set; } = string.Empty;

        public string IncludedFolders { get; set; } = string.Empty;

        public string ExcludedExtensions { get; set; } = string.Empty;

        public string ExcludedFolders { get; set; } = string.Empty;

        public bool WatchEnabled { get; set; } = true;

    }
}

internal sealed record WorkerIndexingSettings(
    IndexerResourceProfile ResourceProfile,
    IndexerRuntimeOptions RuntimeOptions,
    IReadOnlyList<IndexedLocation> Locations)
{
    public static WorkerIndexingSettings Empty { get; } =
        new(IndexerResourceProfile.Balanced, IndexerRuntimeOptions.Default, Array.Empty<IndexedLocation>());
}

internal sealed record WorkerOcrSettings(string LanguageTag, int MaxPdfPages)
{
    public static WorkerOcrSettings Default { get; } = new(string.Empty, 50);
}
