using System;

namespace FileSearch.Core.Workflows;

/// <summary>Which number a <see cref="WorkflowCondition"/> compares.</summary>
public enum ConditionMetric
{
    /// <summary>Total matched lines produced by the source search step.</summary>
    HitCount,

    /// <summary>Distinct files with at least one hit in the source search step.</summary>
    FileCount,
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
}

/// <summary>
/// A comparison over the results of a search step, e.g. "hit count of step
/// 'find-todos' is at least 5". Used by <see cref="IfStep"/> branches and as
/// the exit condition of <see cref="RetryStep"/> loops.
/// </summary>
public sealed record WorkflowCondition
{
    /// <summary>
    /// Id of the search step whose results are measured. Null means the most
    /// recently executed search step.
    /// </summary>
    public string? Source { get; init; }

    public ConditionMetric Metric { get; init; } = ConditionMetric.HitCount;

    public ConditionOperator Operator { get; init; } = ConditionOperator.GreaterOrEqual;

    public long Value { get; init; }

    public bool IsSatisfiedBy(long actual) => Operator switch
    {
        ConditionOperator.Equals => actual == Value,
        ConditionOperator.NotEquals => actual != Value,
        ConditionOperator.GreaterThan => actual > Value,
        ConditionOperator.GreaterOrEqual => actual >= Value,
        ConditionOperator.LessThan => actual < Value,
        ConditionOperator.LessOrEqual => actual <= Value,
        _ => throw new InvalidOperationException($"Unknown condition operator '{Operator}'."),
    };

    /// <summary>Human-readable form for logs, e.g. "hitCount >= 5".</summary>
    public string Describe()
    {
        var op = Operator switch
        {
            ConditionOperator.Equals => "==",
            ConditionOperator.NotEquals => "!=",
            ConditionOperator.GreaterThan => ">",
            ConditionOperator.GreaterOrEqual => ">=",
            ConditionOperator.LessThan => "<",
            ConditionOperator.LessOrEqual => "<=",
            _ => "?",
        };
        var metric = Metric == ConditionMetric.HitCount ? "hitCount" : "fileCount";
        var source = Source is null ? "" : $" of '{Source}'";
        return $"{metric}{source} {op} {Value}";
    }
}
