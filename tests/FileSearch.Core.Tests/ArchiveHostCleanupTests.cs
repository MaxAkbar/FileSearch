using System.Text.Json;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Tests;

public sealed class ArchiveHostCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentRemovesHostCreatedScratchFilesAfterSuccessOrTimeout(bool timeout)
    {
        string hostPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(hostPath, string.Empty);
        try
        {
            var runner = new ScratchRunner(timeout);
            using var service = new OutOfProcessExtractionService(new OutOfProcessExtractionOptions
            {
                HostPath = hostPath,
                UseReusableHostPool = false,
            }, runner);
            if (timeout)
                await Assert.ThrowsAsync<ExtractorHostException>(() => service.ExtractAsync("unused.zip", new ZipExtractor(), Token));
            else
                await service.ExtractAsync("unused.zip", new ZipExtractor(), Token);
            Assert.True(runner.CreatedScratchFile);
            Assert.NotNull(runner.DirectoryPath);
            Assert.False(Directory.Exists(runner.DirectoryPath));
        }
        finally { File.Delete(hostPath); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTerminatedHostCannotLeaveItsArchiveScratchFiles(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        string hostPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(hostPath, string.Empty);
        try
        {
            var runner = new TerminatedChildRunner(cancel);
            using var service = new OutOfProcessExtractionService(new OutOfProcessExtractionOptions
            {
                HostPath = hostPath,
                UseReusableHostPool = false,
                Timeout = TimeSpan.FromSeconds(5),
            }, runner);
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractAsync("unused.zip", new ZipExtractor(), Token));
            else
            {
                var exception = await Assert.ThrowsAsync<ExtractorHostException>(() => service.ExtractAsync("unused.zip", new ZipExtractor(), Token));
                Assert.Equal("extractor_host_timeout", exception.Code);
            }
            Assert.True(runner.CreatedScratchFile);
            Assert.False(Directory.Exists(runner.DirectoryPath));
        }
        finally { File.Delete(hostPath); }
    }

    [Fact]
    public async Task ScratchValidationRejectsTheRootAndUnrelatedDirectories()
    {
        await using var scratch = ExtractionScratchDirectory.Create();
        Assert.Equal(scratch.Path, ExtractionScratchDirectory.ValidateRequestDirectory(scratch.Path));
        Assert.Throws<InvalidDataException>(() => ExtractionScratchDirectory.ValidateRequestDirectory(Path.GetDirectoryName(scratch.Path)));
        Assert.Throws<InvalidDataException>(() => ExtractionScratchDirectory.ValidateRequestDirectory(Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => ExtractionScratchDirectory.ValidateRequestDirectory(null));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ScratchRunner(bool timeout) : IExtractorHostProcessRunner
    {
        public string? DirectoryPath { get; private set; }
        public bool CreatedScratchFile { get; private set; }
        public Task<ExtractorHostRunResult> RunAsync(ExtractorHostCommand command, string requestJson,
            TimeSpan deadline, CancellationToken cancellationToken)
        {
            var request = JsonSerializer.Deserialize<ExtractorHostRequest>(requestJson, ExtractorHostProtocol.JsonOptions)!;
            DirectoryPath = ExtractionScratchDirectory.ValidateRequestDirectory(request.TemporaryDirectory);
            File.WriteAllText(Path.Combine(DirectoryPath, "orphan.tmp"), "parser scratch");
            CreatedScratchFile = true;
            return Task.FromResult(new ExtractorHostRunResult(timeout ? -1 : 0,
                timeout ? string.Empty : JsonSerializer.Serialize(ExtractorHostResponse.Ok([], []), ExtractorHostProtocol.JsonOptions),
                string.Empty, timeout));
        }
    }

    private sealed class TerminatedChildRunner(bool cancel) : IExtractorHostProcessRunner
    {
        public string? DirectoryPath { get; private set; }
        public bool CreatedScratchFile { get; private set; }
        public async Task<ExtractorHostRunResult> RunAsync(ExtractorHostCommand command, string requestJson,
            TimeSpan deadline, CancellationToken cancellationToken)
        {
            var request = JsonSerializer.Deserialize<ExtractorHostRequest>(requestJson, ExtractorHostProtocol.JsonOptions)!;
            DirectoryPath = ExtractionScratchDirectory.ValidateRequestDirectory(request.TemporaryDirectory);
            string marker = Path.Combine(DirectoryPath, "orphan.tmp");
            const string script = "$request = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
                "[IO.File]::WriteAllText((Join-Path $request.temporaryDirectory 'orphan.tmp'), 'scratch'); " +
                "Start-Sleep -Seconds 30";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = new DefaultExtractorHostProcessRunner().RunAsync(
                new ExtractorHostCommand("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], "cleanup test child"),
                requestJson, deadline, linked.Token);
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(marker) && !run.IsCompleted && started.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20, cancellationToken);
            CreatedScratchFile = File.Exists(marker);
            if (cancel) await linked.CancelAsync();
            return await run;
        }
    }
}
