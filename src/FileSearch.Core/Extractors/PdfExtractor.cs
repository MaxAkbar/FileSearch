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
    public string ExtractorId => "filesearch.pdf-pdfpig";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".pdf" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // PdfPig parses synchronously, so open on the thread pool. Note that
        // an async iterator still runs between yields on the consumer's
        // thread (Task.Yield resumes on the captured context, it does NOT
        // hop off a UI thread) — callers on a dispatcher must consume this
        // stream from a background task, as FilePreviewService does.
        var document = await Task.Run(() => PdfDocument.Open(path), cancellationToken).ConfigureAwait(false);
        using (document)
        {
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
}
