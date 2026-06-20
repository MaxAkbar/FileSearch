using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FileSearch.Core.Extractors;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class FilePreviewServiceTests : IDisposable
{
    private readonly string _path;

    public FilePreviewServiceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".docx");
        File.WriteAllText(_path, "raw document bytes that should not appear in preview");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public async Task LoadHitsPreviewAsync_UsesRegisteredExtractorForDocumentFiles()
    {
        var extractor = new StubExtractor(new[]
        {
            new TextLine(1, "First paragraph."),
            new TextLine(2, "Second paragraph has needle."),
            new TextLine(3, "Third paragraph."),
        });
        var registry = new ExtractorRegistry(new[] { extractor });
        var service = new FilePreviewService(registry);

        var preview = await service.LoadHitsPreviewAsync(
            _path,
            new[] { 2 },
            contextLines: 1,
            CancellationToken.None);

        Assert.Contains("First paragraph.", preview);
        Assert.Contains("►      2  Second paragraph has needle.", preview);
        Assert.Contains("Third paragraph.", preview);
        Assert.DoesNotContain("raw document bytes", preview);
    }

    [Fact]
    public async Task LoadHitsPreviewAsync_IncludesSourceAnchorWhenPresent()
    {
        var extractor = new StubExtractor(new[]
        {
            new TextLine(1, "Screenshot title."),
            new TextLine(2, "Screenshot needle.", SourceAnchor.ImageOcrRegion(10, 20, 30, 40, 100, 200)),
        });
        var registry = new ExtractorRegistry(new[] { extractor });
        var service = new FilePreviewService(registry);

        var preview = await service.LoadHitsPreviewAsync(
            _path,
            new[] { 2 },
            contextLines: 0,
            CancellationToken.None);

        Assert.Contains("►      2  Screenshot needle.", preview);
        Assert.Contains("OCR region x10 y20 30x40 of 100x200", preview);
    }

    [Fact]
    public async Task ExtractionNeverRunsOnCallerSynchronizationContext()
    {
        // Simulates the WPF dispatcher: if any part of the extraction loop
        // executes while the caller's SynchronizationContext is current, a
        // PDF/Excel parse would be running on the UI thread.
        var observed = new List<SynchronizationContext?>();
        var extractor = new ContextRecordingExtractor(observed);
        var registry = new ExtractorRegistry(new[] { extractor });
        var service = new FilePreviewService(registry);

        var previous = SynchronizationContext.Current;
        var uiLikeContext = new SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiLikeContext);
        try
        {
            await service.LoadHitsPreviewAsync(_path, new[] { 1 }, contextLines: 1, CancellationToken.None);
            await service.LoadFullTextAsync(_path, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.NotEmpty(observed);
        Assert.DoesNotContain(uiLikeContext, observed);
    }

    private sealed class ContextRecordingExtractor : ITextExtractor
    {
        private readonly List<SynchronizationContext?> _observed;

        public ContextRecordingExtractor(List<SynchronizationContext?> observed)
        {
            _observed = observed;
        }

        public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".docx" };

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // The segment before the first await runs synchronously on the
            // consumer's thread — exactly where PdfDocument.Open would run.
            lock (_observed)
                _observed.Add(SynchronizationContext.Current);

            await Task.Delay(1, cancellationToken);
            yield return new TextLine(1, "alpha");

            lock (_observed)
                _observed.Add(SynchronizationContext.Current);

            yield return new TextLine(2, "beta");
        }
    }

    private sealed class StubExtractor : ITextExtractor
    {
        private readonly IReadOnlyList<TextLine> _lines;

        public StubExtractor(IReadOnlyList<TextLine> lines)
        {
            _lines = lines;
        }

        public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".docx" };

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var line in _lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }
        }
    }
}
