using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts paragraph text from Word .docx documents using OpenXml.
/// One <see cref="TextLine"/> per paragraph, with each paragraph's runs
/// concatenated into a single string.
/// </summary>
public sealed class WordExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".docx" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) yield break;

        int lineNumber = 0;
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrEmpty(text)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, text);
        }
    }
}
