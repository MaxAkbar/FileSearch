using System;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Shapes walker options for index builds. The index profile only records
/// recursion, hidden-file handling, and extension filters, so any other
/// search-time filter baked into a build would silently narrow what gets
/// indexed while coverage checks still report the index as complete.
/// </summary>
internal static class IndexWalkerOptions
{
    /// <summary>
    /// Strips per-search filters (globs, size, date) that the profile does
    /// not record; they are re-applied per query by
    /// <see cref="IndexedFileFilter"/> instead.
    /// </summary>
    public static WalkerOptions ForIndexing(WalkerOptions options) =>
        options with
        {
            IncludeGlobs = Array.Empty<string>(),
            ExcludeGlobs = Array.Empty<string>(),
            MinFileSizeBytes = 0,
            MaxFileSizeBytes = 0,
            ModifiedAfterUtc = null,
            ModifiedBeforeUtc = null,
        };
}
