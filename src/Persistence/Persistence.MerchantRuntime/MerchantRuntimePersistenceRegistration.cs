using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Carts.Application;
using Checkouts.Application;
using Merchants.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Orders.Domain.Items;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Checkouts;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.MerchantRuntime.Merchants;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Orders.Items;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Payments.Psp;
using Persistence.MerchantRuntime.Products;
using Persistence.MerchantRuntime.Vault;
using Persistence.MerchantRuntime.Webhooks;
using Products.Application;

namespace Persistence.MerchantRuntime;

/// <summary>
/// Registers the MerchantRuntime cluster's DbContext + every repository/port adapter that touches it
/// (rls-to-query-filter design.md "Context topology" — shop.*/txn.* + merch.Merchants/VaultSecrets/
/// VaultRevealAudits/ProvisioningAudits). Every adapter is <c>internal sealed</c> to this assembly, so
/// only this extension can wire them into the container — no other assembly can new one up. All Scoped,
/// unkeyed (this cluster has no separate RLS-bypass principal to key against, task 8's "1 principal").
/// </summary>
public static class MerchantRuntimePersistenceRegistration
{
    public static IServiceCollection AddMerchantRuntimePersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            return new MerchantRuntimeDbContext(
                options, sp.GetRequiredService<IActorContext>(), authorizerFactory(sp),
                sp.GetRequiredService<ISecurityTelemetry>());
        });

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICartRepository, CartRepository>();
        services.AddScoped<ICheckoutRepository, CheckoutRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderSummaryReader, OrderSummaryReader>();
        services.AddScoped<IOrderNoSequence, OrderNoSequence>();
        services.AddScoped<IPaymentSessionProbe, PaymentSessionProbe>();
        services.AddScoped<IRevealAuditWriter, RevealAuditWriter>();
        services.AddScoped<IItemPolicyRepository, ItemPolicyRepository>();
        services.AddScoped<IPolicyReportRepository, PolicyReportRepository>();
        services.AddScoped<IAdminItemPolicyReader, AdminItemPolicyReader>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IPayableOrderReader, PayableOrderReader>();
        services.AddScoped<MerchantRepository>();
        services.AddScoped<IMerchantRepository>(sp => sp.GetRequiredService<MerchantRepository>());
        services.AddScoped<IMerchantDirectoryReader>(sp => sp.GetRequiredService<MerchantRepository>());

        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IWebhookMerchantResolver, WebhookMerchantResolver>();

        services.AddScoped<IVaultSecretStore, LocalEnvelopeVaultStore>();
        services.AddScoped<IVaultMaintenance, VaultMaintenance>();
        services.AddScoped<IVaultRevealAuditVerifier, VaultRevealAuditVerifier>();
        services.AddScoped<IVaultAuditAppender, VaultAuditAppender>();
        services.AddScoped<IVaultRevealAuditWriter, VaultRevealAuditAppenderAdapter>();

        services.AddScoped<IUnitOfWork, MerchantRuntimeUnitOfWork>();

        return services;
    }

    /// <summary>
    /// Admin cross-merchant write escape-hatch for <see cref="ItemPolicy"/> (policy-reference-record
    /// REQ-3.2-admin/3.3-admin, design.md "Write guard registration" §Admin plane) — mirrors
    /// <c>Persistence.Provisioning</c>'s <c>AddProvisioning</c>: a SEPARATE <see cref="MerchantRuntimeDbContext"/>
    /// instance built with the caller-supplied admin authorizer (never the ambient
    /// <c>MerchantRequestWriteAuthorizer</c> <see cref="AddMerchantRuntimePersistence"/> binds every ordinary
    /// request to), registered ONLY under its own narrow port type (<see cref="IAdminItemPolicyWriter"/>) so
    /// DI can never confuse it with the ambient <see cref="MerchantRuntimeDbContext"/> registered above. An
    /// admin HTTP request has no bound merchant (<c>HasActor=false</c>), so the ambient write floor denies it
    /// unconditionally regardless of entity type — this is genuinely new machinery, not an extension of the
    /// ambient registration.
    /// </summary>
    public static IServiceCollection AddAdminItemPolicyWriter(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped<IAdminItemPolicyWriter>(sp =>
        {
            var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            var db = new MerchantRuntimeDbContext(
                options, UnboundActorContext.Instance, authorizerFactory(sp), sp.GetRequiredService<ISecurityTelemetry>());
            return new AdminItemPolicyWriter(db, sp.GetRequiredService<ISecurityTelemetry>());
        });

        return services;
    }

    /// <summary>No query filter ever needs a real merchant for this context — every read the writer runs is
    /// an explicit <c>IgnoreQueryFilters()</c> (the write floor, not the actor, is what confines it). Mirrors
    /// <c>Persistence.Provisioning.ProvisioningRegistration</c>'s own private <c>UnboundActorContext</c>.</summary>
    private sealed class UnboundActorContext : IActorContext
    {
        public static readonly UnboundActorContext Instance = new();
        public Guid MerchantId => throw new InvalidOperationException("No actor is bound.");
        public Guid? UserId => null;
        public bool HasActor => false;
    }
}
