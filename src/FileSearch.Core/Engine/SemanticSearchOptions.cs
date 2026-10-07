namespace FileSearch.Core.Engine;

/// <summary>Filters semantic similarity scores before fusion with other providers.</summary>
public sealed record SemanticSearchOptions(
    double MinimumScore = SemanticSearchOptions.DefaultMinimumScore,
    int MaximumResults = SemanticSearchOptions.DefaultMaximumResults)
{
    public const double DefaultMinimumScore = 0.60;
    public const int DefaultMaximumResults = 25;

    public static double NormalizeMinimumScore(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : DefaultMinimumScore;

    public static int NormalizeMaximumResults(int value) => Math.Clamp(value, 1, DefaultMaximumResults);
}
