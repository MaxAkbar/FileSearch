using System;
using System.IO;

namespace FileSearch.Core.Indexing;

public sealed record FileIndexOptions
{
    public string DatabasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileSearch",
        "Index",
        "filesearch.db");
}
