using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Gui.Settings;

public sealed class SavedSearchSettings
{
    public string QueryText { get; set; } = string.Empty;

    public string SearchPath { get; set; } = string.Empty;

    public LocationScopeKind LocationScope { get; set; } = LocationScopeKind.SelectedFolder;

    public List<string> SelectedIndexedRoots { get; set; } = [];

    public bool UseQuickSearchMatching { get; set; }

    public string FileNamePattern { get; set; } = string.Empty;

    public string ExcludeFileNamePattern { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; } = true;

    public QueryMode SearchMode { get; set; } = QueryMode.Unified;

    public bool MatchCase { get; set; }

    public bool EnableDocumentExtraction { get; set; } = true;

    public bool EnableImageOcr { get; set; }

    public bool SkipUnknownFileTypes { get; set; }

    public bool UseIndex { get; set; }

    public double SemanticMinimumScore { get; set; } = SemanticSearchOptions.DefaultMinimumScore;

    public int SemanticMaximumResults { get; set; } = SemanticSearchOptions.DefaultMaximumResults;

    public SearchTarget SearchTarget { get; set; } = SearchTarget.Content;

    public List<SearchTarget> SearchTargets { get; set; } = [];

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

    public LocationScopeKind GetLocationScope() =>
        Enum.IsDefined(LocationScope) ? LocationScope : LocationScopeKind.SelectedFolder;

    public IReadOnlyList<string> GetSelectedIndexedRoots() => (SelectedIndexedRoots ?? [])
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Select(root => Path.TrimEndingDirectorySeparator(root.Trim()))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    [JsonIgnore]
    public string LocationSummary => GetLocationScope() switch
    {
        LocationScopeKind.AllIndexedLocations => "All indexed locations",
        LocationScopeKind.SelectedIndexedLocations => GetSelectedIndexedRoots().Count switch
        {
            0 => "No indexed locations selected",
            1 => "1 selected indexed location",
            var count => $"{count} selected indexed locations",
        },
        _ => string.IsNullOrWhiteSpace(SearchPath) ? "No folder" : SearchPath.Trim(),
    };

    public IReadOnlyList<SearchTarget> GetSearchTargets()
    {
        var configuredTargets = SearchTargets.Count > 0 ? SearchTargets : [SearchTarget];
        var targets = new List<SearchTarget>(3);

        foreach (var target in configuredTargets)
        {
            if (target == SearchTarget.FileAndFolderNames)
            {
                targets.Add(SearchTarget.FileNames);
                targets.Add(SearchTarget.FolderNames);
            }
            else if (Enum.IsDefined(target))
            {
                targets.Add(target);
            }
        }

        return targets.Count == 0
            ? [SearchTarget.Content]
            : targets.Distinct().ToArray();
    }

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var path = LocationSummary;
            var scope = string.IsNullOrWhiteSpace(FileNamePattern) ? "all files" : FileNamePattern.Trim();
            var targets = GetSearchTargets();
            var target = targets.Count == 1 && targets[0] == SearchTarget.Content
                ? SearchMode.ToString()
                : string.Join(", ", targets);
            return $"{path} | {target} | {scope}" + (UseQuickSearchMatching ? " | Quick Search matching" : "");
        }
    }
}
