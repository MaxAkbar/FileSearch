namespace FileSearch.Core.Databases;

public sealed partial class DatabaseReadSession
{
    // Keys remain native Int64 values from this private snapshot. Display keys and
    // formatted/truncated cell values are never used to construct continuation SQL.
    private const int MaxPageBookmarks = 64;
    private readonly Dictionary<(string Table, long FirstRow), long> _pageBookmarks = [];
    private readonly Queue<(string Table, long FirstRow)> _pageBookmarkOrder = [];

    private long? PageBookmark(DatabaseTable table, long firstRow) =>
        _pageBookmarks.TryGetValue((table.Name, firstRow), out long key) ? key : null;

    private void RememberPage(DatabaseTable table, long firstRow, PageReadResult result)
    {
        if (!result.Page.HasMore || result.LastKey is not { } key || result.Page.Rows.Count == 0) return;
        long nextRow = firstRow + result.Page.Rows.Count;
        if (nextRow > DatabaseFiles.MaxRows) return;
        var address = (table.Name, nextRow);
        if (!_pageBookmarks.ContainsKey(address))
        {
            if (_pageBookmarks.Count == MaxPageBookmarks) _pageBookmarks.Remove(_pageBookmarkOrder.Dequeue());
            _pageBookmarkOrder.Enqueue(address);
        }
        _pageBookmarks[address] = key;
    }

    private void ClearPageBookmarks()
    {
        _pageBookmarks.Clear();
        _pageBookmarkOrder.Clear();
    }

    private static int IntegerPrimaryKeyColumn(DatabaseTable table)
    {
        int key = -1;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            if (!table.Columns[index].IsPrimaryKey) continue;
            if (key >= 0 || table.Columns[index].Type != nameof(CSharpDB.Primitives.DbType.Integer)) return -1;
            key = index;
        }
        return key;
    }

    private sealed record PageReadResult(DatabasePage Page, long? LastKey);
}
