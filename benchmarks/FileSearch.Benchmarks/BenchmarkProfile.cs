namespace FileSearch.Benchmarks;

internal sealed record BenchmarkProfile(
    string Name,
    int MetadataOnlyEntryCount,
    int SmallTextFileCount,
    int OfficeDocumentCount,
    int PdfDocumentCount,
    int LargeLogFileCount,
    int LargeLogLinesPerFile,
    int ArchiveCount,
    int ArchiveEntriesPerFile,
    int UnicodeAndLongPathFileCount,
    int StoppedIndexerChangeCount,
    int QueryIterations)
{
    public static BenchmarkProfile Smoke { get; } = new(
        "smoke",
        MetadataOnlyEntryCount: 10_000,
        SmallTextFileCount: 50,
        OfficeDocumentCount: 2,
        PdfDocumentCount: 1,
        LargeLogFileCount: 1,
        LargeLogLinesPerFile: 200,
        ArchiveCount: 1,
        ArchiveEntriesPerFile: 3,
        UnicodeAndLongPathFileCount: 4,
        StoppedIndexerChangeCount: 5,
        QueryIterations: 10);

    public static BenchmarkProfile Standard { get; } = new(
        "standard",
        MetadataOnlyEntryCount: 100_000,
        SmallTextFileCount: 10_000,
        OfficeDocumentCount: 30,
        PdfDocumentCount: 30,
        LargeLogFileCount: 6,
        LargeLogLinesPerFile: 50_000,
        ArchiveCount: 12,
        ArchiveEntriesPerFile: 20,
        UnicodeAndLongPathFileCount: 200,
        StoppedIndexerChangeCount: 500,
        QueryIterations: 100);

    public static BenchmarkProfile Full { get; } = new(
        "full",
        MetadataOnlyEntryCount: 1_000_000,
        SmallTextFileCount: 250_000,
        OfficeDocumentCount: 250,
        PdfDocumentCount: 250,
        LargeLogFileCount: 20,
        LargeLogLinesPerFile: 250_000,
        ArchiveCount: 50,
        ArchiveEntriesPerFile: 100,
        UnicodeAndLongPathFileCount: 2_000,
        StoppedIndexerChangeCount: 10_000,
        QueryIterations: 250);

    public static BenchmarkProfile Resolve(string? name) =>
        (name ?? "smoke").Trim().ToLowerInvariant() switch
        {
            "smoke" => Smoke,
            "standard" => Standard,
            "default" => Standard,
            "full" => Full,
            _ => throw new ArgumentException($"Unknown benchmark profile: {name}", nameof(name)),
        };
}
