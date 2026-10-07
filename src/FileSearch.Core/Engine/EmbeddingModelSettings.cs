using System.Text.Json;

namespace FileSearch.Core.Engine;

/// <summary>Reads the desktop document model configuration without changing settings or starting indexing.</summary>
public static class EmbeddingModelSettings
{
    public static string DefaultSettingsPath =>
        Environment.GetEnvironmentVariable("FILESEARCH_WORKER_SETTINGS_PATH") is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileSearch", "settings.json");

    public static EmbeddingModelPackOptions Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var options = new EmbeddingModelPackOptions { SettingsFilePath = path };
        try
        {
            if (!File.Exists(path)) return options;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("SemanticModelPackId", out var id))
                options.SelectedModelPackId = id.GetString() ?? string.Empty;
            if (json.RootElement.TryGetProperty("SemanticModelPacksDirectory", out var directory) &&
                directory.GetString() is { Length: > 0 } modelDirectory)
                options.ModelPacksDirectory = modelDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Missing/invalid configuration means disabled, never implicit installation or selection.
            options.SelectedModelPackId = string.Empty;
        }
        return options;
    }

    public static SemanticSearchOptions LoadSearchOptions(string? settingsPath = null)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath ?? DefaultSettingsPath));
            var score = json.RootElement.TryGetProperty("SemanticMinimumScore", out var minimum) ? minimum.GetDouble() : SemanticSearchOptions.DefaultMinimumScore;
            var count = json.RootElement.TryGetProperty("SemanticMaximumResults", out var maximum) ? maximum.GetInt32() : SemanticSearchOptions.DefaultMaximumResults;
            return new SemanticSearchOptions(SemanticSearchOptions.NormalizeMinimumScore(score), SemanticSearchOptions.NormalizeMaximumResults(count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new SemanticSearchOptions();
        }
    }
}
