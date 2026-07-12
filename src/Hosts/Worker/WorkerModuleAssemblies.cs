using System.Reflection;

namespace Worker;

/// <summary>
/// Module Infrastructure assemblies whose entity configurations the worker's PolDbContext applies
/// at model-build time — the same set the consoles use, so the worker builds the identical model the
/// migrations created (it reads/writes merchant-user tables, never the admin schema).
/// </summary>
internal static class WorkerModuleAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        // The dispatcher consumes MerchantUserRegistrationSubmitted -> records a control-plane notice (REQ-20.4);
        // the merchant-user EF configs (incl. MerchantUserRegistrationNotices) must be in the worker's model to write it.
        typeof(Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        // The worker's PolDbContext must map the same iam.* tables the API/migrations model has (rf2) — even
        // though the worker never queries them today, a model that omits them would diverge from the migrated
        // schema and fail HasPendingModelChanges()-style consistency checks.
        typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
    ];
}
