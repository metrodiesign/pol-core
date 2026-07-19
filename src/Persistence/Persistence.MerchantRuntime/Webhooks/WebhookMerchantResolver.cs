using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Persistence.MerchantRuntime.Webhooks;

/// <summary>
/// Resolves a webhook's merchant by reading <c>txn.PspConnections</c> directly — rls-to-query-filter
/// task 8 dropped <c>sec.usp_resolve_webhook_merchant</c> along with the rest of the RLS apparatus (the
/// proc's WITH EXECUTE AS bypass had no more RLS to bypass once the security policy was torn down and
/// the runtime collapsed to one DB principal). The call runs in a FRESH DI scope so the request's own
/// <see cref="MerchantRuntimeDbContext"/> connection is not opened before the merchant is bound —
/// otherwise that connection would be reused pre-bind.
/// </summary>
internal sealed class WebhookMerchantResolver : IWebhookMerchantResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WebhookMerchantResolver(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<Guid?> ResolveMerchantAsync(Guid pspConnectionId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantRuntimeDbContext>();

        var merchantIds = await db.Database
            .SqlQueryRaw<Guid>(
                "SELECT TOP 1 MerchantId AS [Value] FROM txn.PspConnections WHERE Id = {0}",
                pspConnectionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return merchantIds.Count > 0 ? merchantIds[0] : null;
    }
}
