using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts visible text from HTML files by removing markup, comments, scripts, and styles.
/// </summary>
public sealed class HtmlExtractor : ITextExtractor
{
    public string ExtractorId => "filesearch.html";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".html", ".htm" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var text = MarkupText.FromHtml(content);
        if (!string.IsNullOrEmpty(text))
            yield return new TextLine(1, text);
    }
}
