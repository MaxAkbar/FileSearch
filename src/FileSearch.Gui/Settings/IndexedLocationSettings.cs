using System.Text.Json.Serialization;

namespace FileSearch.Gui.Settings;

public sealed class IndexedLocationSettings
{
    public string Root { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    public bool IncludeHidden { get; set; } = false;

    public bool EnableDocumentExtraction { get; set; } = true;

    public bool SkipUnknownFileTypes { get; set; } = false;

    public bool WatchEnabled { get; set; } = true;

    public long LastIndexedUtcTicks { get; set; }

    public long FileCount { get; set; }

    public long LineCount { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Root) ? "Index" : System.IO.Path.GetFileName(Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    [JsonIgnore]
    public string Summary => $"{FileCount:n0} files, {LineCount:n0} lines";
}
