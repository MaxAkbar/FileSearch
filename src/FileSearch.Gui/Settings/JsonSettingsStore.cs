using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public JsonSettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FileSearch");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
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
        catch
        {
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
            var json = JsonSerializer.Serialize(settings, s_options);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Persisted settings are a convenience — never crash on save.
        }
    }
}
