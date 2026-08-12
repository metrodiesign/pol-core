using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminPaymentSessionReader(
    MerchantRuntimeDbContext db,
    DefaultPspSelection defaultPsp,
    IPspAdapterFactory adapterFactory) : IAdminPaymentSessionReader, IAdminPaymentRoutingSelector
{
    public async Task<AdminPaymentSessionResource?> ResolveAsync(
        Guid paymentSessionId,
        bool unrestricted,
        IReadOnlySet<Guid> accessibleMerchantIds,
        CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.Set<Session>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == paymentSessionId)
            .Select(x => new AdminPaymentSessionResource(x.Id, x.MerchantId, x.OrderId, x.Version))
            .SingleOrDefaultAsync(ct), cancellationToken);
        return row is not null && (unrestricted || accessibleMerchantIds.Contains(row.MerchantId)) ? row : null;
    }

    public async Task<Code> SelectAsync(
        Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken)
    {
        method = PaymentMethods.Normalize(method);
        var order = await PlatformReadGuard.ReadAsync(ct => db.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == orderId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new NotFoundException("Order was not found.");

        var active = await PlatformReadGuard.ReadAsync(ct => db.RoutingRulesets.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.MerchantId == merchantId && x.Status == RoutingRulesetStatus.Active, ct),
            cancellationToken);
        if (active is null)
            return await SelectDefaultAsync(merchantId, method, cancellationToken);

        var matching = active.Rules.Where(x => x.Enabled
                && (x.Method == "any" || x.Method == method)
                && (x.OriginatorId == null || x.OriginatorId == order.OriginatorId)
                && (x.MinAmount == null || order.Amount.Amount >= x.MinAmount)
                && (x.MaxAmount == null || order.Amount.Amount <= x.MaxAmount))
            .OrderBy(x => x.Priority)
            .ToArray();
        foreach (var rule in matching)
        {
            foreach (var id in new Guid?[] { rule.TargetConnectionId, rule.FallbackConnectionId })
            {
                if (id is null)
                    continue;
                var connection = await PlatformReadGuard.ReadAsync(ct => db.Set<Connection>().IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id && x.MerchantId == merchantId, ct), cancellationToken);
                if (connection is not null && Eligible(connection, method))
                    return connection.Psp;
            }
        }

        throw new ConflictException("No active routing rule can serve this payment.", "routing_unavailable");
    }

    private async Task<Code> SelectDefaultAsync(
        Guid merchantId, string method, CancellationToken cancellationToken)
    {
        var connection = await PlatformReadGuard.ReadAsync(ct => db.Set<Connection>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MerchantId == merchantId && x.Psp == defaultPsp.Psp, ct), cancellationToken);
        if (connection is null || !Eligible(connection, method))
            throw new ConflictException("Default PSP routing is unavailable.", "routing_unavailable");
        return connection.Psp;
    }

    private bool Eligible(Connection connection, string method)
    {
        try
        {
            connection.EnsureEligible(method);
            return adapterFactory.For(connection.Psp).SupportedMethods.Contains(method);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
