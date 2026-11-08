using System.Text.Json.Serialization;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Benchmarks;

internal sealed record BaselineTask(string Id, string Query, string? ExpectedFile,
    QueryMode Mode = QueryMode.PlainText, SearchTarget Target = SearchTarget.Content,
    int? Page = null, string? Member = null, long? DatabaseRow = null, string? DatabaseColumn = null);
internal sealed record CorpusFile(string Path, long Bytes, string Sha256);
internal sealed record TaskManifest(int Version, string Provenance, string Profile, string ContentRoot,
    int DatabaseRows, IReadOnlyList<BaselineTask> Tasks, IReadOnlyList<CorpusFile> Files);
internal sealed record TaskSample(string Task, string Route, int Iteration, string Condition, bool Success,
    int Hits, double? FirstAnyMs, double? FirstUsefulMs, double CompletionMs, long AllocatedBytes,
    long WorkingSetBytes, string? Error, IReadOnlyList<string> Status);
internal sealed record Percentiles(int Count, double? P50, double? P95, double? P99)
{
    public static Percentiles From(IEnumerable<double?> values)
    {
        var sorted = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        double? At(double p) => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, At(.5), At(.95), At(.99));
    }
}
internal sealed record TaskSummary(string Task, string Route, int Samples, int Successes,
    Percentiles FirstUsefulMs, Percentiles CompletionMs, double? CompletionP95GoalMs, bool? MeetsGoal);
internal sealed record DatabaseSample(string Engine, int Iteration, double OpenSnapshotAndSchemaMs,
    double FirstPageMs, double DeepPageMs, double FirstPageIncludingOpenMs, long DeepFirstRow, bool Success, string? Error);
internal sealed record CancellationSample(string Route, string Condition, bool Observed, double AcknowledgeMs, string? Error);
internal sealed record TaskWorker(string Task, string Route, int RequestedSamples, int CompletedSamples, bool Success, bool TimedOut, string? Error);
internal sealed record BaselineEnvironment(string Os, string Runtime, string Architecture, string Cpu,
    int LogicalProcessors, long AvailableMemoryBytes, string StorageKind, string FileSystem,
    string CoreVersion, string CSharpDbVersion, string SqliteVersion, string GitHead, string GitStatus, string BuildConfiguration);
internal sealed record TaskBaselineReport(DateTime StartedUtc, DateTime FinishedUtc, BaselineEnvironment Environment,
    string Condition, string ManifestPath, double? InitialIndexMs, long IndexBytes, long IndexedFiles,
    long FailedFiles, double RefreshToSearchableMs, bool RefreshSuccess, bool SourceBytesUnchanged,
    long PeakWorkingSetBytes, IReadOnlyList<TaskSample> Samples, IReadOnlyList<TaskSummary> Summaries,
    IReadOnlyList<TaskWorker> Workers,
    IReadOnlyList<DatabaseSample> DatabaseSamples, IReadOnlyList<CancellationSample> Cancellation,
    IReadOnlyList<string> Unmeasured);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TaskManifest))]
[JsonSerializable(typeof(TaskBaselineReport))]
internal sealed partial class TaskBaselineJsonContext : JsonSerializerContext;
