using FileSearch.Core.Extractors;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class PdfExtractorTests
{
    [Fact]
    public void SupportedExtensions_IncludesPdf()
    {
        var extractor = new PdfExtractor();
        Assert.Contains(".pdf", extractor.SupportedExtensions);
    }

    // Note: content-level PDF tests require a real PDF fixture file.
    // The simplest way to verify end-to-end PDF extraction is to drop a
    // .pdf into a test folder and run the full Searcher integration test
    // against it — see SearcherTests for the pattern.
}
