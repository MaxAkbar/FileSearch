using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts visible slide and notes text from PowerPoint .pptx decks using OpenXml.
/// </summary>
public sealed class PowerPointExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".pptx" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        using var document = PresentationDocument.Open(path, isEditable: false);
        var presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<SlideId>().ToList();
        if (presentationPart is null || slideIds is null) yield break;

        int lineNumber = 0;
        int slideNumber = 0;

        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;

            if (slideId.RelationshipId is null) continue;
            if (presentationPart.GetPartById(slideId.RelationshipId!) is not SlidePart slidePart) continue;

            foreach (var text in ExtractTextBlocks(slidePart.Slide?.Descendants<A.Text>()))
            {
                lineNumber++;
                yield return new TextLine(lineNumber, $"[Slide {slideNumber}] {text}");
            }

            var notesPart = slidePart.NotesSlidePart;
            foreach (var text in ExtractTextBlocks(notesPart?.NotesSlide?.Descendants<A.Text>()))
            {
                lineNumber++;
                yield return new TextLine(lineNumber, $"[Slide {slideNumber} Notes] {text}");
            }
        }
    }

    private static IEnumerable<string> ExtractTextBlocks(IEnumerable<A.Text>? textElements)
    {
        if (textElements is null) yield break;

        foreach (var text in textElements.Select(t => MarkupText.Normalize(t.Text)))
        {
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }
}
