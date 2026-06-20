using System;
using System.Text.Json.Serialization;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Gui.Settings;

public sealed class SavedSearchSettings
{
    public string QueryText { get; set; } = string.Empty;

    public string SearchPath { get; set; } = string.Empty;

    public string FileNamePattern { get; set; } = string.Empty;

    public string ExcludeFileNamePattern { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; } = true;

    public QueryMode SearchMode { get; set; } = QueryMode.Unified;

    public bool MatchCase { get; set; }

    public bool EnableDocumentExtraction { get; set; } = true;

    public bool EnableImageOcr { get; set; }

    public bool SkipUnknownFileTypes { get; set; }

    public bool UseIndex { get; set; }

    public SearchTarget SearchTarget { get; set; } = SearchTarget.Content;

    public int MinSizeKB { get; set; }

    public int MaxSizeKB { get; set; }

    public bool ModifiedAfterEnabled { get; set; }

    public DateTime ModifiedAfter { get; set; } = DateTime.Today.AddDays(-7);

    public bool ModifiedBeforeEnabled { get; set; }

    public DateTime ModifiedBefore { get; set; } = DateTime.Today;

    public string AdditionalPlainTextExtensions { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(QueryText) ? "(empty search)" : QueryText.Trim();

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(SearchPath) ? "No folder" : SearchPath.Trim();
            var scope = string.IsNullOrWhiteSpace(FileNamePattern) ? "all files" : FileNamePattern.Trim();
            var target = SearchTarget == SearchTarget.Content ? SearchMode.ToString() : SearchTarget.ToString();
            return $"{path} | {target} | {scope}";
        }
    }
}
