using System.Text.Json.Serialization;

namespace FileSearch.Benchmarks;

internal sealed record CorpusManifest(
    string Profile,
    int Seed,
    DateTime GeneratedUtc,
    string ContentRoot,
    string MetadataRoot,
    int MetadataOnlyEntryCount,
    int PhysicalFileCount,
    IReadOnlyList<BenchmarkQuery> Queries,
    IReadOnlyList<ExternalRootProbe> ExternalRoots);

internal sealed record BenchmarkQuery(
    string Name,
    string Query,
    string RootKind,
    IReadOnlyList<string> RelevantPaths,
    string? ExpectedTopPath = null);

internal sealed record ExternalRootProbe(
    string Kind,
    string? Path,
    bool Available,
    string Strategy);

internal sealed record BenchmarkReport(
    string Profile,
    DateTime MeasuredUtc,
    IReadOnlyList<BenchmarkMetric> Metrics,
    RelevanceSummary Relevance,
    IReadOnlyList<ExternalRootProbe> ExternalRoots);

internal sealed record BenchmarkMetric(string Name, double Value, string Unit, string Notes = "");

internal sealed record RelevanceSummary(
    double Mrr,
    double NdcgAt10,
    double RecallAt20,
    double ZeroResultRate,
    double TopResultAccuracy,
    int QueryCount);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CorpusManifest))]
[JsonSerializable(typeof(BenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
