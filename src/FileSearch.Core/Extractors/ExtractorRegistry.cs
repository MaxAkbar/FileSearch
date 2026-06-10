using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileSearch.Core.Extractors;

public sealed class ExtractorRegistry : IExtractorRegistry
{
    private readonly Dictionary<string, ITextExtractor> _byExtension;
    private readonly ITextExtractor? _fallback;

    public IReadOnlySet<string> SupportedExtensions => _byExtension.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <param name="extractors">All registered extractors.</param>
    /// <param name="fallback">
    /// Optional extractor used when no extension match is found. Pass the
    /// plain-text extractor here to attempt reading unknown extensions as text.
    /// </param>
    public ExtractorRegistry(IEnumerable<ITextExtractor> extractors, ITextExtractor? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _fallback = fallback;
        _byExtension = new Dictionary<string, ITextExtractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in extractors)
            foreach (var ext in extractor.SupportedExtensions)
                _byExtension[ext] = extractor;
    }

    public ITextExtractor? GetFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && _byExtension.TryGetValue(ext, out var hit))
            return hit;
        return _fallback;
    }
}
