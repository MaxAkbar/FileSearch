namespace FileSearch.Core.Extractors;

public interface IOutOfProcessExtractionService
{
    bool ShouldUse(ITextExtractor extractor);

    Task<OutOfProcessExtractionResult> ExtractAsync(
        string path,
        ITextExtractor extractor,
        CancellationToken cancellationToken);
}
