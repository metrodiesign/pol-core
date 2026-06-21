using System.Reflection;

namespace Worker;

/// <summary>
/// Module Infrastructure assemblies whose entity configurations the worker's ProducerDbContext applies
/// at model-build time — the same set the consoles use, so the worker builds the identical model the
/// migrations created (it reads/writes producer tables, never the admin schema).
/// </summary>
internal static class WorkerModuleAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(Cart.Infrastructure.CartModuleRegistration).Assembly,
        typeof(Checkout.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
    ];
}
