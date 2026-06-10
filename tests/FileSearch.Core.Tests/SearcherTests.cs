using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class SearcherTests : IDisposable
{
    private readonly string _root;
    private readonly ISearcher _searcher;

    public SearcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filesearch-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        _searcher = new Searcher(new FileWalker(), registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<IReadOnlyList<Hit>> SearchAsync(Query query)
    {
        var request = new SearchRequest(query, new[] { _root }, new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var hit in _searcher.SearchAsync(request, CancellationToken.None))
            hits.Add(hit);
        return hits;
    }

    [Fact]
    public async Task FindsMatchingLine_InSingleFile()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"),
            "first line\nsecond line with FOO\nthird line\n");

        var hits = await SearchAsync(new TermQuery("foo"));

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.LineNumber);
        Assert.Contains("FOO", hit.LineContent);
        Assert.Single(hit.Highlights);
    }

    [Fact]
    public async Task FindsMatches_AcrossMultipleFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\nbeta\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "beta\ngamma\n");

        var hits = await SearchAsync(new TermQuery("beta"));

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Path.EndsWith("a.txt"));
        Assert.Contains(hits, h => h.Path.EndsWith("b.txt"));
    }

    [Fact]
    public async Task SkipsBinaryFiles()
    {
        File.WriteAllText(Path.Combine(_root, "text.txt"), "needle\n");
        File.WriteAllBytes(Path.Combine(_root, "binary.txt"), new byte[] { 0x00, 0xFF, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e' });

        var hits = await SearchAsync(new TermQuery("needle"));

        var hit = Assert.Single(hits);
        Assert.EndsWith("text.txt", hit.Path);
    }

    [Fact]
    public async Task RespectsAndQuery_AcrossLines()
    {
        // Line must contain BOTH terms.
        File.WriteAllText(Path.Combine(_root, "a.txt"),
            "has foo only\nhas bar only\nhas foo and bar together\n");

        var hits = await SearchAsync(new AndQuery(new Query[]
        {
            new TermQuery("foo"),
            new TermQuery("bar"),
        }));

        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.LineNumber);
    }

    [Fact]
    public async Task EndToEnd_ParseAndSearch()
    {
        File.WriteAllText(Path.Combine(_root, "code.cs"),
            "public class Foo { }\npublic class Bar { }\nprivate int x = 0;\n");

        var parser = new QueryParser();
        var query = parser.Parse("public AND /class\\s+\\w+/");

        var request = new SearchRequest(query, new[] { _root }, new WalkerOptions());
        var hits = new List<Hit>();
        await foreach (var h in _searcher.SearchAsync(request, CancellationToken.None))
            hits.Add(h);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Contains("public class", h.LineContent));
    }

    [Fact]
    public async Task RespectsMaxHitsPerFileCap()
    {
        File.WriteAllText(
            Path.Combine(_root, "many.txt"),
            string.Concat(Enumerable.Repeat("needle line\n", 5)));

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain }, plain);
        var searcher = new Searcher(new FileWalker(), registry, new SearchOptions { MaxHitsPerFile = 2 });

        var hits = new List<Hit>();
        var request = new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions());
        await foreach (var hit in searcher.SearchAsync(request, CancellationToken.None))
            hits.Add(hit);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task PropagatesCancellationMidStream()
    {
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "needle\n");

        using var cts = new CancellationTokenSource();
        var observed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var request = new SearchRequest(new TermQuery("needle"), new[] { _root }, new WalkerOptions());
            await foreach (var _ in _searcher.SearchAsync(request, cts.Token))
            {
                observed++;
                cts.Cancel();
            }
        });

        Assert.True(observed >= 1);
    }

    [Fact]
    public async Task ContinuesWhenAnExtractorThrows()
    {
        File.WriteAllText(Path.Combine(_root, "bad.boom"), "needle\n");
        File.WriteAllText(Path.Combine(_root, "good.txt"), "needle\n");

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain, new ThrowingExtractor() });
        var searcher = new Searcher(new FileWalker(), registry);
        SearchProgress? progress = null;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions(),
            p => progress = p);

        var hits = new List<Hit>();
        await foreach (var hit in searcher.SearchAsync(request, CancellationToken.None))
            hits.Add(hit);

        var found = Assert.Single(hits);
        Assert.EndsWith("good.txt", found.Path);
        Assert.NotNull(progress);
        Assert.Equal(1, progress.FilesFailed);
    }

    private sealed class ThrowingExtractor : ITextExtractor
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".boom" };

        public async IAsyncEnumerable<TextLine> ExtractAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new IOException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task ReportsProgressMetrics()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "needle\n");
        File.WriteAllText(Path.Combine(_root, "b.bin"), "needle\n");

        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry(new ITextExtractor[] { plain });
        var searcher = new Searcher(new FileWalker(), registry);
        SearchProgress? progress = null;
        var request = new SearchRequest(
            new TermQuery("needle"),
            new[] { _root },
            new WalkerOptions(),
            p => progress = p);

        var hits = new List<Hit>();
        await foreach (var h in searcher.SearchAsync(request, CancellationToken.None))
            hits.Add(h);

        var hit = Assert.Single(hits);
        Assert.EndsWith("a.txt", hit.Path);
        Assert.NotNull(progress);
        Assert.Equal(2, progress.FilesEnumerated);
        Assert.Equal(1, progress.FilesProcessed);
        Assert.Equal(1, progress.FilesMatched);
        Assert.Equal(1, progress.FilesSkipped);
        Assert.Equal(0, progress.FilesFailed);
    }
}
