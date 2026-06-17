using System.Text.Json;

namespace FileSearch.Core.Extractors;

public static class ExtractorHostProtocol
{
    public const int CurrentVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record ExtractorHostRequest(
    int ProtocolVersion,
    string Path,
    string ExtractorId);

public sealed record ExtractorHostResponse(
    int ProtocolVersion,
    bool Success,
    TextLine[] Lines,
    ExtractionIssue[] Issues,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ExtractorHostResponse Ok(
        IReadOnlyCollection<TextLine> lines,
        IReadOnlyCollection<ExtractionIssue> issues) =>
        new(
            ExtractorHostProtocol.CurrentVersion,
            Success: true,
            lines.ToArray(),
            issues.ToArray(),
            ErrorCode: null,
            ErrorMessage: null);

    public static ExtractorHostResponse Fail(string code, string message) =>
        new(
            ExtractorHostProtocol.CurrentVersion,
            Success: false,
            Lines: Array.Empty<TextLine>(),
            Issues: Array.Empty<ExtractionIssue>(),
            ErrorCode: code,
            ErrorMessage: message);
}

public sealed record OutOfProcessExtractionResult(
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<ExtractionIssue> Issues);
