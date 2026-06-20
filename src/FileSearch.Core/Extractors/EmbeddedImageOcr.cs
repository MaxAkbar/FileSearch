using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Extractors;

public sealed record EmbeddedImageOcrRequest(
    SourceAnchorKind AnchorKind,
    string MemberPath,
    string Label,
    int? Page = null,
    string? Section = null,
    string? Sheet = null);

public interface IEmbeddedImageOcrService
{
    IAsyncEnumerable<TextLine> ExtractAsync(
        byte[] imageBytes,
        EmbeddedImageOcrRequest request,
        CancellationToken cancellationToken);
}

public sealed class NullEmbeddedImageOcrService : IEmbeddedImageOcrService
{
    public async IAsyncEnumerable<TextLine> ExtractAsync(
        byte[] imageBytes,
        EmbeddedImageOcrRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
