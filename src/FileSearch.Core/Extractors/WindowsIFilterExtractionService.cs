namespace FileSearch.Core.Extractors;

public sealed class WindowsIFilterExtractionService : IWindowsIFilterExtractionService
{
    private readonly IOutOfProcessExtractionService _outOfProcessExtraction;
    private readonly ITextExtractor _hostExtractor = new WindowsIFilterHostExtractor();

    public WindowsIFilterExtractionService(IOutOfProcessExtractionService outOfProcessExtraction)
    {
        _outOfProcessExtraction = outOfProcessExtraction;
    }

    public bool CanTryFallback(
        string path,
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount)
    {
        return OperatingSystem.IsWindows() &&
            File.Exists(path) &&
            (primaryExtractor is null || primaryFailure is not null || primaryLineCount == 0) &&
            _outOfProcessExtraction.ShouldUse(_hostExtractor);
    }

    public async Task<WindowsIFilterExtractionResult?> TryExtractAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !_outOfProcessExtraction.ShouldUse(_hostExtractor))
            return null;

        var result = await _outOfProcessExtraction.ExtractAsync(path, _hostExtractor, cancellationToken)
            .ConfigureAwait(false);
        return new WindowsIFilterExtractionResult(result.Lines, result.Issues);
    }

    private sealed class WindowsIFilterHostExtractor : ITextExtractor
    {
        public string ExtractorId => "filesearch.ifilter";

        public string ExtractorVersion => "1";

        public IReadOnlyCollection<string> SupportedExtensions { get; } = Array.Empty<string>();

        public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException("IFilter extraction is only supported by the extractor host process.");
    }
}
