namespace FileSearch.Core.Extractors;

public interface IWindowsIFilterExtractionService
{
    string ExtractorId => "filesearch.ifilter";

    string ExtractorVersion => "1";

    bool CanTryFallback(
        string path,
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount);

    Task<WindowsIFilterExtractionResult?> TryExtractAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed record WindowsIFilterExtractionResult(
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<ExtractionIssue> Issues);
