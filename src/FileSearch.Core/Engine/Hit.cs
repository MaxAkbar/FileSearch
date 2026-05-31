using System.Collections.Generic;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

/// <summary>
/// A single match found by the search engine.
/// </summary>
public sealed record Hit(
    string Path,
    int LineNumber,
    string LineContent,
    IReadOnlyList<MatchSpan> Highlights);
