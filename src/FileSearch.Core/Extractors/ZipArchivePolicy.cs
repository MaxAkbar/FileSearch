using System;

namespace FileSearch.Core.Extractors;

internal sealed record ZipArchivePolicy
{
    public const long DefaultMaxEntryBytes = 16L * 1024 * 1024;
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024;
    public const int DefaultMaxEntries = 10_000;
    public const int DefaultMaxNestedArchiveDepth = 0;

    public static ZipArchivePolicy Default { get; } = new();

    public ZipArchivePolicy(
        long maxEntryBytes = DefaultMaxEntryBytes,
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxEntries = DefaultMaxEntries,
        int maxNestedArchiveDepth = DefaultMaxNestedArchiveDepth)
    {
        if (maxEntryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntryBytes), "Archive per-entry byte limit must be positive.");
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Archive total byte limit must be positive.");
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Archive entry limit must be positive.");
        if (maxNestedArchiveDepth != 0)
            throw new ArgumentOutOfRangeException(nameof(maxNestedArchiveDepth), "Nested archive extraction is not implemented yet.");

        MaxEntryBytes = maxEntryBytes;
        MaxTotalBytes = maxTotalBytes;
        MaxEntries = maxEntries;
        MaxNestedArchiveDepth = maxNestedArchiveDepth;
    }

    public long MaxEntryBytes { get; }

    public long MaxTotalBytes { get; }

    public int MaxEntries { get; }

    public int MaxNestedArchiveDepth { get; }
}
