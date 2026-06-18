using System;

namespace FileSearch.Core.Indexing;

public enum IndexValidationStatus
{
    Passed,
    DriftDetected,
    MissingIndex,
    Unavailable,
    Failed,
}

public sealed record IndexValidationResult(
    string Root,
    IndexValidationStatus Status,
    DateTime CheckedUtc,
    long FilesChecked,
    long FilesMatched,
    long MissingFromIndex,
    long ChangedSinceIndex,
    long MissingFromDisk,
    long FailedChecks,
    string Message)
{
    public bool HasDrift =>
        MissingFromIndex > 0 ||
        ChangedSinceIndex > 0 ||
        MissingFromDisk > 0 ||
        FailedChecks > 0;

    public static IndexValidationResult Create(
        string root,
        DateTime checkedUtc,
        long filesChecked,
        long filesMatched,
        long missingFromIndex,
        long changedSinceIndex,
        long missingFromDisk,
        long failedChecks)
    {
        var hasDrift =
            missingFromIndex > 0 ||
            changedSinceIndex > 0 ||
            missingFromDisk > 0 ||
            failedChecks > 0;
        var message = hasDrift
            ? $"Drift detected: {missingFromIndex:n0} missing, {changedSinceIndex:n0} changed, {missingFromDisk:n0} removed, {failedChecks:n0} failed checks."
            : $"Validated {filesChecked:n0} files with no drift.";

        return new IndexValidationResult(
            root,
            hasDrift ? IndexValidationStatus.DriftDetected : IndexValidationStatus.Passed,
            checkedUtc,
            filesChecked,
            filesMatched,
            missingFromIndex,
            changedSinceIndex,
            missingFromDisk,
            failedChecks,
            message);
    }

    public static IndexValidationResult MissingIndex(string root, DateTime checkedUtc) =>
        new(
            root,
            IndexValidationStatus.MissingIndex,
            checkedUtc,
            0,
            0,
            0,
            0,
            0,
            0,
            "No index row exists for this root.");

    public static IndexValidationResult Unavailable(string root, DateTime checkedUtc, string message) =>
        new(
            root,
            IndexValidationStatus.Unavailable,
            checkedUtc,
            0,
            0,
            0,
            0,
            0,
            0,
            message);

    public static IndexValidationResult Failed(string root, DateTime checkedUtc, string message) =>
        new(
            root,
            IndexValidationStatus.Failed,
            checkedUtc,
            0,
            0,
            0,
            0,
            0,
            1,
            message);
}
