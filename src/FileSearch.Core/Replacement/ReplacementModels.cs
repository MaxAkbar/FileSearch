using FileSearch.Core.Walker;

namespace FileSearch.Core.Replacement;

public enum ReplacementTarget { Contents, Names }
public enum ReplacementNameTarget { Files, Folders, Both }

public sealed record ReplacementRequest(
    IReadOnlyList<string> Roots,
    WalkerOptions WalkerOptions,
    string Find,
    string ReplaceWith,
    ReplacementTarget Target = ReplacementTarget.Contents,
    bool UseRegex = false,
    bool MatchCase = false,
    ReplacementNameTarget NameTarget = ReplacementNameTarget.Both,
    bool IncludeExtensions = false,
    bool IncludeFormulas = false,
    IReadOnlyList<string>? ProtectedRoots = null,
    IReadOnlySet<string>? AdditionalTextExtensions = null,
    IReadOnlyList<string>? SourcePaths = null,
    string? RecoveryGroupId = null,
    string? RecoveryGroupName = null);

public sealed record ReplacementChange(string Location, string Before, string After);

public sealed record ReplacementItem(
    string Id,
    string Path,
    bool IsDirectory,
    string Fingerprint,
    string Identity,
    int ChangeCount,
    IReadOnlyList<ReplacementChange> Changes,
    string? NewPath = null,
    string? SkipReason = null)
{
    public bool CanApply => SkipReason is null && ChangeCount > 0;
}

public sealed record ReplacementPlan(ReplacementRequest Request, IReadOnlyList<ReplacementItem> Items);
public sealed record ReplacementOutcome(string Path, bool Succeeded, string Message, string? NewPath = null, bool IsDirectory = false);
public sealed record ReplacementRecoveryGroup(string Id, string Name, IReadOnlyList<string> BatchIds)
{
    public long ChangeCount { get; init; }
    public int SkippedItemCount { get; init; }
}
public sealed record ReplacementBatchResult(string? BatchId, IReadOnlyList<ReplacementOutcome> Outcomes, bool Cancelled = false)
{
    public int SucceededCount => Outcomes.Count(item => item.Succeeded);
}

public sealed record ReplacementOptions
{
    public string BackupDirectory { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSearch", "ReplacementBackups");
}

public interface IReplacementService
{
    Task<ReplacementPlan> PreviewAsync(ReplacementRequest request, CancellationToken cancellationToken);
    Task<ReplacementBatchResult> ApplyAsync(ReplacementPlan plan, IReadOnlySet<string> checkedItemIds, CancellationToken cancellationToken);
    Task<ReplacementBatchResult> UndoAsync(CancellationToken cancellationToken);
    Task<bool> CanUndoAsync(CancellationToken cancellationToken);
    Task<ReplacementRecoveryGroup?> GetLastRecoveryGroupAsync(CancellationToken cancellationToken);
    Task<ReplacementBatchResult> UndoGroupAsync(string groupId, CancellationToken cancellationToken);
}
