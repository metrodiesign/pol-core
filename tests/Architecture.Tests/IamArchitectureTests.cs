using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// The rf2 dependency decision (design.md §2 "Dependency rule ใหม่"): <c>Iam.Domain</c> is a PUBLISHED
/// LANGUAGE — the single RBAC vocabulary that any module may reference directly, like <c>SharedKernel</c> —
/// but the rest of the module is confined:
///   (b) <c>Iam.*</c> references NO business module (it is a leaf), and every OTHER module may reference
///       ONLY <c>Iam.Domain</c>, never <c>Iam.Application</c> or <c>Iam.Infrastructure</c>;
///   (c) the <c>iam.Roles</c> table is queried ONLY from the Iam store and the two side "resolution
///       repositories" — any other type reaching for the <see cref="global::Iam.Domain.Roles.Role"/> entity
///       (a would-be direct querier) fails the guard. Duplicating role reads outside these carve-outs is
///       exactly the drift rf2 exists to kill (REQ-1.6, design.md Technology Decisions "RLS บน iam.*").
/// </summary>
public class IamArchitectureTests
{
    private static readonly Assembly IamDomain = typeof(global::Iam.Domain.Roles.Role).Assembly;
    private static readonly Assembly IamApplication = typeof(global::Iam.Application.Roles.RoleSideContext).Assembly;
    private static readonly Assembly IamInfrastructure = typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly;

