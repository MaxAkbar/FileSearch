using System;
using System.Threading;
using System.Threading.Tasks;
using CSharpDB.Engine;
using CSharpDB.Execution;
using CSharpDB.Sql;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Statement executor that abstracts over the three CSharpDB execution
/// surfaces: a raw <see cref="Database"/> handle (writes, engine-serialized),
/// a snapshot <see cref="Database.ReaderSession"/> (reads that must not block
/// on or race the writer), and a <see cref="WriteTransaction"/> (batched
/// writes). Implicit conversions let existing <see cref="IndexTables"/> call
/// sites pass any of them without churn.
/// </summary>
internal readonly struct DbExec
{
    private readonly Database? _database;
    private readonly Database.ReaderSession? _session;
    private readonly WriteTransaction? _transaction;

    private DbExec(Database? database, Database.ReaderSession? session, WriteTransaction? transaction)
    {
        _database = database;
        _session = session;
        _transaction = transaction;
    }

    public static implicit operator DbExec(Database database) => new(database, null, null);

    public static implicit operator DbExec(Database.ReaderSession session) => new(null, session, null);

    public static implicit operator DbExec(WriteTransaction transaction) => new(null, null, transaction);

    public ValueTask<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        if (_database is not null)
            return _database.ExecuteAsync(sql, cancellationToken);
        if (_session is not null)
            return _session.ExecuteReadAsync(sql, cancellationToken);
        if (_transaction is not null)
            return _transaction.ExecuteAsync(sql, cancellationToken);

        throw new InvalidOperationException("DbExec has no execution target.");
    }

    public ValueTask<QueryResult> ExecuteAsync(Statement statement, CancellationToken cancellationToken)
    {
        if (_database is not null)
            return _database.ExecuteAsync(statement, cancellationToken);
        if (_session is not null)
            return _session.ExecuteReadAsync(statement, cancellationToken);
        if (_transaction is not null)
            return _transaction.ExecuteAsync(statement, cancellationToken);

        throw new InvalidOperationException("DbExec has no execution target.");
    }
}
