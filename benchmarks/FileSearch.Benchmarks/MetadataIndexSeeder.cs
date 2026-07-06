using CSharpDB.Engine;
using CSharpDB.Primitives;
using FileSearch.Core.Indexing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Benchmarks;

internal sealed partial class MetadataIndexSeeder
{
    private const int FileBatchSize = 2_000;
    private const int TokenBatchFlushSize = 16_384;

    public async Task EnsureSeededAsync(BenchmarkPaths paths, CorpusManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.MetadataRoot);

        using (var index = BenchmarkIndexFactory.Create(paths))
        {
            await index.BuildOrRefreshAsync(
                    new IndexRequest(paths.MetadataRoot, BenchmarkIndexFactory.IndexOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        using var database = new IndexDatabase(new FileIndexOptions { DatabasePath = paths.DatabasePath }, NullLogger.Instance);
        await database.RunExclusiveWriteAsync(
                db => SeedMetadataRowsAsync(db, paths, manifest, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task SeedMetadataRowsAsync(
        Database db,
        BenchmarkPaths paths,
        CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var root = IndexPath.NormalizeRoot(paths.MetadataRoot);
        var rootRow = await IndexTables.GetRootAsync(db, root, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Metadata root was not initialized: {root}");

        var existingCount = await IndexTables.CountOkFilesAsync(db, rootRow.Id, cancellationToken).ConfigureAwait(false);
        if (existingCount >= manifest.MetadataOnlyEntryCount)
            return;

        await IndexTables.DeleteFilesForRootAsync(db, rootRow.Id, cancellationToken).ConfigureAwait(false);

        var firstFileId = await IndexTables.AllocateIdsAsync(
                db,
                "files",
                manifest.MetadataOnlyEntryCount,
                cancellationToken)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow.Ticks;
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var fileBatch = db.PrepareInsertBatch("files", FileBatchSize);
        var tokenBatch = IndexTables.PrepareMetadataTokenBatch(db);

        for (var i = 0; i < manifest.MetadataOnlyEntryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileId = firstFileId + i;
            var fileName = i == 42 ? "metadata_target_000042.cs" : $"metadata_file_{i:D7}.cs";
            var directory = Path.Combine(paths.MetadataRoot, "virtual", $"bucket_{i % 1_024:D4}");
            var path = Path.Combine(directory, fileName);
            var pathLower = path.ToLowerInvariant();
            var directoryLower = directory.ToLowerInvariant();
            var fileNameLower = fileName.ToLowerInvariant();
            var modified = created + TimeSpan.TicksPerMinute * (i % 500_000);

            fileBatch.AddRow(
                DbValue.FromInteger(fileId),
                DbValue.FromInteger(rootRow.Id),
                DbValue.FromText(path),
                DbValue.FromText(pathLower),
                DbValue.FromText(directory),
                DbValue.FromText(directoryLower),
                DbValue.FromText(fileName),
                DbValue.FromText(fileNameLower),
                DbValue.FromText(".cs"),
                DbValue.FromInteger(1_024 + i % 16_384),
                DbValue.FromInteger(created),
                DbValue.FromInteger(modified),
                DbValue.FromInteger((long)FileAttributes.Archive),
                DbValue.FromText(FileTypeCategory.ForExtension(".cs")),
                DbValue.FromInteger(now),
                DbValue.FromText(FileStatus.Ok),
                DbValue.FromText(string.Empty),
                DbValue.FromInteger(0),
                DbValue.FromText(string.Empty),
                DbValue.FromText(string.Empty),
                DbValue.FromInteger(0),
                DbValue.FromText(IndexContentVersion.Current),
                DbValue.FromInteger(i % 251 == 0 ? i % 7 : 0),
                DbValue.FromInteger(i % 251 == 0 ? modified : 0),
                DbValue.FromText("filesearch.benchmark-metadata"),
                DbValue.FromText("1"),
                DbValue.FromInteger(1),
                DbValue.FromInteger(now));
            IndexTables.AddMetadataTokens(
                tokenBatch,
                rootRow.Id,
                fileId,
                IndexTables.BuildIndexedMetadataTokens(
                    path,
                    directory,
                    fileName,
                    ".cs",
                    FileTypeCategory.ForExtension(".cs")));

            if (fileBatch.Count >= FileBatchSize)
            {
                await fileBatch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                fileBatch.Clear();
            }

            if (tokenBatch.Count >= TokenBatchFlushSize)
                await IndexTables.FlushMetadataTokenBatchAsync(tokenBatch, cancellationToken).ConfigureAwait(false);
        }

        if (fileBatch.Count > 0)
            await fileBatch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        await IndexTables.FlushMetadataTokenBatchAsync(tokenBatch, cancellationToken).ConfigureAwait(false);

        await IndexTables.MarkRootRefreshedAsync(
                db,
                rootRow.Id,
                rootRow.OptionsHash,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
