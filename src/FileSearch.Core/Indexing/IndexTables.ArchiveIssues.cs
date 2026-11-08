using CSharpDB.Engine;

namespace FileSearch.Core.Indexing;

internal static partial class IndexTables
{
    public static async Task<IReadOnlyList<long>> ReadArchiveCoverageIssueFileIdsAsync(
        DbExec db, long rootId, CancellationToken cancellationToken)
    {
        var archiveIds = new HashSet<long>();
        var ids = new HashSet<long>();
        await using (var result = await db.ExecuteAsync(
            Sql.Format($"SELECT id, extension, status FROM files WHERE root_id = {rootId}"),
            cancellationToken).ConfigureAwait(false))
        {
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = result.Current;
                if (!string.Equals(row[1].AsText, ".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                string status = row[2].AsText;
                if (string.Equals(status, FileStatus.Indexing, StringComparison.OrdinalIgnoreCase) || IsStaleStatus(status))
                    continue;
                long id = row[0].AsInteger;
                archiveIds.Add(id);
                if (string.Equals(status, FileStatus.Error, StringComparison.OrdinalIgnoreCase))
                    ids.Add(id);
            }
        }

        foreach (var batch in archiveIds.Chunk(500))
        {
            await using var result = await db.ExecuteAsync(
                Sql.Format($"SELECT file_id, severity FROM extraction_issues WHERE file_id IN ({new Sql.IdList(batch)})"),
                cancellationToken).ConfigureAwait(false);
            while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(result.Current[1].AsText, "info", StringComparison.OrdinalIgnoreCase))
                    ids.Add(result.Current[0].AsInteger);
            }
        }
        return ids.OrderBy(id => id).ToArray();
    }
}
