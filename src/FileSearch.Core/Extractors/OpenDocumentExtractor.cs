using System.Collections.Generic;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts text from OpenDocument files (.odt, .ods, .odp) by reading content.xml.
/// </summary>
public sealed class OpenDocumentExtractor : ITextExtractor
{
    public string ExtractorId => "filesearch.opendocument";

    public string ExtractorVersion => "1";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".odt", ".ods", ".odp" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml");
        if (entry is null) yield break;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var text = MarkupText.FromXml(content);
        if (string.IsNullOrEmpty(text)) yield break;

        yield return new TextLine(1, text);
    }
}
