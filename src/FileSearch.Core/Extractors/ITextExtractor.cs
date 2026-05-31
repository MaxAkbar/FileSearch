using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Pulls text content out of a file, one line at a time. Implementations may
/// stream from a plain text file, or unpack and parse structured formats
/// (PDF, DOCX, XLSX, ZIP...). The async-enumerable shape lets a large file
/// be processed without buffering the whole document.
/// </summary>
public interface ITextExtractor
{
    /// <summary>
    /// File extensions (including the leading dot, lowercase) this extractor
    /// supports. Used by <see cref="IExtractorRegistry"/> to dispatch.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken);
}
