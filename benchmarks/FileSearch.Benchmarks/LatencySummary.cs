namespace FileSearch.Benchmarks;

internal sealed record LatencySummary(
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds)
{
    public static LatencySummary From(IReadOnlyList<double> milliseconds)
    {
        if (milliseconds.Count == 0)
            return new LatencySummary(0, 0, 0);

        var sorted = milliseconds.OrderBy(value => value).ToArray();
        return new LatencySummary(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
            return sorted[0];

        var index = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return sorted[lower];

        var fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
