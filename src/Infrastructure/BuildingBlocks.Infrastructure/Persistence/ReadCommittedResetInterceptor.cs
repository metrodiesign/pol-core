using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>Returns a pooled connection to the pool at READ COMMITTED. SQL Server keeps the session isolation
/// level after a transaction ends and SqlClient pooling does not reset it, so one SERIALIZABLE transaction
/// (first-login JIT, merchant user bootstrap) leaked into the next pooled user of that connection — the outbox
/// and notification dispatchers then failed with "You can only specify the READPAST lock in the READ COMMITTED
/// or REPEATABLE READ isolation levels". A context that started a non-READ COMMITTED transaction is marked, and
/// the reset runs when EF is about to close (return) the connection: no transaction or reader is active then,
/// unlike at commit time where SqlClient still holds the just-completed transaction. One stateless instance is
/// shared so every context keeps the same options fingerprint.</summary>
public sealed class ReadCommittedResetInterceptor : IDbTransactionInterceptor, IDbConnectionInterceptor
{
    public static readonly ReadCommittedResetInterceptor Instance = new();

    private const string ResetSql = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";

    private readonly ConditionalWeakTable<DbContext, object> _dirty = new();

    private ReadCommittedResetInterceptor()
    {
    }

    public DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Mark(eventData.Context, result);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context, result);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (Unmark(eventData.Context) && connection.State == ConnectionState.Open)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ResetSql;
            command.ExecuteNonQuery();
        }
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (Unmark(eventData.Context) && connection.State == ConnectionState.Open)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ResetSql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return result;
    }

    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    private void Mark(DbContext? context, DbTransaction transaction)
    {
        // SQL Server only: the leak is a SqlClient pooling behaviour and the reset statement is T-SQL (SQLite,
        // used by the in-memory architecture tests, rejects it).
        if (context is null
            || transaction.IsolationLevel is IsolationLevel.ReadCommitted or IsolationLevel.Unspecified
            || !string.Equals(context.Database.ProviderName, SqlServerProvider, StringComparison.Ordinal))
            return;
        _dirty.AddOrUpdate(context, Instance);
    }

    private bool Unmark(DbContext? context) => context is not null && _dirty.Remove(context);
}
