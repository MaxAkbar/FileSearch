namespace FileSearch.Core.Extractors;

public interface IOutOfProcessExtractionService
{
    bool ShouldUse(ITextExtractor extractor);

    Task<OutOfProcessExtractionResult> ExtractAsync(
        string path,
        ITextExtractor extractor,
        CancellationToken cancellationToken);
}

/// <summary>Applies archive member output bounds without changing standalone document extraction.</summary>
public interface IArchiveDocumentExtractionService
{
    bool ShouldUseDocumentMember(ITextExtractor extractor);
    Task<OutOfProcessExtractionResult> ExtractDocumentMemberAsync(
        string path, ITextExtractor extractor, CancellationToken cancellationToken);
}
