using System;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed record IndexRequest(
    string Root,
    WalkerOptions WalkerOptions,
    Action<IndexProgress>? Progress = null,
    IndexingThrottle? Throttle = null,
    Action<IndexValidationProgress>? ValidationProgress = null);
