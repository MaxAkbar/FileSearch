using System;

namespace FileSearch.Core.Indexing;

public enum IndexFailureExportFormat
{
    Csv,
    Json,
}

public sealed record IndexFailureInfo(
    string Root,
    string Path,
    string ExtractorId,
    string ExtractorVersion,
    string Error,
    long ExtractionAttemptCount,
    DateTime? LastAttemptUtc,
    string? MemberPath = null,
    string FailureKind = "file_error",
    string? IssueCode = null,
    string? Severity = null)
{
    public long RetryCount => Math.Max(0, ExtractionAttemptCount - 1);
}
