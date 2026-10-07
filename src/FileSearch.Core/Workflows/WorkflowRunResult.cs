using System;
using System.Collections.Generic;
using FileSearch.Core.Replacement;

namespace FileSearch.Core.Workflows;

public enum WorkflowRunStatus
{
    /// <summary>Ran to the end of the step list.</summary>
    Completed,

    /// <summary>Ended early by a <see cref="StopStep"/>.</summary>
    Stopped,

    /// <summary>A step failed or the workflow was invalid.</summary>
    Failed,

    /// <summary>Cancelled by the host.</summary>
    Cancelled,
}

/// <summary>Per-execution record of one step (loop bodies produce one per iteration).</summary>
public sealed record WorkflowStepOutcome(
    string StepId,
    string StepKind,
    string DisplayName,
    bool Succeeded,
    string? Detail = null,
    long HitCount = 0,
    int FileCount = 0,
    long ChangeCount = 0,
    ReplacementBatchResult? ReplacementBatch = null);

public sealed record WorkflowRunResult
{
    public WorkflowRunStatus Status { get; init; }

    /// <summary>True for a completed run, or a stop step that declared success.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Stop/error message, or null for an ordinary completion.</summary>
    public string? Message { get; init; }

    public IReadOnlyList<WorkflowStepOutcome> StepOutcomes { get; init; } = Array.Empty<WorkflowStepOutcome>();

    /// <summary>Validation problems when <see cref="Status"/> is Failed before any step ran.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public string? RecoveryGroupId { get; init; }
    public IReadOnlyList<ReplacementBatchResult> ReplacementBatches { get; init; } = [];
}

public sealed record WorkflowRunOptions
{
    /// <summary>
    /// Hits buffered per search step for later steps (exports, sub-searches);
    /// counting continues past the cap, buffering stops. 0 means unlimited.
    /// </summary>
    public int MaxBufferedHitsPerStep { get; init; } = 10_000;

    /// <summary>
    /// Hard cap on total step executions (loop iterations included), so a
    /// mis-authored workflow cannot loop forever.
    /// </summary>
    public int MaxStepExecutions { get; init; } = 10_000;

    /// <summary>
    /// Log what file operations and program launches would do instead of
    /// doing it. Searches and replacement previews still run; exports are also previewed.
    /// </summary>
    public bool DryRun { get; init; }
    /// <summary>Explicit authorization for replacement when the host does not provide item review.</summary>
    public bool AllowReplacementApply { get; init; }
    public IReadOnlyList<string> ProtectedReplacementRoots { get; init; } = [];
}
