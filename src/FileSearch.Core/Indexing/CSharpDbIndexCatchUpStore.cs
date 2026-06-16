using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Indexing;

internal sealed class CSharpDbIndexCatchUpStore : IIndexCatchUpStore
{
    private readonly CSharpDbFileIndex? _index;

    public CSharpDbIndexCatchUpStore(IFileIndex index) => _index = index as CSharpDbFileIndex;

    public Task<IndexVolumeCheckpoint?> GetVolumeCheckpointAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken) =>
        _index is null
            ? Task.FromResult<IndexVolumeCheckpoint?>(null)
            : _index.GetVolumeCheckpointCoreAsync(volume, cancellationToken);

    public Task DeleteFileByIdentityAsync(
        string volumeKey,
        string fileReferenceNumber,
        CancellationToken cancellationToken) =>
        _index?.DeleteFileByIdentityCoreAsync(volumeKey, fileReferenceNumber, cancellationToken) ?? Task.CompletedTask;

    public Task UpdateVolumeCheckpointAsync(
        IndexVolumeInfo volume,
        ulong journalId,
        long lastCommittedUsn,
        string health,
        string? error,
        CancellationToken cancellationToken) =>
        _index?.UpdateVolumeCheckpointCoreAsync(
            volume,
            journalId,
            lastCommittedUsn,
            health,
            error,
            cancellationToken) ?? Task.CompletedTask;
}
