using System.Collections.Generic;
using System.IO;
using System.Threading;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Workflows;

/// <summary>
/// An <see cref="IFileWalker"/> over an explicit file list instead of a
/// directory tree — the seam that lets a workflow sub-search run the regular
/// <see cref="Engine.Searcher"/> pipeline over the files a previous step
/// matched. Roots and walker options are ignored: the set is already fixed;
/// only files deleted since the previous step are skipped.
/// </summary>
internal sealed class FixedListFileWalker : IFileWalker
{
    private readonly IReadOnlyList<string> _files;

    public FixedListFileWalker(IReadOnlyList<string> files) => _files = files;

    public IEnumerable<string> Enumerate(
        IEnumerable<string> roots,
        WalkerOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var file in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file))
                yield return file;
        }
    }
}
