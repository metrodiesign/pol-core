using System.Reflection;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AdminConsole;

/// <summary>
/// Module Infrastructure assemblies whose <c>IEntityTypeConfiguration</c> classes the shared
/// DbContexts apply at model-build time. Shared by the runtime composition root and the design-time
/// factories so <c>dotnet ef migrations</c> builds the same model the app runs.
/// </summary>
internal static class HostModuleAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(Cart.Infrastructure.CartModuleRegistration).Assembly,
        typeof(Checkout.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
    ];

    // The admin control plane has no entities of its own yet, so AdminDbContext maps NOTHING from the
    // producer modules (otherwise admin migrations would recreate Products/Orders/PaymentSessions under
    // the admin schema). Add control-plane assemblies here when they exist.
    public static IReadOnlyList<Assembly> Admin { get; } = [];

    // ponytail: design-time only — real env-driven connection string is supplied via POL_DESIGN_SQL;
    // this localhost default just lets the model build for migrations.
    public static string DesignConnectionString =>
        Environment.GetEnvironmentVariable("POL_DESIGN_SQL")
        ?? "Server=localhost;Database=pol_core_design;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>Lets <c>dotnet ef migrations</c> construct the producer model without booting the host.</summary>
public sealed class ProducerDbContextFactory : IDesignTimeDbContextFactory<ProducerDbContext>
{
    public ProducerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProducerDbContext>()
            .UseSqlServer(HostModuleAssemblies.DesignConnectionString)
            .Options;
        return new ProducerDbContext(options, new ModuleAssemblies(HostModuleAssemblies.All, HostModuleAssemblies.Admin));
    }
}

/// <summary>Lets <c>dotnet ef migrations</c> construct the admin model without booting the host.</summary>
public sealed class AdminDbContextFactory : IDesignTimeDbContextFactory<AdminDbContext>
{
    public AdminDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseSqlServer(HostModuleAssemblies.DesignConnectionString)
            .Options;
        return new AdminDbContext(options, new ModuleAssemblies(HostModuleAssemblies.All, HostModuleAssemblies.Admin));
    }
}
