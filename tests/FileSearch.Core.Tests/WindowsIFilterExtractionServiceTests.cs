using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class WindowsIFilterExtractionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".custom");

    public WindowsIFilterExtractionServiceTests()
    {
        File.WriteAllText(_path, "sample");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public async Task TryExtractAsyncDelegatesToOutOfProcessHost()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = new FakeOutOfProcessExtractionService
        {
            ShouldUseResult = true,
            Result = new OutOfProcessExtractionResult(
                new[] { new TextLine(1, "ifilter text") },
                new[] { new ExtractionIssue(null, "ifilter_empty", "empty") }),
        };
        var service = new WindowsIFilterExtractionService(host);

        Assert.True(service.CanTryFallback(_path, primaryExtractor: null, primaryFailure: null, primaryLineCount: 0));
        var result = await service.TryExtractAsync(_path, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("filesearch.ifilter", host.ExtractorId);
        Assert.Equal("ifilter text", Assert.Single(result.Lines).Content);
        Assert.Equal("ifilter_empty", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void CanTryFallbackReturnsFalseWhenHostCannotRun()
    {
        var service = new WindowsIFilterExtractionService(new FakeOutOfProcessExtractionService());

        Assert.False(service.CanTryFallback(_path, primaryExtractor: null, primaryFailure: null, primaryLineCount: 0));
    }

    private sealed class FakeOutOfProcessExtractionService : IOutOfProcessExtractionService
    {
        public bool ShouldUseResult { get; init; }

        public string? ExtractorId { get; private set; }

        public OutOfProcessExtractionResult Result { get; init; } = new(
            Array.Empty<TextLine>(),
            Array.Empty<ExtractionIssue>());

        public bool ShouldUse(ITextExtractor extractor)
        {
            ExtractorId = extractor.ExtractorId;
            return ShouldUseResult;
        }

        public Task<OutOfProcessExtractionResult> ExtractAsync(
            string path,
            ITextExtractor extractor,
            CancellationToken cancellationToken)
        {
            ExtractorId = extractor.ExtractorId;
            return Task.FromResult(Result);
        }
    }
}
