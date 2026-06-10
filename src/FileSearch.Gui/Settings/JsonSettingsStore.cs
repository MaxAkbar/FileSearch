using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Gui.Settings;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON in
/// <c>%AppData%/FileSearch/settings.json</c>. Failures are swallowed —
/// settings persistence must never crash the app.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public JsonSettingsStore(ILogger<JsonSettingsStore>? logger = null)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FileSearch");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        _logger = logger ?? NullLogger<JsonSettingsStore>.Instance;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, s_options) ?? new AppSettings();
            MigrateLegacyFields(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load settings; using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Older settings files stored a single LastQuery/LastSearchPath. Move
    /// those values into the recent-history lists and clear the legacy
    /// fields so the next save doesn't keep them around.
    /// </summary>
    private static void MigrateLegacyFields(AppSettings s)
    {
        if (s.RecentQueries.Count == 0 && !string.IsNullOrWhiteSpace(s.LastQuery))
            s.RecentQueries.Add(s.LastQuery);
        if (s.RecentPaths.Count == 0 && !string.IsNullOrWhiteSpace(s.LastSearchPath))
            s.RecentPaths.Add(s.LastSearchPath);

        s.LastQuery = null;
        s.LastSearchPath = null;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            // Write-then-move so a crash mid-write can't truncate the file.
            var json = JsonSerializer.Serialize(settings, s_options);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Persisted settings are a convenience — never crash on save.
            _logger.LogWarning(ex, "Could not save settings.");
        }
    }
}
