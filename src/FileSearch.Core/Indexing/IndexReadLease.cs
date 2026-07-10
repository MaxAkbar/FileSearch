using System;
using CSharpDB.Engine;

namespace FileSearch.Core.Indexing;

/// <summary>
/// A borrowed snapshot view of the shared index database: a
/// <see cref="Database.ReaderSession"/> for statement reads (pass
/// <see cref="Session"/> to <see cref="IndexTables"/> via <see cref="DbExec"/>)
/// plus the underlying handle for Database-only operations. Dispose promptly
/// — the shared handle cannot be
/// swapped for external changes or compaction while leases are active.
/// </summary>
internal sealed class IndexReadLease : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal IndexReadLease(Database database, Database.ReaderSession session, long generation, Action release)
    {
        Database = database;
        Session = session;
        Generation = generation;
        _release = release;
    }

    public Database Database { get; }

    public Database.ReaderSession Session { get; }

    public long Generation { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            Session.Dispose();
        }
        finally
        {
            _release();
        }
    }
}
