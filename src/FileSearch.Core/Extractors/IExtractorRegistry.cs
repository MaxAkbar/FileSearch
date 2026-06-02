using System.Collections.Generic;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Resolves the appropriate <see cref="ITextExtractor"/> for a given path,
/// based on its extension. Adding a new file format is a matter of
/// implementing <see cref="ITextExtractor"/> and registering it; no other
/// code needs to change (OCP).
/// </summary>
public interface IExtractorRegistry
{
    IReadOnlySet<string> SupportedExtensions { get; }

    ITextExtractor? GetFor(string path);
}
