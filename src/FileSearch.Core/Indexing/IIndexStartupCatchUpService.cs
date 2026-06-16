using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

public interface IIndexStartupCatchUpService
{
    Task<IndexStartupCatchUpResult> CatchUpAsync(
        IReadOnlyCollection<IndexedLocation> locations,
        CancellationToken cancellationToken);
}

public sealed record IndexStartupCatchUpResult(
    IReadOnlySet<string> HandledRoots,
    IReadOnlyDictionary<string, string> FallbackReasons)
{
    public static IndexStartupCatchUpResult Empty { get; } =
        new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
