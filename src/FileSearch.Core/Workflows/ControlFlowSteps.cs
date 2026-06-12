using System;
using System.Collections.Generic;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Branches on a condition over earlier search results: runs <see cref="Then"/>
/// when the condition holds, otherwise <see cref="Else"/>.
/// </summary>
public sealed record IfStep : WorkflowStep
{
    public WorkflowCondition Condition { get; init; } = new();

    public IReadOnlyList<WorkflowStep> Then { get; init; } = Array.Empty<WorkflowStep>();

    public IReadOnlyList<WorkflowStep> Else { get; init; } = Array.Empty<WorkflowStep>();
}

/// <summary>
/// Repeats <see cref="Body"/> until <see cref="Until"/> is satisfied or the
/// iteration limit is reached — the "broaden and try again" loop. Each
/// iteration exposes <c>${iteration}</c> (1-based) to the body; when
/// <see cref="ParameterSets"/> is non-empty, iteration <c>i</c> additionally
/// exposes the variables of the <c>i</c>-th set, so successive iterations can
/// relax the query, widen roots, etc.
/// </summary>
public sealed record RetryStep : WorkflowStep
{
    public IReadOnlyList<WorkflowStep> Body { get; init; } = Array.Empty<WorkflowStep>();

    /// <summary>Exit condition, checked after each iteration.</summary>
    public WorkflowCondition Until { get; init; } = new();

    /// <summary>Upper bound on iterations regardless of the condition.</summary>
    public int MaxIterations { get; init; } = 5;

    /// <summary>
    /// Optional per-iteration variables. When non-empty, the loop runs at most
    /// one iteration per set (still capped by <see cref="MaxIterations"/>).
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ParameterSets { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, string>>();
}

/// <summary>
/// Runs <see cref="Body"/> once per distinct file matched by the source search
/// step, exposing <c>${file}</c>, <c>${fileName}</c> and <c>${directory}</c>
/// to the body — e.g. "for each config file found, sub-search it for secrets".
/// </summary>
public sealed record ForEachStep : WorkflowStep
{
    /// <summary>Search step whose matched files to iterate; null means the most recent one.</summary>
    public string? SourceStepId { get; init; }

    public IReadOnlyList<WorkflowStep> Body { get; init; } = Array.Empty<WorkflowStep>();

    /// <summary>Safety cap on iterated files; 0 means unlimited.</summary>
    public int MaxItems { get; init; } = 100;
}

/// <summary>
/// Ends the workflow immediately — typically inside an <see cref="IfStep"/>
/// branch ("if nothing found, stop with a message").
/// </summary>
public sealed record StopStep : WorkflowStep
{
    /// <summary>Whether the run counts as successful.</summary>
    public bool Succeeded { get; init; } = true;

    public string? Message { get; init; }
}
