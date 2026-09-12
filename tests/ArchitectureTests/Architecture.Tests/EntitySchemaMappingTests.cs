using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Architecture.Tests;

/// <summary>
/// Model assertion (rf1 REQ-1.4 + rf2 REQ-1.6): every mapped entity across EVERY module must land in one
/// of the allowed schemas — <c>shop</c>, <c>txn</c>, <c>admin</c>, <c>merch</c>, <c>iam</c>, <c>cfg</c> — with a
/// single named exception, <c>dbo</c> for the framework-owned <c>DataProtectionKeys</c> table. There is no
/// <c>HasDefaultSchema</c> fallback (<see cref="SchemaNames"/>), so an entity that forgets its schema would
/// silently land in <c>dbo</c>; this guard turns that into a red test. The rf2-specific tightening: every
/// <c>Iam</c> entity (the central RBAC catalog) maps to <c>iam</c> and nothing else.
///
/// The full model is built the same way <see cref="MoneyColumnMappingTests"/> builds it — Sqlite in-memory
/// over the module registration assemblies — but with ALL seven modules so admin/merch/iam entities are
/// present. Owned/complex types (Money) share their owner's table and report no table of their own; they are
/// skipped (only entity types that own a table are asserted).
/// </summary>
public sealed class EntitySchemaMappingTests : IDisposable
{
    // The multi-schema allow-set (design.md "Schema map") + the ONE named framework exception.
    private static readonly HashSet<string> AllowedSchemas =
        [SchemaNames.Shop, SchemaNames.Checkout, SchemaNames.Txn, SchemaNames.Admin, SchemaNames.Merch,
            SchemaNames.Iam, SchemaNames.Cfg, SchemaNames.Acct, SchemaNames.Access, SchemaNames.OAuth,
            SchemaNames.Dbo];

    // The named exception (REQ-1.4): dbo is allowed ONLY for framework-owned DataProtectionKeys. Any other
    // entity in dbo means a missing ToTable schema silently falling back — caught explicitly below.
    private const string DboException = "DataProtectionKeys";

    private readonly SqliteConnection _connection;
    private readonly PolDbContext _db;

    public EntitySchemaMappingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // EnableServiceProviderCaching(false): EF's model cache keys on the CONTEXT TYPE, not on
        // ModuleAssemblies — with caching on, this class and MoneyColumnMappingTests (5 assemblies, same
        // PolDbContext + SQLite) share one cached model and whichever test class runs first wins, making
        // Every_Iam_entity_maps_to_the_iam_schema flake by xunit ordering.
        var options = new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_connection)
            .EnableServiceProviderCaching(false).Options;
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
        ]);
        _db = new PolDbContext(options, modules);
    }

    [Fact]
    public void Every_mapped_entity_lands_in_an_allowed_schema()
    {
        var offenders = new List<string>();
        foreach (var entity in _db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
                continue; // owned/complex type sharing its owner's table — no schema of its own.

            var schema = entity.GetSchema();
            if (schema is null || !AllowedSchemas.Contains(schema))
                offenders.Add($"{entity.ClrType.FullName} -> {schema ?? "(null)"}.{table}");
        }

        Assert.True(
            offenders.Count == 0,
            "Every entity must map to an allowed schema {shop, checkout, txn, admin, merch, iam, cfg, acct, access, oauth, dbo}. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_DataProtectionKeys_lives_in_dbo()
    {
        var dboTables = _db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.GetSchema() == SchemaNames.Dbo)
            .Select(e => e.GetTableName()!)
            .Distinct()
            .ToList();

        Assert.True(
            dboTables.Count == 1 && dboTables[0] == DboException,
            $"dbo is the single named exception and must hold only '{DboException}'. Found: "
            + string.Join(", ", dboTables));
    }

    [Fact]
    public void Every_Iam_entity_maps_to_the_iam_schema()
    {
        var offenders = _db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Where(e => IsIamDomain(e.ClrType))
            .Where(e => e.GetSchema() != SchemaNames.Iam)
            .Select(e => $"{e.ClrType.FullName} -> {e.GetSchema() ?? "(null)"}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Every Iam.Domain entity must map to the '{SchemaNames.Iam}' schema. Offenders: "
            + string.Join(", ", offenders));

        // Fail loud if the Iam catalog silently disappeared from the model (a vacuous pass otherwise):
        // the RBAC catalog plus API-client credential owner tables must all be present.
        var iamTables = _db.Model.GetEntityTypes()
            .Where(e => IsIamDomain(e.ClrType) && e.GetTableName() is not null)
            .Select(e => e.GetTableName()!)
            .ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "ApiClients", "OneTimeSecretTickets",
                "Permissions", "PermissionGroups", "Roles", "RolePermissions",
            },
            iamTables);
    }

    // Packaging merges domain assemblies; the published RBAC catalog remains confined by its namespace.
    private static bool IsIamDomain(Type clrType) => clrType.Namespace?.StartsWith("Iam.Domain", StringComparison.Ordinal) == true;

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
