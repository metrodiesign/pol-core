using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Resolves a webhook's merchant via <c>sec.usp_resolve_webhook_merchant</c> (a proc that runs
/// WITH EXECUTE AS a bypass principal — proven on live SQL Server 2025 to read the mapping row while
/// the calling principal's own direct access stays RLS-blocked). The call runs in a FRESH DI scope so
/// the request's own <see cref="PolDbContext"/> connection is not opened before the merchant is
/// bound — otherwise that connection would be stamped with no <c>SESSION_CONTEXT</c> and reused.
/// </summary>
public sealed class WebhookMerchantResolver : IWebhookMerchantResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WebhookMerchantResolver(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<Guid?> ResolveMerchantAsync(Guid pspConnectionId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PolDbContext>();

        var merchantIds = await db.Database
            .SqlQueryRaw<Guid>(
                "EXEC sec.usp_resolve_webhook_merchant @PspConnectionId = {0}",
                pspConnectionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return merchantIds.Count > 0 ? merchantIds[0] : null;
    }
}
