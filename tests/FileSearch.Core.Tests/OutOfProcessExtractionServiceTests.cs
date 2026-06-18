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
            new OutOfProcessExtractionOptions { HostPath = _hostPath, UseReusableHostPool = false },
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
            new OutOfProcessExtractionOptions { HostPath = _hostPath, UseReusableHostPool = false },
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
            new OutOfProcessExtractionOptions { HostPath = _hostPath, UseReusableHostPool = false },
            new FakeHostRunner
            {
                Result = new ExtractorHostRunResult(-1, string.Empty, string.Empty, TimedOut: true),
            });

        var ex = await Assert.ThrowsAsync<ExtractorHostException>(
            () => service.ExtractAsync(@"C:\docs\file.pdf", new PdfExtractor(), TestContext.Current.CancellationToken));

        Assert.Equal("extractor_host_timeout", ex.Code);
    }

    [Fact]
    public async Task ExtractAsyncUsesExtractorSpecificTimeout()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var runner = new FakeHostRunner();
        var options = new OutOfProcessExtractionOptions
        {
            HostPath = _hostPath,
            Timeout = TimeSpan.FromSeconds(30),
            UseReusableHostPool = false,
        };
        options.ExtractorTimeouts["filesearch.ifilter"] = TimeSpan.FromSeconds(7);
        var service = new OutOfProcessExtractionService(options, runner);

        await service.ExtractAsync(@"C:\docs\file.pdf", new TestExtractor("filesearch.ifilter"), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(7), runner.Timeout.GetValueOrDefault());
    }

    [Fact]
    public async Task ExtractAsyncReportsCrashWhenNoValidResponseIsReturned()
    {
        File.WriteAllText(_hostPath, string.Empty);
        var service = new OutOfProcessExtractionService(
            new OutOfProcessExtractionOptions { HostPath = _hostPath, UseReusableHostPool = false },
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
            new OutOfProcessExtractionOptions { HostPath = _hostPath, UseReusableHostPool = false },
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

    [Fact]
    public async Task ExtractAsyncCanUseReusableHostPool()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "first needle\nsecond needle\n", TestContext.Current.CancellationToken);
        try
        {
            var options = new OutOfProcessExtractionOptions
            {
                HostPath = ResolveBuiltHostPath(),
                HostPoolSize = 1,
                Timeout = TimeSpan.FromSeconds(10),
            };
            options.ExtractorIds.Add("filesearch.plain-text");
            using var service = new OutOfProcessExtractionService(options);
            var extractor = new PlainTextExtractor();

            var first = await service.ExtractAsync(file, extractor, TestContext.Current.CancellationToken);
            var second = await service.ExtractAsync(file, extractor, TestContext.Current.CancellationToken);

            Assert.True(service.ShouldUse(extractor));
            Assert.Contains(first.Lines, line => line.Content == "first needle");
            Assert.Contains(second.Lines, line => line.Content == "second needle");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static string ResolveBuiltHostPath()
    {
        var repositoryRoot = FindAncestorContaining(AppContext.BaseDirectory, "FileSearch.slnx");
        if (repositoryRoot is null)
            throw new DirectoryNotFoundException("Could not find repository root for extractor host test.");

        var configuration = typeof(OutOfProcessExtractionServiceTests)
            .Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .FirstOrDefault()
            ?.Configuration ?? "Debug";
        var hostDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "FileSearch.ExtractorHost",
            "bin",
            configuration,
            "net10.0");
        var executableName = OperatingSystem.IsWindows()
            ? "FileSearch.ExtractorHost.exe"
            : "FileSearch.ExtractorHost";
        var executablePath = Path.Combine(hostDirectory, executableName);
        if (File.Exists(executablePath))
            return executablePath;

        var dllPath = Path.Combine(hostDirectory, "FileSearch.ExtractorHost.dll");
        if (File.Exists(dllPath))
            return dllPath;

        throw new FileNotFoundException("Built extractor host was not found.", executablePath);
    }

    private static string? FindAncestorContaining(string startDirectory, string fileName)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, fileName)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class FakeHostRunner : IExtractorHostProcessRunner
    {
        public ExtractorHostCommand? Command { get; private set; }

        public string? RequestJson { get; private set; }

        public TimeSpan? Timeout { get; private set; }

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
            Timeout = timeout;
            return Task.FromResult(Result);
        }
    }

    private sealed class TestExtractor(string extractorId) : ITextExtractor
    {
        public string ExtractorId { get; } = extractorId;

        public IReadOnlyCollection<string> SupportedExtensions { get; } = Array.Empty<string>();

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
