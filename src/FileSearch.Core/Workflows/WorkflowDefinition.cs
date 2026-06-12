using System;
using System.Collections.Generic;

namespace FileSearch.Core.Workflows;

/// <summary>
/// A saved, user-authored search workflow: a named sequence of steps with
/// structured control flow (conditions, retry loops, for-each loops). The
/// JSON shape of this record is a documented format users can hand-edit and
/// share; see README.Workflows.md at the repository root.
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>Format version written to disk; see <see cref="WorkflowJson.CurrentVersion"/>.</summary>
    public int Version { get; init; } = WorkflowJson.CurrentVersion;

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public IReadOnlyList<WorkflowStep> Steps { get; init; } = Array.Empty<WorkflowStep>();
}
