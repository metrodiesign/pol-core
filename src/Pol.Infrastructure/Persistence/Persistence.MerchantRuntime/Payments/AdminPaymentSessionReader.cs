using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Application;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using SharedKernel;
using Persistence.ControlPlane;

namespace Persistence.MerchantRuntime.Payments;

/// <summary>
/// Reads admin payment-session resources and — as <see cref="IPaymentRouteSelector"/> — is the single
/// server-side routing authority for every audience (REQ-6.8-6.18). It resolves a connection from the
/// merchant's active ruleset (primary before fallback) and refuses when nothing is eligible; there is no
/// deployment default (REQ-6.18). Local eligibility only: enabled state, environment match, a credential
/// reference and the two-level method policy, which it delegates to
/// <see cref="IEffectivePaymentCapabilityResolver"/> so account-and-merchant policy (REQ-5.15) is decided
/// in exactly one place and never re-implemented against the raw tables.
/// </summary>
internal sealed class AdminPaymentSessionReader(
    CommerceDbContext db,
    ControlPlaneDbContext controlPlane,
    IEffectivePaymentCapabilityResolver capabilities) : IAdminPaymentSessionReader, IPaymentRouteSelector
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

    public async Task<PspRouteSelection> SelectAsync(
        Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken)
    {
        method = PaymentMethods.Normalize(method);
        var order = await PlatformReadGuard.ReadAsync(ct => db.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == orderId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new NotFoundException("Order was not found.");

        var merchantEnvironment = await PlatformReadGuard.ReadAsync(ct => controlPlane.Merchants.IgnoreQueryFilters()
            .AsNoTracking().Where(x => x.Id == merchantId).Select(x => (PspEnvironment?)x.PaymentEnvironment)
            .SingleOrDefaultAsync(ct), cancellationToken)
            ?? throw new NotFoundException("Merchant was not found.");

        var active = await PlatformReadGuard.ReadAsync(ct => controlPlane.RoutingRulesets.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.MerchantId == merchantId && x.Status == RoutingRulesetStatus.Active, ct),
            cancellationToken);

        // REQ-6.18: no active ruleset covering the method is a refusal, never a deployment default.
        if (active is null)
            throw new ConflictException(
                "No active routing rule can serve this payment.", "routing_unavailable");

        var matching = active.Rules.Where(x => x.Enabled
                && (x.Method == "any" || x.Method == method)
                && (x.OriginatorId == null || x.OriginatorId == order.OriginatorId)
                && (x.MinAmount == null || order.Amount.Amount >= x.MinAmount)
                && (x.MaxAmount == null || order.Amount.Amount <= x.MaxAmount))
            .OrderBy(x => x.Priority)
            .ToArray();

        // Primary first, then fallback, in priority order — the fallback is used ONLY here, before any PSP
        // call. Once a charge request is in flight there is no failover (REQ-6.10, handled at StartRedirect).
        foreach (var rule in matching)
        {
            foreach (var id in new[] { rule.TargetConnectionId, rule.FallbackConnectionId })
            {
                if (id is not { } connectionId)
                    continue;
                var connection = await PlatformReadGuard.ReadAsync(ct => controlPlane.Set<Connection>().IgnoreQueryFilters()
                    .AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId && x.MerchantId == merchantId, ct),
                    cancellationToken);
                if (connection is not null
                    && await EligibleAsync(connection, method, merchantEnvironment, order.Amount, cancellationToken))
                    return new PspRouteSelection(
                        connection.Id, connection.Psp, connection.ActiveSecretVersionId!.Value,
                        connection.ActiveSecretEnvironment);
            }
        }

        throw new ConflictException(
            "No active routing rule can serve this payment.", "routing_unavailable");
    }

    /// <summary>
    /// Local, PSP-free eligibility (REQ-6.14-6.16): the connection is enabled (REQ-3.5), holds a credential
    /// reference and its active credential's environment matches the merchant's, and the method is enabled at
    /// BOTH the provider account and the merchant policy (REQ-5.15) — the last decided by the shared
    /// capability resolver, whose <c>QualifyingAccountId</c> is this connection. Never touches health
    /// (REQ-6.15) and never probes the PSP (REQ-6.16).
    /// </summary>
    private async Task<bool> EligibleAsync(
        Connection connection, string method, PspEnvironment merchantEnvironment, Money amount, CancellationToken ct)
    {
        if (!connection.IsEnabled)
            return false;
        if (connection.ActiveSecretVersionId is null)
            return false;
        if (connection.ActiveSecretEnvironment != merchantEnvironment)
            return false;

        var decision = await capabilities.ResolveMethodAsync(
            new ResolvePaymentMethod(
                new PaymentCapabilitySubject(connection.MerchantId, PaymentAudience.PlatformAdmin, null),
                method, connection.Psp.ToCode(), amount),
            ct);
        return decision.Allowed && decision.QualifyingAccountId == connection.Id;
    }
}
