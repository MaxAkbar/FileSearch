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

    public static CSharpDbFileIndex Create(BenchmarkPaths paths)
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

        return new CSharpDbFileIndex(
            new FileIndexOptions { DatabasePath = paths.DatabasePath },
            new FileWalker(),
            new ExtractorRegistry(extractors, plainText),
            SearchOptions);
    }
}
