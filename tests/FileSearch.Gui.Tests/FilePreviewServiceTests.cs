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
