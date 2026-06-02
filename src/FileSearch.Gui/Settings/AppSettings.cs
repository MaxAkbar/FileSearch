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

    public bool SkipUnknownFileTypes { get; set; } = false;

    public string AdditionalPlainTextExtensions { get; set; } = string.Empty;

    /// <summary>
    /// Most-recently-used search queries, most-recent first. Capped at
    /// ~15 entries. Surfaced as the dropdown on the "Containing text" field.
    /// </summary>
    public List<string> RecentQueries { get; set; } = new();

    /// <summary>
    /// Most-recently-used "Look in" folders, most-recent first. Capped at
    /// ~15 entries. Surfaced as the dropdown on the "Look in" field.
    /// </summary>
    public List<string> RecentPaths { get; set; } = new();

    // ----- legacy fields (pre-history) -----
    // Kept so settings files from older versions can be migrated by the
    // store at load time. After migration these get set to null and
    // omitted from the next save.
    public string? LastQuery { get; set; }
    public string? LastSearchPath { get; set; }
}
