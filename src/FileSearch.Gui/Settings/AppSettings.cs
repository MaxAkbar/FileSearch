using System.Collections.Generic;
using FileSearch.Core.Indexing;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Settings;

/// <summary>
/// User preferences and last-session state persisted between runs.
/// Keep this a flat POCO: new fields just get added here and to the JSON.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public string CustomThemeFileName { get; set; } = string.Empty;

    /// <summary>
    /// Number of rows shown per page in each searchable sidebar section.
    /// </summary>
    public int SidebarPageSize { get; set; } = 7;

    /// <summary>
    /// Controls how aggressively the background indexer uses CPU/disk.
    /// </summary>
    public IndexerResourceProfile IndexerResourceProfile { get; set; } = IndexerResourceProfile.Balanced;

    public bool KeepIndexUpdatedAfterClose { get; set; }

    public bool StartBackgroundIndexerAtSignIn { get; set; }

    public bool PauseIndexingOnBattery { get; set; }

    public bool IndexOnlyWhenIdle { get; set; }

    public int IndexerCpuLimitPercent { get; set; }

    public int IndexerDiskPauseMilliseconds { get; set; }

    // Legacy setting migrated into KeepIndexUpdatedAfterClose and
    // StartBackgroundIndexerAtSignIn on load.
    public bool? RunInBackground { get; set; }

    public bool SkipUnknownFileTypes { get; set; }

    public bool UseIndex { get; set; }

    public QuickSearchHotkey QuickSearchHotkey { get; set; } = QuickSearchHotkey.WinShiftF;

    public QuickSearchScopeKind QuickSearchDefaultScope { get; set; } = QuickSearchScopeKind.AllIndexedLocations;

    public QuickSearchScopeKind QuickSearchLastScope { get; set; } = QuickSearchScopeKind.AllIndexedLocations;

    public bool QuickSearchRememberLastScope { get; set; } = true;

    public bool QuickSearchIncludeContent { get; set; } = true;

    public string QuickSearchFolderPath { get; set; } = string.Empty;

    public List<string> QuickSearchSelectedIndexedRoots { get; set; } = new();

    public List<string> QuickSearchPinnedPaths { get; set; } = new();

    public List<FavoriteResultSettings> FavoriteResults { get; set; } = new();

    public List<WorkspaceSettings> Workspaces { get; set; } = new();

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
