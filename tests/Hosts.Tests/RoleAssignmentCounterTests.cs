extern alias ApiHost;

using ApiHost::Api.Iam;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;

namespace Hosts.Tests;

/// <summary>
/// <see cref="HostRoleAssignmentCounter"/> scopes counts to the caller's <see cref="RoleSideContext"/>
/// (Codex P1): a Merchant console must see only its OWN merchant's assignment rows for a shared role —
/// the global total would leak how many users OTHER merchants have bound (REQ-3.6). Real
/// <see cref="PolDbContext"/> over in-memory SQLite (the <c>Iam.Tests.RoleStoreListTests</c> recipe) so the
/// actual EF predicates run, not fakes. The model loads only Iam + Merchants: SQLite has no schemas, so
/// <c>admin.RoleAssignments</c>/<c>merch.RoleAssignments</c> (and <c>AuthAudits</c>) collide by table name in
/// one model — the Platform branch (pre-rf2 global admin+merch sum, unchanged by the fix) therefore stays
/// covered by the live-SQL integration tier, and only the NEW merchant-scoped branch is pinned here.
/// </summary>
public sealed class RoleAssignmentCounterTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, Scope> NoCatalog = new Dictionary<string, Scope>();

    private readonly SqliteConnection _connection;
    private readonly PolDbContext _db;

    private readonly Guid _merchantA = Guid.NewGuid();
    private readonly Guid _merchantB = Guid.NewGuid();
    private readonly Role _shared;
    private readonly Role _unassigned;

    public RoleAssignmentCounterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_connection).Options,
            new ModuleAssemblies([
                typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
                typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
            ]));
        // Not EnsureCreated: the full Merchants model carries SQL Server-only DDL (nvarchar(max) on
        // merch.Merchants.Metadata) that SQLite cannot parse. Only the five RBAC tables this counter
        // actually touches are created, straight from EF's own generated script.
        string[] needed = ["\"Roles\"", "\"RolePermissions\"", "\"Permissions\"", "\"PermissionGroups\"", "\"RoleAssignments\""];
        foreach (var statement in _db.Database.GenerateCreateScript()
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(s => s.Length > 0 && needed.Any(s.Contains)))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }

        _shared = Role.Create("shared_role", "Shared", null, null, RoleStatus.Active, Scope.Merchant, null, [], NoCatalog);
        _unassigned = Role.Create("lonely", "Lonely", null, null, RoleStatus.Active, Scope.Merchant, null, [], NoCatalog);
        _db.AddRange(_shared, _unassigned);

        // The shared role is bound twice in merchant A and once in merchant B.
        _db.AddRange(
            MerchantRoleAssignment.Create(Guid.NewGuid(), _shared.Id, _merchantA, Guid.NewGuid(), DateTime.UtcNow),
            MerchantRoleAssignment.Create(Guid.NewGuid(), _shared.Id, _merchantA, Guid.NewGuid(), DateTime.UtcNow),
            MerchantRoleAssignment.Create(Guid.NewGuid(), _shared.Id, _merchantB, Guid.NewGuid(), DateTime.UtcNow));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private HostRoleAssignmentCounter Counter() => new(_db);

    [Fact]
    public async Task Merchant_count_covers_only_its_own_merchants_assignments()
    {
        Assert.Equal(2, await Counter().CountAsync(RoleSideContext.Merchant(_merchantA), _shared.Id, CancellationToken.None));
        Assert.Equal(1, await Counter().CountAsync(RoleSideContext.Merchant(_merchantB), _shared.Id, CancellationToken.None));
        Assert.Equal(0, await Counter().CountAsync(RoleSideContext.Merchant(Guid.NewGuid()), _shared.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CountMany_applies_the_same_merchant_scoping_and_zero_fills_absent_roles()
    {
        var counts = await Counter().CountManyAsync(
            RoleSideContext.Merchant(_merchantA), [_shared.Id, _unassigned.Id], CancellationToken.None);

        Assert.Equal(2, counts[_shared.Id]);
        Assert.Equal(0, counts[_unassigned.Id]);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
