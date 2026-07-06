using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Benchmarks;

internal static class BenchmarkIndexFactory
{
    public static WalkerOptions IndexOptions { get; } = new()
    {
        ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        MaxFileSizeBytes = 0,
        IncludeHidden = true,
    };

    public static SearchOptions SearchOptions { get; } = new()
    {
        MaxHitsPerFile = 5,
    };

    public static CSharpDbFileIndex Create(BenchmarkPaths paths) =>
        new(
            new FileIndexOptions { DatabasePath = paths.DatabasePath },
            new FileWalker(),
            CreateExtractorRegistry(),
            SearchOptions);

    /// <summary>
    /// Live (non-indexed) searcher over the same extractor set and search
    /// options as the benchmark index, so live and indexed measurements are
    /// comparable.
    /// </summary>
    public static Searcher CreateLiveSearcher() =>
        new(new FileWalker(), CreateExtractorRegistry(), SearchOptions);

    private static ExtractorRegistry CreateExtractorRegistry()
    {
        var plainText = new PlainTextExtractor();
        var extractors = new ITextExtractor[]
        {
            plainText,
            new PdfExtractor(),
            new WordExtractor(),
            new ExcelExtractor(),
            new PowerPointExtractor(),
            new OpenDocumentExtractor(),
            new EpubExtractor(),
            new RtfExtractor(),
            new HtmlExtractor(),
            new EmlExtractor(),
            new XmlTextExtractor(),
            new CalendarContactExtractor(),
            new ZipExtractor(),
        };

        return new ExtractorRegistry(extractors, plainText);
    }
}
