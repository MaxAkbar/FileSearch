using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Extractors;

public sealed record ExtractionIssue(
    string? MemberPath,
    string Code,
    string Message,
    string Severity = "warning");

public interface IExtractionIssueSink
{
    void Report(ExtractionIssue issue);
}

public interface IDiagnosticTextExtractor : ITextExtractor
{
    IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        IExtractionIssueSink issues,
        CancellationToken cancellationToken);
}

public interface IContextualDiagnosticTextExtractor : IDiagnosticTextExtractor, IContextualTextExtractor
{
    IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        IExtractionIssueSink issues,
        CancellationToken cancellationToken);
}

public sealed class NullExtractionIssueSink : IExtractionIssueSink
{
    public static NullExtractionIssueSink Instance { get; } = new();

    private NullExtractionIssueSink()
    {
    }

    public void Report(ExtractionIssue issue)
    {
    }
}

public sealed class ListExtractionIssueSink : IExtractionIssueSink
{
    private readonly List<ExtractionIssue> _issues = new();

    public IReadOnlyList<ExtractionIssue> Issues => _issues;

    public void Report(ExtractionIssue issue)
    {
        _issues.Add(issue);
    }
}
