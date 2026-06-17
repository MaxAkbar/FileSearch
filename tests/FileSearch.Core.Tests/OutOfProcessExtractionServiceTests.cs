using System.Text.Json;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class OutOfProcessExtractionServiceTests : IDisposable
{
    private readonly string _hostPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");

    public void Dispose()
    {
        try { File.Delete(_hostPath); } catch { }
    }

    [Fact]
    public void ShouldUseReturnsFalseWhenHostIsMissing()
    {
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath },
            new FakeHostRunner());

        Assert.False(service.ShouldUse(new PdfExtractor()));
    }

    [Fact]
    public async Task ExtractAsyncSendsRequestAndReturnsLinesAndIssues()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var runner = new FakeHostRunner
        {
            Result = new ExtractorHostRunResult(
                ExitCode: 0,
                StandardOutput: JsonSerializer.Serialize(
                    ExtractorHostResponse.Ok(
                        new[] { new TextLine(3, "hosted needle") },
                        new[] { new ExtractionIssue("archive.bin", "archive_member_unsupported_type", "skipped") }),
                    ExtractorHostProtocol.JsonOptions),
                StandardError: string.Empty,
                TimedOut: false),
        };
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath },
            runner);

        var result = await service.ExtractAsync(@"C:\docs\file.pdf", new PdfExtractor(), TestContext.Current.CancellationToken);

        Assert.True(service.ShouldUse(new PdfExtractor()));
        Assert.Equal(_hostPath, runner.Command?.DisplayPath);
        var request = JsonSerializer.Deserialize<ExtractorHostRequest>(
            runner.RequestJson!,
            ExtractorHostProtocol.JsonOptions);
        Assert.Equal(@"C:\docs\file.pdf", request?.Path);
        Assert.Equal("filesearch.pdf-pdfpig", request?.ExtractorId);
        Assert.Equal("hosted needle", Assert.Single(result.Lines).Content);
        Assert.Equal("archive_member_unsupported_type", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task ExtractAsyncReportsTimeoutAsHostException()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath },
            new FakeHostRunner
            {
                Result = new ExtractorHostRunResult(-1, string.Empty, string.Empty, TimedOut: true),
            });

        var ex = await Assert.ThrowsAsync<ExtractorHostException>(
            () => service.ExtractAsync(@"C:\docs\file.pdf", new PdfExtractor(), TestContext.Current.CancellationToken));

        Assert.Equal("extractor_host_timeout", ex.Code);
    }

    [Fact]
    public async Task ExtractAsyncReportsCrashWhenNoValidResponseIsReturned()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath },
            new FakeHostRunner
            {
                Result = new ExtractorHostRunResult(42, string.Empty, "boom", TimedOut: false),
            });

        var ex = await Assert.ThrowsAsync<ExtractorHostException>(
            () => service.ExtractAsync(@"C:\docs\file.pdf", new PdfExtractor(), TestContext.Current.CancellationToken));

        Assert.Equal("extractor_host_crashed", ex.Code);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task ExtractAsyncReportsHostFailureCodeFromResponse()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath },
            new FakeHostRunner
            {
                Result = new ExtractorHostRunResult(
                    1,
                    JsonSerializer.Serialize(
                        ExtractorHostResponse.Fail("extractor_failed", "bad document"),
                        ExtractorHostProtocol.JsonOptions),
                    string.Empty,
                    TimedOut: false),
            });

        var ex = await Assert.ThrowsAsync<ExtractorHostException>(
            () => service.ExtractAsync(@"C:\docs\file.pdf", new PdfExtractor(), TestContext.Current.CancellationToken));

        Assert.Equal("extractor_failed", ex.Code);
        Assert.Equal("bad document", ex.Message);
    }

    private sealed class FakeHostRunner : IExtractorHostProcessRunner
    {
        public ExtractorHostCommand? Command { get; private set; }

        public string? RequestJson { get; private set; }

        public ExtractorHostRunResult Result { get; init; } = new(
            ExitCode: 0,
            StandardOutput: JsonSerializer.Serialize(
                ExtractorHostResponse.Ok(Array.Empty<TextLine>(), Array.Empty<ExtractionIssue>()),
                ExtractorHostProtocol.JsonOptions),
            StandardError: string.Empty,
            TimedOut: false);

        public Task<ExtractorHostRunResult> RunAsync(
            ExtractorHostCommand command,
            string requestJson,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Command = command;
            RequestJson = requestJson;
            return Task.FromResult(Result);
        }
    }
}
