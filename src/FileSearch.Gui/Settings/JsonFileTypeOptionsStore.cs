using System;
using System.IO;
using System.Text.Json;

namespace FileSearch.Gui.Settings;

public sealed class JsonFileTypeOptionsStore : IFileTypeOptionsStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public JsonFileTypeOptionsStore()
        : this(GetDefaultPath())
    {
    }

    internal JsonFileTypeOptionsStore(string path)
    {
        _path = path;
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
        catch
        {
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

            var json = JsonSerializer.Serialize(options, s_options);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // File-type configuration is user-editable convenience; never crash on save.
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
                foreach (var extension in MainViewModelsExtensionParser.Parse(value))
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

    private static class MainViewModelsExtensionParser
    {
        public static IEnumerable<string> Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;

            foreach (var value in raw.Split(new[] { ';', ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var extension = value.Trim();
                if (extension.StartsWith("*.", StringComparison.Ordinal))
                    extension = extension[1..];
                if (!extension.StartsWith(".", StringComparison.Ordinal))
                    extension = "." + extension;
                extension = extension.ToLowerInvariant();
                if (extension.Length > 1)
                    yield return extension;
            }
        }
    }
}
