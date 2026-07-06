using System;
using System.IO;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Thrown when an index write session completed its work but the final
/// checkpoint could not persist it (typically because concurrent readers held
/// the database file for the whole retry window). The write is NOT durable;
/// callers must keep their pending-change record so the work can be retried.
/// </summary>
public sealed class IndexWritePersistenceException : IOException
{
    public IndexWritePersistenceException()
        : base("Index write session could not be persisted (checkpoint failed).")
    {
    }

    public IndexWritePersistenceException(string message)
        : base(message)
    {
    }

    public IndexWritePersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static IndexWritePersistenceException ForDatabase(string databasePath, Exception? lastFailure) =>
        lastFailure is null
            ? new IndexWritePersistenceException(
                $"Index write session could not be persisted (checkpoint failed) for '{databasePath}'. The change stays pending and will be retried.")
            : new IndexWritePersistenceException(
                $"Index write session could not be persisted (checkpoint failed) for '{databasePath}'. The change stays pending and will be retried.",
                lastFailure);
}
