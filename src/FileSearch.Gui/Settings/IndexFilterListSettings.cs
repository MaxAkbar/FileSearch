using System.Text.Json.Serialization;
using FileSearch.Core;

namespace FileSearch.Gui.Settings;

public sealed class IndexFilterListSettings
{
    private static readonly char[] s_folderSeparators = { ';', ',', '\r', '\n', '\t' };

    public string Name { get; set; } = string.Empty;

    public string Extensions { get; set; } = string.Empty;

    public string Folders { get; set; } = string.Empty;

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var extensions = ExtensionList.Parse(Extensions);
            if (extensions.Length > 0)
                parts.Add(string.Join(", ", extensions));

            var folders = ParseFolders(Folders);
            if (folders.Length > 0)
                parts.Add(string.Join(", ", folders));

            return parts.Count == 0 ? "No filters" : string.Join(" | ", parts);
        }
    }

    public static string[] ParseFolders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw.Split(
                s_folderSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
