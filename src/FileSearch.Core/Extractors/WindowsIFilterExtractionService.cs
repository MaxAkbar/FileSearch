namespace FileSearch.Core.Extractors;

public sealed class WindowsIFilterExtractionService : IWindowsIFilterExtractionService
{
    private readonly IOutOfProcessExtractionService _outOfProcessExtraction;
    private readonly WindowsIFilterExtractionOptions _options;
    private readonly ITextExtractor _hostExtractor = new WindowsIFilterHostExtractor();

    public WindowsIFilterExtractionService(
        IOutOfProcessExtractionService outOfProcessExtraction,
        WindowsIFilterExtractionOptions? options = null)
    {
        _outOfProcessExtraction = outOfProcessExtraction;
        _options = options ?? new WindowsIFilterExtractionOptions();
    }

    public bool CanTryFallback(
        string path,
        ITextExtractor? primaryExtractor,
        Exception? primaryFailure,
        long primaryLineCount)
    {
        return _options.Enabled &&
            OperatingSystem.IsWindows() &&
            File.Exists(path) &&
            _options.AllowsPath(path) &&
            (primaryExtractor is null || primaryFailure is not null || primaryLineCount == 0) &&
            _outOfProcessExtraction.ShouldUse(_hostExtractor);
    }

    public async Task<WindowsIFilterExtractionResult?> TryExtractAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled ||
            !OperatingSystem.IsWindows() ||
            !_options.AllowsPath(path) ||
            !_outOfProcessExtraction.ShouldUse(_hostExtractor))
        {
            return null;
        }

        var result = await _outOfProcessExtraction.ExtractAsync(path, _hostExtractor, cancellationToken)
            .ConfigureAwait(false);
        return new WindowsIFilterExtractionResult(result.Lines, result.Issues);
    }

    private sealed class WindowsIFilterHostExtractor : ITextExtractor
    {
        public string ExtractorId => "filesearch.ifilter";

        public string ExtractorVersion => "2";

        public IReadOnlyCollection<string> SupportedExtensions { get; } = Array.Empty<string>();

        public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException("IFilter extraction is only supported by the extractor host process.");
    }
}
