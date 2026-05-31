using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts visible text from XML-based content files where markup is usually noise.
/// </summary>
public sealed class XmlTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".svg", ".xaml", ".resx" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var text = MarkupText.FromXml(content);
        if (!string.IsNullOrEmpty(text))
            yield return new TextLine(1, text);
    }
}