    // Every OTHER module's three layers — the ones allowed to reference Iam.Domain but nothing deeper.
    private static readonly Assembly[] OtherModuleAssemblies =
    [
        typeof(global::Products.Domain.Product).Assembly,
        typeof(global::Products.Application.IProductRepository).Assembly,
        typeof(global::Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(global::Carts.Domain.Cart).Assembly,
        typeof(global::Carts.Application.ICartRepository).Assembly,
        typeof(global::Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(global::Checkouts.Domain.Session).Assembly,
        typeof(global::Checkouts.Application.ICheckoutRepository).Assembly,
        typeof(global::Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(global::Orders.Domain.Order).Assembly,
        typeof(global::Orders.Application.IOrderRepository).Assembly,
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(global::Payments.Domain.Session).Assembly,
        typeof(global::Payments.Application.Ports.IPspAdapter).Assembly,
        typeof(global::Payments.Infrastructure.Psp.PspAdapterFactory).Assembly,
        typeof(global::Admins.Domain.Users.User).Assembly,
        typeof(global::Admins.Application.Users.IUserRepository).Assembly,
        typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(global::Merchants.Domain.Merchant).Assembly,
        typeof(global::Merchants.Application.IMerchantRepository).Assembly,
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
    ];

    // Every OTHER module's Infrastructure (the migration-owner's EF configs still live there, task 8's
    // PolDbContext stays the migration owner) PLUS the three runtime Persistence assemblies (task 8.5 — the
    // actual repositories that query iam.Roles at runtime now live there, not in a module's own
    // Infrastructure project). Together these are the only place a rogue `iam.Roles` query could be written.
    private static readonly Assembly[] ProductionInfrastructure =
    [
        typeof(global::Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(global::Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(global::Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(global::Payments.Infrastructure.Psp.PspAdapterFactory).Assembly,
        typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
        typeof(Persistence.ControlPlane.ControlPlaneDbContext).Assembly,
        typeof(Persistence.MerchantUser.MerchantUserDbContext).Assembly,
        typeof(Persistence.MerchantRuntime.MerchantRuntimeDbContext).Assembly,
    ];

    // The ONLY namespaces allowed to depend on the iam.Roles entity type: the migration-owner's EF configs
    // (unchanged, task 8 keeps PolDbContext as the schema owner) and, at runtime, the Iam store + the
    // ControlPlane-side resolution repositories (task 8.5.1/8.5.7 — MerchantRoleReader resolves iam.Roles for
    // BOTH the admin console's own RoleRepository AND the merchant side's cross-context read, since
    // Persistence.MerchantUser may never reference Iam.Domain directly, design.md "Context topology").
    private static readonly string[] ConfinedNamespaces =
    [
        "Iam.Infrastructure.Persistence.Roles",
        "Admins.Infrastructure.Persistence.Roles",
        "Merchants.Infrastructure.Persistence.Users.Roles",
        "Persistence.ControlPlane.Iam",
        "Persistence.ControlPlane.Admins",
        // The context class itself declares "DbSet<Role> Roles => Set<Role>()" (task 8.5.1) — an IL-level
        // dependency on the entity type, not a query. It is not itself a rogue querier.
        "Persistence.ControlPlane",
    ];

    private const string RoleEntity = "Iam.Domain.Roles.Role";

    // ---- (b) module reference rules -------------------------------------------------------------------

    [Fact]
    public void Iam_layer_keys_match_their_real_assembly_names()
    {
        Assert.Equal("Iam.Domain", IamDomain.GetName().Name);
        Assert.Equal("Iam.Application", IamApplication.GetName().Name);
        Assert.Equal("Iam.Infrastructure", IamInfrastructure.GetName().Name);
    }

    [Fact]
    public void Iam_does_not_depend_on_any_business_module()
    {
        // Iam is a leaf published-language module: it references SharedKernel/BuildingBlocks/EF, never a peer.
        string[] forbidden =
        [
            "Products.Domain", "Products.Application", "Products.Infrastructure",
            "Carts.Domain", "Carts.Application", "Carts.Infrastructure",
            "Checkouts.Domain", "Checkouts.Application", "Checkouts.Infrastructure",
            "Orders.Domain", "Orders.Application", "Orders.Infrastructure",
            "Payments.Domain", "Payments.Application", "Payments.Infrastructure",
            "Admins.Domain", "Admins.Application", "Admins.Infrastructure",
            "Merchants.Domain", "Merchants.Application", "Merchants.Infrastructure",
        ];
        AssertAllResolveToARealAssembly(forbidden);

        foreach (var assembly in new[] { IamDomain, IamApplication, IamInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on any business module. {Offenders(result)}");
        }
    }

    [Fact]
    public void Iam_layers_do_not_depend_on_a_Host()
    {
        foreach (var assembly in new[] { IamDomain, IamApplication, IamInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("Api").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on a Host. {Offenders(result)}");
        }
    }

    [Fact]
    public void Other_modules_reference_only_Iam_Domain_not_Application_or_Infrastructure()
    {
        // Iam.Domain is the published surface; Iam.Application/Iam.Infrastructure are internal to the module.
        string[] forbidden = ["Iam.Application", "Iam.Infrastructure"];
        AssertAllResolveToARealAssembly(forbidden);

        foreach (var assembly in OtherModuleAssemblies)
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} may reference only Iam.Domain, not Iam.Application/Infrastructure. {Offenders(result)}");
        }
    }

    // ---- (c) iam.Roles confinement --------------------------------------------------------------------

    [Fact]
    public void Only_the_store_and_resolution_repositories_query_iam_Roles()
    {
        // Fail loud if the entity name drifts: at least one type MUST depend on it, or the guard is vacuous.
        var dependents = Types.InAssemblies(ProductionInfrastructure)
            .That().HaveDependencyOn(RoleEntity)
            .GetTypes()
            .ToList();

        Assert.True(dependents.Count > 0,
            $"No production type depends on '{RoleEntity}' — the confinement guard is vacuous (renamed entity?).");

        var offenders = dependents
            .Where(t => !ConfinedNamespaces.Any(ns => (t.Namespace ?? string.Empty) == ns))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{RoleEntity}' may be queried only from the Iam store + resolution repositories "
            + $"({string.Join(", ", ConfinedNamespaces)}). Offenders: {string.Join(", ", offenders)}");
    }

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);

    // REQ-15.2: NotHaveDependencyOnAny only checks dependencies that exist; a name that resolves to NO assembly
    // at all (e.g. stale after a rename) makes the guard pass vacuously instead of catching a real crossing.
    private static void AssertAllResolveToARealAssembly(string[] names)
    {
        foreach (var name in names)
        {
            Exception? failure = null;
            try { Assembly.Load(name); }
            catch (Exception ex) { failure = ex; }

            Assert.True(failure is null,
                $"'{name}' does not resolve to any loaded assembly ({failure?.Message}) — a stale or " +
                "mistyped name here would make NotHaveDependencyOnAny pass vacuously instead of guarding anything.");
        }
    }
}
