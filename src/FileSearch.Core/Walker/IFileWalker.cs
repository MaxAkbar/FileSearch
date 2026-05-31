using System.Collections.Generic;
using System.Threading;

namespace FileSearch.Core.Walker;

/// <summary>
/// Yields file paths under one or more root directories.
/// </summary>
public interface IFileWalker
{
    IEnumerable<string> Enumerate(
        IEnumerable<string> roots,
        WalkerOptions options,
        CancellationToken cancellationToken);
}
