using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Gui.Settings;

public sealed class JsonFileTypeOptionsStore : IFileTypeOptionsStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public JsonFileTypeOptionsStore(ILogger<JsonFileTypeOptionsStore>? logger = null)
        : this(GetDefaultPath(), logger)
    {
    }

    internal JsonFileTypeOptionsStore(string path, ILogger<JsonFileTypeOptionsStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<JsonFileTypeOptionsStore>.Instance;
    }

    public FileTypeOptions Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = new FileTypeOptions();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_path);
            var options = JsonSerializer.Deserialize<FileTypeOptions>(json, s_options) ?? new FileTypeOptions();
            Normalize(options);
            return options;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load file-type options; using defaults.");
            return new FileTypeOptions();
        }
    }

    public void Save(FileTypeOptions options)
    {
        try
        {
            Normalize(options);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write-then-move so a crash mid-write can't truncate the file.
            var json = JsonSerializer.Serialize(options, s_options);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // File-type configuration is user-editable convenience; never crash on save.
            _logger.LogWarning(ex, "Could not save file-type options.");
        }
    }

    private static void Normalize(FileTypeOptions options)
    {
        options.DocumentExtensions = NormalizeExtensions(options.DocumentExtensions);
        options.AdditionalPlainTextExtensions = NormalizeExtensions(options.AdditionalPlainTextExtensions);
    }

    internal static List<string> NormalizeExtensions(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (var value in values)
            {
                foreach (var extension in FileSearch.Core.ExtensionList.Parse(value))
                    result.Add(extension);
            }
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetDefaultPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FileSearch");
        return Path.Combine(folder, "file-types.json");
    }
}
