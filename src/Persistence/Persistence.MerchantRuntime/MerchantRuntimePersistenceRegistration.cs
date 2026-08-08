using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Carts.Application;
using Merchants.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.MerchantRuntime.Merchants;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Orders.Items;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Payments.Psp;
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
                .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new MerchantRuntimeDbContext(
                options, sp.GetRequiredService<IActorContext>(), authorizerFactory(sp),
                sp.GetRequiredService<ISecurityTelemetry>());
        });

        services.AddScoped<CartRepository>();
        services.AddScoped<ICartRepository>(sp => sp.GetRequiredService<CartRepository>());
        services.AddScoped<ICartForOrderStore>(sp => sp.GetRequiredService<CartRepository>());
        services.AddScoped<OrderRepository>();
        services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IOrderStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IOrderSummaryReader, OrderSummaryReader>();
        services.AddScoped<IOrderNoSequence, OrderNoSequence>();
        services.AddScoped<IPaymentSessionProbe, PaymentSessionProbe>();
        services.AddScoped<IDocumentSaleProbe, DocumentSaleProbe>();
        services.AddScoped<IDoubleSellAuditor, DoubleSellAuditor>();
        services.AddScoped<IRevealAuditWriter, RevealAuditWriter>();
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

}
