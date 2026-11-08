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
        int maxNestedArchiveDepth = DefaultMaxNestedArchiveDepth,
        int maxEntryTextCharacters = ZipExtractor.MaxDocumentTextCharacters,
        int maxTotalTextCharacters = 8 * 1024 * 1024,
        int maxEntryTextLines = ZipExtractor.MaxDocumentTextLines,
        int maxTotalTextLines = 200_000,
        long maxPackageBytes = DefaultMaxTotalBytes,
        int maxPackageEntries = DefaultMaxEntries,
        int maxXmlDepth = 64,
        TimeSpan? maxDuration = null)
    {
        if (maxEntryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntryBytes), "Archive per-entry byte limit must be positive.");
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Archive total byte limit must be positive.");
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Archive entry limit must be positive.");
        if (maxNestedArchiveDepth != 0)
            throw new ArgumentOutOfRangeException(nameof(maxNestedArchiveDepth), "Nested archive extraction is not implemented yet.");
        if (maxEntryTextCharacters <= 0 || maxTotalTextCharacters <= 0 || maxEntryTextLines <= 0 || maxTotalTextLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntryTextCharacters), "Archive text and line limits must be positive.");
        if (maxPackageBytes <= 0 || maxPackageEntries <= 0 || maxXmlDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPackageBytes), "Document package limits must be positive.");
        if (maxDuration is { } duration && duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDuration), "Archive duration must be positive.");

        MaxEntryBytes = maxEntryBytes;
        MaxTotalBytes = maxTotalBytes;
        MaxEntries = maxEntries;
        MaxNestedArchiveDepth = maxNestedArchiveDepth;
        MaxEntryTextCharacters = maxEntryTextCharacters;
        MaxTotalTextCharacters = maxTotalTextCharacters;
        MaxEntryTextLines = maxEntryTextLines;
        MaxTotalTextLines = maxTotalTextLines;
        MaxPackageBytes = maxPackageBytes;
        MaxPackageEntries = maxPackageEntries;
        MaxXmlDepth = maxXmlDepth;
        MaxDuration = maxDuration ?? TimeSpan.FromSeconds(20);
    }

    public long MaxEntryBytes { get; }

    public long MaxTotalBytes { get; }

    public int MaxEntries { get; }

    public int MaxNestedArchiveDepth { get; }
    public int MaxEntryTextCharacters { get; }
    public int MaxTotalTextCharacters { get; }
    public int MaxEntryTextLines { get; }
    public int MaxTotalTextLines { get; }
    public long MaxPackageBytes { get; }
    public int MaxPackageEntries { get; }
    public int MaxXmlDepth { get; }
    public TimeSpan MaxDuration { get; }
}
