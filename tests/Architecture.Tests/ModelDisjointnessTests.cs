using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUser;

namespace Architecture.Tests;

/// <summary>
/// Guardrail for rls-to-query-filter REQ-11.5: every entity the migration-owner
/// (<see cref="PolDbContext"/>) maps must land in EXACTLY ONE of the three runtime contexts
/// (<see cref="ControlPlaneDbContext"/> / <see cref="MerchantUserDbContext"/> /
/// <see cref="MerchantRuntimeDbContext"/>) — no entity left unassigned (a silent read-floor gap) and no
/// entity double-assigned (a context boundary that doesn't actually separate the clusters). Model built the
/// same Sqlite-in-memory way as <see cref="EntitySchemaMappingTests"/>; owned/complex types (Money) report no
/// table of their own and are skipped — only entity types that own a physical table are compared.
/// </summary>
public sealed class ModelDisjointnessTests : IDisposable
{
    private readonly SqliteConnection _polConnection;
    private readonly SqliteConnection _controlPlaneConnection;
    private readonly SqliteConnection _merchantUserConnection;
    private readonly SqliteConnection _merchantRuntimeConnection;

    private readonly PolDbContext _pol;
    private readonly ControlPlaneDbContext _controlPlane;
    private readonly MerchantUserDbContext _merchantUser;
    private readonly MerchantRuntimeDbContext _merchantRuntime;

    public ModelDisjointnessTests()
    {
        _polConnection = OpenSqlite();
        _controlPlaneConnection = OpenSqlite();
        _merchantUserConnection = OpenSqlite();
        _merchantRuntimeConnection = OpenSqlite();

        var modules = new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
            typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
            typeof(Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly,
            typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
            typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
            typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
            typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
            typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
            typeof(MasterData.Infrastructure.MasterDataModuleRegistration).Assembly,
        ]);
        _pol = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_polConnection)
                .EnableServiceProviderCaching(false).Options,
            modules);

        _controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_controlPlaneConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        _merchantUser = new MerchantUserDbContext(
            new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(_merchantUserConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        _merchantRuntime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_merchantRuntimeConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    private static SqliteConnection OpenSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public void Every_PolDbContext_entity_is_assigned_to_exactly_one_runtime_context()
    {
        var polEntities = TableOwningTypes(_pol);
        var controlPlaneEntities = TableOwningTypes(_controlPlane);
        var merchantUserEntities = TableOwningTypes(_merchantUser);
        var merchantRuntimeEntities = TableOwningTypes(_merchantRuntime);

        var runtimeSets = new (string Name, HashSet<Type> Types)[]
        {
            ("ControlPlaneDbContext", controlPlaneEntities),
            ("MerchantUserDbContext", merchantUserEntities),
            ("MerchantRuntimeDbContext", merchantRuntimeEntities),
        };

        // Unassigned: mapped by PolDbContext but missing from every runtime context (a silent read-floor gap).
        var union = new HashSet<Type>();
        foreach (var (_, types) in runtimeSets)
            union.UnionWith(types);
        var unassigned = polEntities.Except(union).Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(unassigned.Count == 0,
            "Entity mapped by PolDbContext but not assigned to any runtime context: " + string.Join(", ", unassigned));

        // Double-assigned: mapped by more than one runtime context (the boundary doesn't actually separate them).
        var duplicates = new List<string>();
        foreach (var type in union)
        {
            var owners = runtimeSets.Where(s => s.Types.Contains(type)).Select(s => s.Name).ToList();
            if (owners.Count > 1)
                duplicates.Add($"{type.FullName} -> [{string.Join(", ", owners)}]");
        }
        Assert.True(duplicates.Count == 0, "Entity assigned to more than one runtime context: " + string.Join(", ", duplicates));

        // Phantom: a runtime context maps something PolDbContext (the migration owner, which maps ALL tables)
        // does not — would mean a runtime context invented a table with no migration behind it. Task 8.1
        // closed PolDbContext's model catch-up (MerchantUserOutbox + ProvisioningOperation now mapped here
        // too, ahead of task 8.2's migration), so no allowlist is needed anymore.
        var phantoms = union.Except(polEntities).Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(phantoms.Count == 0,
            "Entity mapped by a runtime context but not by PolDbContext (no migration owns it): " + string.Join(", ", phantoms));
    }

    [Fact]
    public void ControlPlane_MerchantUser_and_MerchantRuntime_entity_sets_are_pairwise_disjoint()
    {
        var controlPlaneEntities = TableOwningTypes(_controlPlane);
        var merchantUserEntities = TableOwningTypes(_merchantUser);
        var merchantRuntimeEntities = TableOwningTypes(_merchantRuntime);

        AssertDisjoint("ControlPlaneDbContext", controlPlaneEntities, "MerchantUserDbContext", merchantUserEntities);
        AssertDisjoint("ControlPlaneDbContext", controlPlaneEntities, "MerchantRuntimeDbContext", merchantRuntimeEntities);
        AssertDisjoint("MerchantUserDbContext", merchantUserEntities, "MerchantRuntimeDbContext", merchantRuntimeEntities);
    }

    private static void AssertDisjoint(string leftName, HashSet<Type> left, string rightName, HashSet<Type> right)
    {
        var overlap = left.Intersect(right).Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(overlap.Count == 0,
            $"{leftName} and {rightName} both map: " + string.Join(", ", overlap));
    }

    // Only entity types that own a physical table are comparable across contexts — owned/complex types
    // (Money) share their owner's table and report GetTableName() == null (same skip EntitySchemaMappingTests
    // uses); a TPC abstract base (MasterDataItem) also reports null since only its concrete leaves own tables.
    private static HashSet<Type> TableOwningTypes(DbContext context) =>
        [.. context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).Select(e => e.ClrType)];

    public void Dispose()
    {
        _pol.Dispose();
        _controlPlane.Dispose();
        _merchantUser.Dispose();
        _merchantRuntime.Dispose();
        _polConnection.Dispose();
        _controlPlaneConnection.Dispose();
        _merchantUserConnection.Dispose();
        _merchantRuntimeConnection.Dispose();
    }
}
