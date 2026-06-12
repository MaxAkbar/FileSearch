using System.Collections.Generic;

namespace FileSearch.Core.Workflows;

/// <summary>
/// One entry in the workflow library. <see cref="Error"/> is set when the
/// file exists but cannot be parsed, so the UI can still list it (and offer
/// to open it) instead of hiding it.
/// </summary>
public sealed record WorkflowSummary(
    string FileName,
    string Name,
    string Description,
    string? Error = null);

/// <summary>
/// Persistence for the workflow library: one JSON file per workflow so users
/// can hand-edit, share and version-control individual workflows.
/// </summary>
public interface IWorkflowStore
{
    /// <summary>Folder holding the workflow files; created on first save.</summary>
    string DirectoryPath { get; }

    IReadOnlyList<WorkflowSummary> List();

    WorkflowDefinition? TryLoad(string fileName, out string? error);

    /// <summary>
    /// Saves under <paramref name="fileName"/>, or a name derived from the
    /// workflow's name when null. Returns the file name used.
    /// </summary>
    string Save(WorkflowDefinition workflow, string? fileName = null);

    void Delete(string fileName);

    string GetFullPath(string fileName);
}
