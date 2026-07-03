namespace FileSearch.Mcp;

// Wire shapes for tool results. These mirror the CLI's one-shot automation
// DTOs (search hit, index location, index stats, index failure) so the two
// machine surfaces stay in lockstep; MCP-only additions are the truncation
// flags and the coverage/server sections.

internal sealed record SearchResultDocument(
    string Query,
    string Mode,
    string Target,
    string Route,
    IReadOnlyList<string> Roots,
    int TotalMatches,
    int Returned,
    bool Truncated,
    bool TimedOut,
    double ElapsedSeconds,
    IReadOnlyList<string> Status,
    IReadOnlyList<SearchHitDocument> Hits,
    IReadOnlyList<IndexCoverageDocument>? Coverage = null);

internal sealed record SearchHitDocument(
    string Path,
    int LineNumber,
    string Line,
    bool LineTruncated,
    string Kind,
    string Route,
    double Score,
    long? SizeBytes,
    DateTime? ModifiedUtc,
    string? Anchor);

internal sealed record IndexCoverageDocument(
    string Root,
    bool Covered,
    string Status,
    string Message);

internal sealed record ExtractResultDocument(
    string Path,
    string Extractor,
    int StartLine,
    int Returned,
    bool Truncated,
    bool TimedOut,
    int? NextStartLine,
    IReadOnlyList<ExtractedLineDocument> Lines);

internal sealed record ExtractedLineDocument(
    int N,
    string Text,
    string? Anchor = null);

internal sealed record IndexStatusDocument(
    IndexDatabaseDocument Database,
    IReadOnlyList<IndexLocationDocument> Locations,
    int HiddenLocationCount,
    IndexRootStatsDocument? RootStats,
    ServerInfoDocument Server);

internal sealed record IndexDatabaseDocument(
    string Path,
    bool Exists,
    bool IsCompatible,
    string SchemaVersion,
    long TotalBytes,
    int LocationCount,
    long TotalFileCount,
    long TotalLineCount,
    int PendingChangeCount,
    long FailedFileCount,
    DateTime? LastIndexedUtc);

internal sealed record IndexLocationDocument(
    string Root,
    bool Exists,
    long FileCount,
    long LineCount,
    DateTime? IndexedUtc,
    string? LastValidationStatus,
    bool? OcrEnabled,
    IReadOnlyList<string>? ExcludedDirectories,
    IReadOnlyList<string>? ExcludedExtensions);

internal sealed record IndexRootStatsDocument(
    string Root,
    bool Exists,
    long FileCount,
    long LineCount,
    DateTime? IndexedUtc);

internal sealed record ServerInfoDocument(
    string Version,
    bool ReadOnly,
    bool AllowAnyRoot,
    IReadOnlyList<string>? AllowedRoots,
    IReadOnlyList<string> SupportedExtensions);

internal sealed record IndexFailuresDocument(
    int Total,
    int Returned,
    IReadOnlyList<IndexFailureDocument> Failures);

internal sealed record IndexFailureDocument(
    string Root,
    string Path,
    string? MemberPath,
    string FailureKind,
    string? IssueCode,
    string? Severity,
    string ExtractorId,
    string ExtractorVersion,
    long ExtractionAttemptCount,
    long RetryCount,
    DateTime? LastAttemptUtc,
    string Error);
