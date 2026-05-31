using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts text from PDF documents page-by-page using PdfPig.
/// One <see cref="TextLine"/> per logical line of page text.
/// </summary>
public sealed class PdfExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".pdf" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield(); // hop off the caller's thread before we do sync I/O

        using var document = PdfDocument.Open(path);
        int lineNumber = 0;

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = page.Text;
            if (string.IsNullOrEmpty(text)) continue;

            foreach (var line in text.Split('\n'))
            {
                lineNumber++;
                yield return new TextLine(lineNumber, line.TrimEnd('\r'));
            }
        }
    }
}
