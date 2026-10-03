using System;
using System.Collections.Generic;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Walker;

public sealed record WalkerOptions
{
    /// <summary>
    /// The default file-size cap shared by every front end (GUI, CLI, core),
    /// so the same query can't return different results per surface. Large
    /// files are rarely useful search targets and whole-file extractors
    /// would balloon on them; users override per search.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Directory names whose entire subtrees are pruned from traversal.
    /// Defaults to folders that are huge and almost never the search target;
    /// pass an empty set to walk everything.
    /// </summary>
    public static IReadOnlySet<string> DefaultExcludeDirectories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "node_modules", "bin", "obj" };

    public IReadOnlyList<string> IncludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> IncludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ExcludeExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> IncludeDirectories { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory names pruned from traversal (see <see cref="DefaultExcludeDirectories"/>).</summary>
    public IReadOnlySet<string> ExcludeDirectories { get; init; } = DefaultExcludeDirectories;
    public bool Recursive { get; init; } = true;
    public bool IncludeHidden { get; init; }

    /// <summary>Files smaller than this are skipped. 0 disables the filter.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>Files larger than this are skipped. 0 disables the filter. The default cap exempts Outlook stores, whose messages are read individually.</summary>
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;

    /// <summary>Exempt PST/OST from the default size cap. Set false when supplying an explicit maximum.</summary>
    public bool AllowLargeMailStores { get; init; } = true;

    internal bool ExceedsSizeLimit(string path, long sizeBytes) =>
        MaxFileSizeBytes > 0 && sizeBytes > MaxFileSizeBytes &&
        !(AllowLargeMailStores && MaxFileSizeBytes == DefaultMaxFileSizeBytes && OutlookMailReader.IsStore(path));

    /// <summary>Only include files modified at or after this UTC time.</summary>
    public DateTime? ModifiedAfterUtc { get; init; }

    /// <summary>Only include files modified at or before this UTC time.</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }

    /// <summary>
    /// Allows extractors to run OCR fallbacks for formats that can also have
    /// native text, such as scanned PDFs.
    /// </summary>
    public bool EnableOcr { get; init; }
}
