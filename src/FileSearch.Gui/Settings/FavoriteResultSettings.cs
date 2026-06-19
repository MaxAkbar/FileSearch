using System.Text.Json.Serialization;

namespace FileSearch.Gui.Settings;

public sealed class FavoriteResultSettings
{
    public string Path { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string DisplayName => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}
