namespace FileSearch.Core.Workflows;

public enum ExportFormat
{
    Json,
    Csv,
    Markdown,
}

/// <summary>
/// Writes the results of a search step (or, with no source, all search steps
/// run so far) to a file. <see cref="Path"/> supports <c>${variable}</c>
/// substitution.
/// </summary>
public sealed record ExportStep : WorkflowStep
{
    /// <summary>Search step whose results to export; null means all search steps so far.</summary>
    public string? SourceStepId { get; init; }

    public ExportFormat Format { get; init; } = ExportFormat.Json;

    public string Path { get; init; } = "";

    public bool Overwrite { get; init; } = true;
}

public enum FileOperationKind
{
    Copy,
    Move,
}

public enum FileCollisionPolicy
{
    /// <summary>Leave the existing file and skip the source.</summary>
    Skip,

    /// <summary>Replace the existing file.</summary>
    Overwrite,

    /// <summary>Write alongside with a " (n)" suffix.</summary>
    Rename,
}

/// <summary>
/// Copies or moves the distinct files matched by a search step into a folder.
/// The runner asks the host for confirmation (see
/// <see cref="IWorkflowInteraction"/>) before touching the filesystem.
/// </summary>
public sealed record FileOperationStep : WorkflowStep
{
    public FileOperationKind Operation { get; init; } = FileOperationKind.Copy;

    /// <summary>Search step whose matched files to operate on; null means the most recent one.</summary>
    public string? SourceStepId { get; init; }

    /// <summary>Destination folder; created if missing. Supports <c>${variable}</c> substitution.</summary>
    public string DestinationDirectory { get; init; } = "";

    public FileCollisionPolicy Collision { get; init; } = FileCollisionPolicy.Rename;
}

/// <summary>
/// Launches an external program, either once or once per distinct file matched
/// by a search step (with <c>${file}</c> available in the arguments). The
/// runner asks the host for confirmation before launching anything.
/// </summary>
public sealed record RunProgramStep : WorkflowStep
{
    /// <summary>Executable to launch. Supports <c>${variable}</c> substitution.</summary>
    public string Program { get; init; } = "";

    /// <summary>Argument string. Supports <c>${variable}</c> substitution (e.g. <c>${file}</c>).</summary>
    public string Arguments { get; init; } = "";

    /// <summary>Run once per distinct matched file instead of once overall.</summary>
    public bool PerFile { get; init; }

    /// <summary>Search step supplying files for <see cref="PerFile"/>; null means the most recent one.</summary>
    public string? SourceStepId { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Wait for the program to exit and record its exit code.</summary>
    public bool WaitForExit { get; init; } = true;

    /// <summary>Per-launch wait limit when <see cref="WaitForExit"/> is set; 0 waits indefinitely.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Safety cap on per-file launches; 0 means unlimited.</summary>
    public int MaxFiles { get; init; } = 100;
}
