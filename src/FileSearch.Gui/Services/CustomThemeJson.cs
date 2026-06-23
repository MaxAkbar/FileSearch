using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace FileSearch.Gui.Services;

internal sealed class CustomThemeDefinition
{
    public string Name { get; set; } = string.Empty;

    public AppTheme BaseTheme { get; set; } = AppTheme.Light;

    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Fonts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class CustomThemeJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CustomThemeInfo LoadInfo(string path)
    {
        var definition = LoadDefinition(path);
        var fileName = Path.GetFileName(path);
        var name = string.IsNullOrWhiteSpace(definition.Name)
            ? Path.GetFileNameWithoutExtension(path)
            : definition.Name.Trim();

        return new CustomThemeInfo(name, fileName, path, definition.BaseTheme);
    }

    public static CustomThemeDefinition LoadDefinition(string path)
    {
        var json = File.ReadAllText(path);
        var definition = JsonSerializer.Deserialize<CustomThemeDefinition>(json, s_options)
            ?? throw new InvalidOperationException("Theme file is empty.");

        definition.Name = definition.Name?.Trim() ?? string.Empty;
        definition.Colors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        definition.Fonts ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return definition;
    }

    public static ResourceDictionary CreateResourceDictionary(CustomThemeDefinition definition)
    {
        var dictionary = new ResourceDictionary();

        foreach (var (key, value) in definition.Colors)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            var color = ParseColor(value);
            if (IsColorKey(key))
            {
                dictionary[key] = color;
                continue;
            }

            if (TryGetAtlasColorCompanionKey(key, out var colorKey) && !dictionary.Contains(colorKey))
                dictionary[colorKey] = color;

            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
                brush.Freeze();

            dictionary[key] = brush;
        }

        foreach (var (key, value) in definition.Fonts)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            dictionary[key] = new FontFamily(value.Trim());
        }

        return dictionary;
    }

    private static bool IsColorKey(string key) =>
        key.EndsWith("Color", StringComparison.Ordinal) &&
        !key.EndsWith("Brush", StringComparison.Ordinal);

    private static bool TryGetAtlasColorCompanionKey(string key, out string colorKey)
    {
        const string brushSuffix = "Brush";
        if (key.StartsWith("Atlas.", StringComparison.Ordinal) &&
            key.EndsWith(brushSuffix, StringComparison.Ordinal))
        {
            colorKey = key[..^brushSuffix.Length] + "Color";
            return true;
        }

        colorKey = string.Empty;
        return false;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid color value '{value}'. Use #RRGGBB or #AARRGGBB.", ex);
        }
    }
}
