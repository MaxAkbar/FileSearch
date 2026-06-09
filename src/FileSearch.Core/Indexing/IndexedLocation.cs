using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public sealed record IndexedLocation(
    string Root,
    WalkerOptions WalkerOptions,
    bool WatchEnabled = true);
