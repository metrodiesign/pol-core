using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Resolves a webhook's tenant via <c>VCentralPay.usp_resolve_webhook_tenant</c> (a proc that runs
/// WITH EXECUTE AS a bypass principal — proven on live SQL Server 2025 to read the mapping row while
/// the calling principal's own direct access stays RLS-blocked). The call runs in a FRESH DI scope so
/// the request's own <see cref="ProducerDbContext"/> connection is not opened before the tenant is
/// bound — otherwise that connection would be stamped with no <c>SESSION_CONTEXT</c> and reused.
/// </summary>
public sealed class WebhookTenantResolver : IWebhookTenantResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WebhookTenantResolver(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<Guid?> ResolveTenantAsync(Guid pspConnectionId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProducerDbContext>();

        var tenantIds = await db.Database
            .SqlQueryRaw<Guid>(
                "EXEC VCentralPay.usp_resolve_webhook_tenant @PspConnectionId = {0}",
                pspConnectionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenantIds.Count > 0 ? tenantIds[0] : null;
    }
}
