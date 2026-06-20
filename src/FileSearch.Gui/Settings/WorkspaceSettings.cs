using System.Text.Json.Serialization;

namespace FileSearch.Gui.Settings;

public sealed class WorkspaceSettings
{
    public string Name { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public SavedSearchSettings Search { get; set; } = new();

    public List<SearchScope> CustomScopes { get; set; } = new();

    public List<FavoriteResultSettings> FavoriteResults { get; set; } = new();

    public List<string> PinnedPaths { get; set; } = new();

    public List<string> QuickSearchSelectedIndexedRoots { get; set; } = new();

    public string ResultSort { get; set; } = "Relevance";

    public string ResultGroup { get; set; } = "File";

    public string RefinementQuery { get; set; } = string.Empty;

    public bool RunOnLoad { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed workspace)" : Name.Trim();

    [JsonIgnore]
    public string RunOnLoadActionText => RunOnLoad ? "Disable run on load" : "Run search when loaded";

    [JsonIgnore]
    public string RunOnLoadGlyph => RunOnLoad ? "\uE73E" : "\uE768";

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var query = string.IsNullOrWhiteSpace(Search.QueryText) ? "empty query" : Search.QueryText.Trim();
            var path = string.IsNullOrWhiteSpace(Search.SearchPath) ? "no folder" : Search.SearchPath.Trim();
            var run = RunOnLoad ? "runs on load" : "manual run";
            return $"{run} | {query} | {path}";
        }
    }
}
