using System;
using System.IO;

namespace FileSearch.Core.Indexing;

public sealed record FileIndexOptions
{
    public string DatabasePath { get; init; } = GetDefaultDatabasePath();

    private static string GetDefaultDatabasePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("FILESEARCH_INDEX_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch",
            "Index",
            "filesearch.db");
    }
}
