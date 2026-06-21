using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The multi-tenant isolation floor (PLAN decision #3). On every physical connection open it sets
/// <c>SESSION_CONTEXT('TenantId')</c> as read-only, so SQL Server native RLS predicates filter
/// every query/command for the request's tenant — independent of application code. Because
/// SESSION_CONTEXT is per-connection, this MUST run at connection-open (not per-query): a reused
/// pooled connection would otherwise retain a previous tenant's value (spike 2026-06-21 confirmed).
/// Connections with no bound tenant (the Admin cross-tenant DB principal) are left unset; their RLS
/// policy governs access.
/// </summary>
public sealed class SessionContextConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenant;

    public SessionContextConnectionInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetTenantAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetTenantAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task SetTenantAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!_tenant.HasTenant)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenant, @read_only = 1;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.Value = _tenant.TenantId;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
