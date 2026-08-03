using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orders.Application;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// <see cref="IOrderNoSequence"/> over <c>shop.OrderNoSeq</c> (purchase-flow-completion REQ-7.1). A SQL
/// sequence is the only allocator here that is safe under concurrency without a lock, and EF has no model
/// concept for one — so this is a narrow, single-purpose raw-SQL port (one statement, no predicate, no
/// entity), registered in <c>BypassPrimitiveTests.AllowedPorts</c> as such. The Buddhist-year prefix is
/// formatting only; uniqueness comes entirely from the sequence.
/// </summary>
internal sealed class OrderNoSequence : IOrderNoSequence
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly IClock _clock;

    public OrderNoSequence(MerchantRuntimeDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        // EF wraps any composed SqlQuery in a derived table (SELECT TOP(2) ... FROM (...)), which SQL Server
        // rejects for NEXT VALUE FOR (Msg 11719) — so this one statement goes through ADO directly, enlisted
        // in whatever transaction the caller's unit of work already opened (today no caller opens one; the
        // enlistment is there so the first caller that does is already correct).
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NEXT VALUE FOR shop.OrderNoSeq;";
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            var next = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return Format(_clock.UtcNow, next);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>ORD + Buddhist year mod 100 + the sequence value, zero-padded to 8 (REQ-7.1). A value past
    /// the 8-digit space would render 14+ chars into a varchar(13) column — the column would still refuse it,
    /// but as an opaque truncation error; this names the real problem instead (review PR #168).</summary>
    internal static string Format(DateTime mintedAt, long sequenceValue)
    {
        if (sequenceValue > 99_999_999)
            throw new InvalidOperationException(
                $"OrderNo sequence value {sequenceValue} exceeds the 8-digit ORD number space (REQ-7.1).");

        return $"ORD{(mintedAt.Year + 543) % 100:D2}{sequenceValue:D8}";
    }
}
