using System.Reflection;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Api;

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
        typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        // global:: required: Api.Merchants/Api.Admins/Api.Iam (D7 area namespaces) now shadow the module root
        // namespaces Merchants/Admins/Iam from within namespace Api and its descendants.
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
        // masterdata-split: the four reference modules replace the retired master-data module. global:: for the
        // same D7 insurance as above (a future Api.<Area> namespace must not shadow the module roots).
        typeof(global::Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
        typeof(global::Levels.Infrastructure.LevelsModuleRegistration).Assembly,
        typeof(global::Offices.Infrastructure.OfficesModuleRegistration).Assembly,
        typeof(global::Positions.Infrastructure.PositionsModuleRegistration).Assembly,
        typeof(global::Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
        typeof(global::Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
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
