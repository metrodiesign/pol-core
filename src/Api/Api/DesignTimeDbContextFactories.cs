using System.Reflection;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Api;

/// <summary>
/// Module Infrastructure marker types whose namespaces select the shared DbContext's
/// <c>IEntityTypeConfiguration</c> classes. Shared by runtime composition and design-time
/// factories so <c>dotnet ef migrations</c> builds the same model the app runs.
/// </summary>
internal static class HostModuleAssemblies
{
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(Products.Infrastructure.ProductsModuleRegistration),
        typeof(Carts.Infrastructure.CartModuleRegistration),
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration),
        typeof(Payments.Infrastructure.PaymentsModuleRegistration),
        // global:: required: Api.Merchants/Api.Admins/Api.Iam (D7 area namespaces) now shadow the module root
        // namespaces Merchants/Admins/Iam from within namespace Api and its descendants.
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration),
        typeof(global::Admins.Infrastructure.AdminModuleRegistration),
        typeof(global::Iam.Infrastructure.IamModuleRegistration),
        typeof(global::Governance.Infrastructure.GovernanceModuleRegistration),
        typeof(global::Notifications.Infrastructure.NotificationsModuleRegistration),
        typeof(global::Accounts.Infrastructure.AccountsModuleRegistration),
        typeof(global::Access.Infrastructure.AccessModuleRegistration),
    ];

    // ponytail: design-time only — real env-driven connection string is supplied via POL_DESIGN_SQL;
    // this localhost default just lets the model build for migrations.
    public static string DesignConnectionString =>
        Environment.GetEnvironmentVariable("POL_DESIGN_SQL")
        ?? "Server=localhost;Database=pol_core_design;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>Lets <c>dotnet ef migrations</c> construct the merchant-user model without booting the host.</summary>
public sealed class PolDbContextFactory : IDesignTimeDbContextFactory<PolDbContext>
{
    public PolDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(HostModuleAssemblies.DesignConnectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, new ModuleAssemblies(HostModuleAssemblies.All));
    }
}
