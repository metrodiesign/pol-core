using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Checkouts.Application;
using Carts.Application;
using Merchants.Application;
using Merchants.Application.AdminControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Governance.Application;
using Payments.Application;
using Payments.Application.Capabilities;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Application.HandlePspWebhook;
using Platform.Application.Transactions;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.ControlPlane.Orders;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Orders.Items;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Payments.Psp;
using Persistence.MerchantRuntime.Reporting;
using Persistence.MerchantRuntime.Webhooks;
using Persistence.MerchantRuntime.Notifications;
using Products.Application;
using Reporting.Application;
using Notifications.Application;

namespace Persistence.MerchantRuntime;

/// <summary>
/// Registers the Commerce cluster's <see cref="CommerceDbContext"/> + every repository/port adapter that touches it
/// (rls-to-query-filter design.md "Context topology" — shop.*/txn.* + merch.Merchants/VaultSecrets/
/// VaultRevealAudits/ProvisioningAudits). Every adapter is <c>internal sealed</c> to this assembly, so
/// only this extension can wire them into the container — no other assembly can new one up. All Scoped,
/// unkeyed (this cluster has no separate RLS-bypass principal to key against, task 8's "1 principal").
/// </summary>
public static class MerchantRuntimePersistenceRegistration
{
    public static IServiceCollection AddCommercePersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<CommerceDbContext>()
                .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new CommerceDbContext(
                options, sp.GetRequiredService<IActorContext>(), authorizerFactory(sp),
                sp.GetRequiredService<ISecurityTelemetry>());
        });

        services.AddScoped<CartRepository>();
        services.AddScoped<ICartRepository>(sp => sp.GetRequiredService<CartRepository>());
        services.AddScoped<ICartForOrderStore>(sp => sp.GetRequiredService<CartRepository>());
        services.AddScoped<IAdminCartReader, AdminCartReader>();
        services.AddScoped<OrderRepository>();
        services.AddScoped<IOrderRepository>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IOrderStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IOrderWorkflowStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IPaymentLinkStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<IPaymentLinkReplayStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<OrderLinkIssuer>();
        services.AddScoped<PaymentLinkReplayService>();
        services.AddScoped<ICheckoutTransactionStore>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddScoped<ITransactionRepository>(sp => sp.GetRequiredService<OrderRepository>());
        services.AddSingleton<ITransactionInquiryScheduler, TransactionInquiryScheduler>();
        services.AddScoped<IOrderOwnerResolver, OrderOwnerResolver>();
        services.AddSingleton<IPaymentLinkTokenService, DataProtectedPaymentLinkTokenService>();
        services.AddSingleton<IPaymentLinkReplayProtector, DataProtectedPaymentLinkReplayProtector>();
        services.AddSingleton<ICheckoutCapabilityService, DataProtectedCheckoutCapabilityService>();
        services.AddSingleton<ITransactionReturnBindingService, DataProtectedTransactionReturnBindingService>();
        services.AddScoped<ICustomerCheckoutReader, CustomerCheckoutReader>();
        services.AddScoped<ITrustedOrderPricingSource, UnconfiguredTrustedOrderPricingSource>();
        services.AddScoped<IAdminOrderReader, AdminOrderReader>();
        services.AddScoped<IOrderSummaryReader, OrderSummaryReader>();
        services.AddScoped<IOrderNoSequence, OrderNoSequence>();
        services.AddScoped<IPaymentSessionProbe, PaymentSessionProbe>();
        services.AddScoped<IDocumentSaleProbe, DocumentSaleProbe>();
        services.AddScoped<IDoubleSellAuditor, DoubleSellAuditor>();
        services.AddScoped<IRevealAuditWriter, RevealAuditWriter>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<InboundWebhookStore>();
        services.AddScoped<IInboundWebhookRecorder>(sp => sp.GetRequiredService<InboundWebhookStore>());
        services.AddScoped<IAdminInboundWebhookReader>(sp => sp.GetRequiredService<InboundWebhookStore>());
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<AdminPaymentSessionReader>();
        services.AddScoped<IAdminPaymentSessionReader>(sp => sp.GetRequiredService<AdminPaymentSessionReader>());
        services.AddScoped<IPaymentRouteSelector>(sp => sp.GetRequiredService<AdminPaymentSessionReader>());
        services.AddScoped<IAdminReportingReader, AdminReportingReader>();
        services.AddScoped<IPayableOrderReader, PayableOrderReader>();

        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<INotificationMaterializer, NotificationMaterializer>();
        services.AddScoped<NotificationDeliveryProcessor>();
        services.AddScoped<IBusinessWebhookSender>(sp => new SignedBusinessWebhookSender(
            sp.GetRequiredService<ISafeDestinationValidator>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.AddScoped<INotificationOperations, NotificationOperations>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IWebhookMerchantResolver, WebhookMerchantResolver>();


        services.AddScoped<IUnitOfWork, MerchantRuntimeUnitOfWork>();
        services.AddScoped<IAdminOperationExecutor>(sp =>
            new Idempotency.AdminOperationExecutor(
                sp.GetRequiredService<CommerceDbContext>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IUnitOfWork>()));

        return services;
    }

    // Compatibility entry point for callers that have not renamed the persistence seam yet. It adds no
    // context of its own and delegates to the canonical Commerce registration above.
    public static IServiceCollection AddMerchantRuntimePersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory) =>
        services.AddCommercePersistence(connectionString, authorizerFactory);

}
