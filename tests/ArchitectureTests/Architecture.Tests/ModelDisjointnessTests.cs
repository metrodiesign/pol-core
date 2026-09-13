using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;

namespace Architecture.Tests;

/// <summary>
/// Guardrail for rls-to-query-filter REQ-11.5: every entity the migration-owner
/// (<see cref="PolDbContext"/>) maps must land in EXACTLY ONE of the two runtime contexts
/// (<see cref="ControlPlaneDbContext"/> / <see cref="CommerceDbContext"/>) — no entity left
/// unassigned (a silent read-floor gap) and no
/// entity double-assigned (a context boundary that doesn't actually separate the clusters). Model built the
/// same Sqlite-in-memory way as <see cref="EntitySchemaMappingTests"/>; owned/complex types (Money) report no
/// table of their own and are skipped — only entity types that own a physical table are compared.
/// </summary>
public sealed class ModelDisjointnessTests : IDisposable
{
    private readonly SqliteConnection _polConnection;
    private readonly SqliteConnection _controlPlaneConnection;
    private readonly SqliteConnection _merchantRuntimeConnection;

    private readonly PolDbContext _pol;
    private readonly ControlPlaneDbContext _controlPlane;
    private readonly CommerceDbContext _merchantRuntime;

    public ModelDisjointnessTests()
    {
        _polConnection = OpenSqlite();
        _controlPlaneConnection = OpenSqlite();
        _merchantRuntimeConnection = OpenSqlite();

        var modules = new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(global::Admins.Infrastructure.AdminModuleRegistration),
            typeof(global::Iam.Infrastructure.IamModuleRegistration),
            typeof(global::Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(global::Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(global::Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(global::Access.Infrastructure.AccessModuleRegistration),
        ]);
        _pol = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_polConnection)
                .EnableServiceProviderCaching(false).Options,
            modules);

        _controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_controlPlaneConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        _merchantRuntime = new CommerceDbContext(
            new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(_merchantRuntimeConnection)
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
        var merchantRuntimeEntities = TableOwningTypes(_merchantRuntime);

        var runtimeSets = new (string Name, HashSet<Type> Types)[]
        {
            ("ControlPlaneDbContext", controlPlaneEntities),
            ("CommerceDbContext", merchantRuntimeEntities),
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

        var phantoms = union.Except(polEntities)
            .Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(phantoms.Count == 0,
            "Entity mapped by a runtime context but not by PolDbContext (no migration owns it): " + string.Join(", ", phantoms));
    }

    [Fact]
    public void ControlPlane_and_MerchantRuntime_entity_sets_are_pairwise_disjoint()
    {
        var controlPlaneEntities = TableOwningTypes(_controlPlane);
        var merchantRuntimeEntities = TableOwningTypes(_merchantRuntime);

        AssertDisjoint("ControlPlaneDbContext", controlPlaneEntities, "CommerceDbContext", merchantRuntimeEntities);
    }

    [Fact]
    public void Design_owner_matrix_pins_control_plane_and_commerce_business_groups()
    {
        var controlPlane = TableOwningTypes(_controlPlane);
        var commerce = TableOwningTypes(_merchantRuntime);

        Assert.Equal(
            ["access", "acct", "admin", "cfg", "dbo", "iam", "merch", "oauth", "txn"],
            controlPlane.Select(type => _controlPlane.Model.FindEntityType(type)!.GetSchema()!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["checkout", "shop", "txn"],
            commerce.Select(type => _merchantRuntime.Model.FindEntityType(type)!.GetSchema()!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        AssertOwner(controlPlane, commerce, controlPlaneOwner: true,
            typeof(Accounts.Domain.Account), typeof(Access.Domain.AccessRole),
            typeof(Iam.Domain.Roles.Role), typeof(Governance.Domain.OperationRecord),
            typeof(Governance.Domain.GovernanceOutboxMessage), typeof(Merchants.Domain.Merchant),
            typeof(Merchants.Domain.Branch), typeof(Merchants.Domain.Sale),
            typeof(Merchants.Domain.Originator), typeof(Payments.Domain.Psp.Connection),
            typeof(Payments.Domain.Capabilities.PaymentProvider),
            typeof(Payments.Domain.Capabilities.MerchantProviderAccountMethod),
            typeof(Payments.Domain.Capabilities.MerchantProviderAccountMethodOption),
            typeof(Payments.Domain.Capabilities.MerchantPaymentMethod),
            typeof(Payments.Domain.Capabilities.MerchantUserPaymentMethod),
            typeof(Payments.Domain.Routing.RoutingRuleset), typeof(Payments.Domain.Routing.RoutingRule),
            typeof(Payments.Domain.ApprovalExecutionRecord),
            typeof(Payments.Domain.Capabilities.PaymentCapabilityMigrationConflict),
            typeof(BuildingBlocks.Infrastructure.Vault.VaultSecretBlob),
            typeof(BuildingBlocks.Infrastructure.Vault.VaultSecretVersion),
            typeof(BuildingBlocks.Infrastructure.Vault.VaultRevealAudit));

        AssertOwner(controlPlane, commerce, controlPlaneOwner: false,
            typeof(Orders.Domain.Order), typeof(Orders.Domain.Items.Item),
            typeof(Carts.Domain.Cart), typeof(Carts.Domain.Items.Item),
            typeof(Checkouts.Domain.PaymentLink), typeof(Checkouts.Domain.PaymentLinkReplay),
            typeof(Payments.Domain.Session), typeof(Payments.Domain.Transaction),
            typeof(Payments.Domain.TransactionEvent), typeof(Payments.Domain.InboundWebhookEvent),
            typeof(Notifications.Domain.Notification), typeof(Notifications.Domain.TemplateVersion),
            typeof(Notifications.Domain.Delivery), typeof(Notifications.Domain.DeliveryAttempt),
            typeof(Notifications.Domain.NotificationInboxMessage), typeof(Notifications.Domain.NotificationReviewNote),
            typeof(BuildingBlocks.Infrastructure.Idempotency.AdminOperationRecord),
            typeof(BuildingBlocks.Infrastructure.Idempotency.IdempotencyRecord),
            typeof(BuildingBlocks.Infrastructure.Outbox.OutboxMessage));
    }

    private static void AssertOwner(
        HashSet<Type> controlPlane, HashSet<Type> commerce, bool controlPlaneOwner, params Type[] types)
    {
        foreach (var type in types)
        {
            Assert.Contains(type, controlPlaneOwner ? controlPlane : commerce);
            Assert.DoesNotContain(type, controlPlaneOwner ? commerce : controlPlane);
        }
    }

    private static void AssertDisjoint(string leftName, HashSet<Type> left, string rightName, HashSet<Type> right)
    {
        var overlap = left.Intersect(right).Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(overlap.Count == 0,
            $"{leftName} and {rightName} both map: " + string.Join(", ", overlap));
    }

    // Only entity types that own a physical table are comparable across contexts — owned/complex types
    // (Money) share their owner's table and report GetTableName() == null (same skip EntitySchemaMappingTests
    // uses). (The old TPC abstract base fell in the same bucket; gone since masterdata-split.)
    private static HashSet<Type> TableOwningTypes(DbContext context) =>
        [.. context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).Select(e => e.ClrType)];

    public void Dispose()
    {
        _pol.Dispose();
        _controlPlane.Dispose();
        _merchantRuntime.Dispose();
        _polConnection.Dispose();
        _controlPlaneConnection.Dispose();
        _merchantRuntimeConnection.Dispose();
    }
}
