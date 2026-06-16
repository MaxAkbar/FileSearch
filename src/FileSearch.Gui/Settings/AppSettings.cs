using System.Collections.Generic;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Settings;

/// <summary>
/// User preferences and last-session state persisted between runs.
/// Keep this a flat POCO: new fields just get added here and to the JSON.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool SkipUnknownFileTypes { get; set; }

    public bool UseIndex { get; set; }

    public List<IndexedLocationSettings> IndexedLocations { get; set; } = new();

    public List<IndexFilterListSettings> IndexInclusionLists { get; set; } = new();

    public List<IndexFilterListSettings> IndexExclusionLists { get; set; } = new();

    // Legacy single-root field retained for migration into IndexedLocations.
    public string LastIndexedRoot { get; set; } = string.Empty;

    public string AdditionalPlainTextExtensions { get; set; } = string.Empty;

    /// <summary>
    /// Most-recently-used full search definitions, most-recent first. Capped
    /// at ~15 entries. Surfaced in the Saved searches sidebar section.
    /// </summary>
    public List<SavedSearchSettings> SavedSearches { get; set; } = new();

    /// <summary>
    /// Legacy query-only search history. Retained for migration and for any
    /// settings files created before saved searches became structured.
    /// </summary>
    public List<string> RecentQueries { get; set; } = new();

    /// <summary>
    /// Most-recently-used "Look in" folders, most-recent first. Capped at
    /// ~15 entries. Surfaced as the dropdown on the "Look in" field.
    /// </summary>
    public List<string> RecentPaths { get; set; } = new();

    /// <summary>
    /// User-created file-pattern scopes, shown below the built-in scope
    /// presets in the sidebar.
    /// </summary>
    public List<SearchScope> CustomScopes { get; set; } = new();

    // ----- legacy fields (pre-history) -----
    // Kept so settings files from older versions can be migrated by the
    // store at load time. After migration these get set to null and
    // omitted from the next save.
    public string? LastQuery { get; set; }
    public string? LastSearchPath { get; set; }
}
