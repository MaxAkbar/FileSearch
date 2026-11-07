using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

public sealed record EligibleIndexedFile(long Id, string Path, long SizeBytes, long ModifiedUtcTicks);

/// <summary>Resolves current indexed files satisfying the entire request before retrieval applies a result cap.</summary>
public interface IIndexedFileEligibility
{
    Task<IReadOnlyList<EligibleIndexedFile>> GetEligibleFilesAsync(SearchRequest request, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<EligibleIndexedFile>>(new NotSupportedException("The configured index cannot resolve Smart Search file restrictions."));
}
