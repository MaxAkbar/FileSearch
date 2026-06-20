using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Extractors;

public sealed record TextExtractionContext(bool EnableOcr = false);

public interface IContextualTextExtractor : ITextExtractor
{
    IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        CancellationToken cancellationToken);
}

public static class TextExtractorContextExtensions
{
    public static IAsyncEnumerable<TextLine> ExtractWithContextAsync(
        this ITextExtractor extractor,
        string path,
        TextExtractionContext context,
        CancellationToken cancellationToken) =>
        extractor is IContextualTextExtractor contextual
            ? contextual.ExtractAsync(path, context, cancellationToken)
            : extractor.ExtractAsync(path, cancellationToken);
}
