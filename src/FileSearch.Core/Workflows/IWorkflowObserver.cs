using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Receives live progress while a workflow runs. Callbacks arrive from
/// background threads; UI hosts must marshal to their dispatcher.
/// </summary>
public interface IWorkflowObserver
{
    /// <param name="depth">Nesting depth (0 = top level), for indented display.</param>
    void OnStepStarted(WorkflowStep step, int depth);

    void OnStepCompleted(WorkflowStepOutcome outcome);

    /// <summary>A hit produced by a search step, streamed as it is found.</summary>
    void OnHit(SearchStep step, Hit hit);

    /// <summary>Free-form progress line for the run log.</summary>
    void OnLog(string message);
}

/// <summary>
/// Host-side confirmation for steps with side effects (file operations,
/// launching programs). A null interaction means headless consent — the user
/// authored the step, so unattended runs proceed; interactive hosts should
/// always supply one.
/// </summary>
public interface IWorkflowInteraction
{
    Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken);
}

/// <summary>What a side-effecting step is about to do, for a confirmation prompt.</summary>
/// <param name="Title">Short summary, e.g. "Copy 12 files".</param>
/// <param name="Description">One-line detail, e.g. the destination folder.</param>
/// <param name="Details">Sample of affected items (paths, command lines).</param>
public sealed record WorkflowConfirmation(
    string Title,
    string Description,
    IReadOnlyList<string> Details);
