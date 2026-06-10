using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

public sealed class IndexCoverageService
{
    private readonly IIndexSearch _index;

    public IndexCoverageService(IIndexSearch index)
    {
        _index = index;
    }

    public Task<IndexCoverage> GetCoverageAsync(
        SearchRequest request,
        CancellationToken cancellationToken) =>
        _index.GetCoverageAsync(request, cancellationToken);
}
