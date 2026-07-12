using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The multi-layer RLS isolation floor (PLAN decision #3, rf1 REQ-4). On every physical connection open
/// it sets <c>SESSION_CONTEXT('MerchantId'/'UserId')</c> as read-only, so SQL Server native RLS
/// predicates filter every query/command for the request's actor — independent of application code.
/// Because SESSION_CONTEXT is per-connection, this MUST run at connection-open (not per-query): a reused
/// pooled connection would otherwise retain a previous actor's value (spike 2026-06-21/2026-07-11
/// confirmed). Both keys are ALWAYS stamped together when an actor is bound — never conditionally — so a
/// pooled reuse can never retain a stale key the current actor didn't set (T13). Connections with no
/// bound actor (e.g. the keyed "admin" registration outside admin-session resolution) are left
/// completely unset; their RLS policy governs access on the unstamped (deny-all) session.
/// </summary>
public sealed class SessionContextConnectionInterceptor : DbConnectionInterceptor
{
    private readonly IActorContext _actor;

    public SessionContextConnectionInterceptor(IActorContext actor) => _actor = actor;

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await StampActorAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        StampActorAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task StampActorAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!_actor.HasActor)
            return; // no stamp at all -> SESSION_CONTEXT empty both keys -> RLS deny-all naturally

        // Sentinel guard (T4): an empty MerchantId with no UserId is neither a valid merchant branch nor a
        // valid platform branch — it must never reach SESSION_CONTEXT. A bound actor in this exact shape
        // is an invariant violation upstream, so fail loudly rather than silently deny.
        if (_actor.MerchantId == Guid.Empty && _actor.UserId is null)
            throw new InvalidOperationException("Bound actor has an empty MerchantId and no UserId; refusing to stamp an empty sentinel.");

        await using var merchantCommand = connection.CreateCommand();
        merchantCommand.CommandText = "EXEC sys.sp_set_session_context @key = N'MerchantId', @value = @merchantId, @read_only = 1;";
        var merchantParameter = merchantCommand.CreateParameter();
        merchantParameter.ParameterName = "@merchantId";
        merchantParameter.Value = _actor.MerchantId;
        merchantCommand.Parameters.Add(merchantParameter);
        await merchantCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Stamped unconditionally alongside MerchantId — NULL explicit when the actor carries no user
        // (e.g. a worker/webhook bind) — so a pooled connection can never retain a previous request's UserId.
        await using var userCommand = connection.CreateCommand();
        userCommand.CommandText = "EXEC sys.sp_set_session_context @key = N'UserId', @value = @userId, @read_only = 1;";
        var userParameter = userCommand.CreateParameter();
        userParameter.ParameterName = "@userId";
        userParameter.Value = (object?)_actor.UserId ?? DBNull.Value;
        userCommand.Parameters.Add(userParameter);
        await userCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
