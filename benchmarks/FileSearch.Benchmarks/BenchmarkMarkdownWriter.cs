using System.Globalization;
using System.Text;

namespace FileSearch.Benchmarks;

internal static class BenchmarkMarkdownWriter
{
    public static string Write(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FileSearch Benchmark Results");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Profile: `{report.Profile}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Measured UTC: `{report.MeasuredUtc:O}`");
        builder.AppendLine("- Methodology: deterministic corpus generated locally; metadata-heavy corpus is direct-seeded into the CSharpDB index; physical files exercise content extraction and indexing.");
        builder.AppendLine("- Scope: internal benchmark targets only. Do not use these results to claim superiority over other tools without publishing a reproducible competitor comparison.");
        builder.AppendLine();

        builder.AppendLine("## Targets");
        builder.AppendLine();
        builder.AppendLine("| Target | Initial internal goal |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine("| Warm metadata search P95 | < 50 ms |");
        builder.AppendLine("| Warm lexical search P95 | < 150 ms |");
        builder.AppendLine("| First semantic/content result | < 500 ms |");
        builder.AppendLine("| Search UI keystroke response | < 16 ms |");
        builder.AppendLine("| Index freshness after an event | < 2 s |");
        builder.AppendLine("| No lost changes after restart | 100% in recovery corpus |");
        builder.AppendLine();

        builder.AppendLine("## Metrics");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value | Unit | Notes |");
        builder.AppendLine("| --- | ---: | --- | --- |");
        foreach (var metric in report.Metrics.OrderBy(static metric => metric.Name, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| `{metric.Name}` | {FormatValue(metric.Value)} | {metric.Unit} | {Escape(metric.Notes)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Relevance");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| MRR | {report.Relevance.Mrr:0.####} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| NDCG@10 | {report.Relevance.NdcgAt10:0.####} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Recall@20 | {report.Relevance.RecallAt20:0.####} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Zero-result rate | {report.Relevance.ZeroResultRate:0.####} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Top-result accuracy | {report.Relevance.TopResultAccuracy:0.####} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Query count | {report.Relevance.QueryCount} |");
        builder.AppendLine();

        builder.AppendLine("## External Root Coverage");
        builder.AppendLine();
        builder.AppendLine("| Kind | Available | Path | Strategy |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var root in report.ExternalRoots)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {Escape(root.Kind)} | {root.Available} | {Escape(root.Path ?? "(not configured)")} | {Escape(root.Strategy)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Reproduce");
        builder.AppendLine();
        builder.AppendLine("```powershell");
        builder.AppendLine("dotnet run --project .\\benchmarks\\FileSearch.Benchmarks\\FileSearch.Benchmarks.csproj -- report --profile smoke --force-index");
        builder.AppendLine("dotnet run --project .\\benchmarks\\FileSearch.Benchmarks\\FileSearch.Benchmarks.csproj -- report --profile standard --force-index");
        builder.AppendLine("dotnet run --project .\\benchmarks\\FileSearch.Benchmarks\\FileSearch.Benchmarks.csproj -- report --profile full --force-index");
        builder.AppendLine("```");
        return builder.ToString();
    }

    private static string FormatValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture);

        if (Math.Abs(value) >= 1_000)
            return value.ToString("n2", CultureInfo.InvariantCulture);

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
