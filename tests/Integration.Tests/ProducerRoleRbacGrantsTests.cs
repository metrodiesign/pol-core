using Microsoft.Data.SqlClient;
using Producer.Domain;

namespace Integration.Tests;

/// <summary>
/// Catalog seed + grant posture + effective-permission union for the Producer Role RBAC tables (REQ-15/16). The
/// catalog tables are SELECT-only for pol_admin (seeded by migration, never written at runtime); the
/// role/grant/assignment tables are full CRUD; pol_app sees none of them. A FK from a grant to the catalog stops a
/// role ever granting a permission that does not exist. The union query mirrors
/// <c>ProducerRoleRepository.ListEffectivePermissionsAsync</c>. Tagged Integration: the default unit run skips it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProducerRoleRbacGrantsTests
{
    private const string TenantOwnerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TenantMemberRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task Catalog_seed_matches_the_advertised_shape()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM producer.ProducerPermissionGroups")));
        Assert.Equal(7, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM producer.ProducerPermissions")));
        Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM producer.ProducerRoles")));

        // tenant_owner grants all 7 (the recovery anchor); tenant_member grants the 4 product/payment keys only.
        Assert.Equal(7, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.ProducerRolePermissions WHERE RoleId=@r", ("@r", Guid.Parse(TenantOwnerRoleId)))));
        Assert.Equal(4, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.ProducerRolePermissions WHERE RoleId=@r", ("@r", Guid.Parse(TenantMemberRoleId)))));
    }

    [Fact]
    public async Task Seeded_catalog_keys_equal_the_code_vocabulary()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        var dbKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = "SELECT [Key] FROM producer.ProducerPermissions";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                dbKeys.Add(reader.GetString(0));
        }

        // The drift guard: the seeded rows are exactly ProducerPermissions.AllKeys (REQ-15.2/15.5).
        Assert.True(dbKeys.SetEquals(ProducerPermissions.AllKeys),
            $"catalog drift: db=[{string.Join(",", dbKeys.Order())}] code=[{string.Join(",", ProducerPermissions.AllKeys.Order())}]");
    }

    [Fact]
    public async Task Admin_has_full_crud_on_roles_but_the_catalog_is_read_only()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var roleId = Guid.NewGuid();
        var code = "it_" + roleId.ToString("N")[..8];

        await IntegrationDb.ExecAsync(admin,
            "INSERT producer.ProducerRoles (Id, Code, Name, Color, Status) VALUES (@id, @code, N'IT', 'gray', 0)",
            ("@id", roleId), ("@code", code));
        await IntegrationDb.ExecAsync(admin,
            "INSERT producer.ProducerRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @id, 'product.create')",
            ("@g", Guid.NewGuid()), ("@id", roleId));
        await IntegrationDb.ExecAsync(admin, "UPDATE producer.ProducerRoles SET Name=N'IT2' WHERE Id=@id", ("@id", roleId));
        await IntegrationDb.ExecAsync(admin, "DELETE producer.ProducerRoles WHERE Id=@id", ("@id", roleId)); // cascade drops the grant

        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.ProducerRoles WHERE Id=@id", ("@id", roleId))));

        // Catalog is SELECT-only for pol_admin — a runtime INSERT is refused.
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT producer.ProducerPermissions ([Key], GroupKey, LabelTh, SortOrder) VALUES ('x.y','catalog',N'x',99)"));
    }

    [Fact]
    public async Task A_role_cannot_grant_a_permission_outside_the_catalog()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // FK ProducerRolePermissions.PermissionKey -> ProducerPermissions.Key (REQ-16.2).
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT producer.ProducerRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, 'bogus.key')",
            ("@g", Guid.NewGuid()), ("@r", Guid.Parse(TenantOwnerRoleId))));
    }

    [Fact]
    public async Task App_principal_cannot_touch_the_role_or_catalog_tables()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerRoles"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerPermissions"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerRoleAssignments"));
    }

    [Fact]
    public async Task Effective_permissions_union_only_active_roles_scoped_to_the_tenant()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        var user = Guid.NewGuid();
        var tenant = IntegrationDb.TenantA;
        var activeRole = Guid.NewGuid();
        var inactiveRole = Guid.NewGuid();
        var assignActive = Guid.NewGuid();
        var assignInactive = Guid.NewGuid();

        try
        {
            // One Active role (product.create) + one Inactive role (payment.create), both assigned to the same user
            // in TenantA. The Inactive role must contribute nothing (REQ-16.4).
            await IntegrationDb.ExecAsync(admin,
                "INSERT producer.ProducerRoles (Id, Code, Name, Color, Status) VALUES (@id, @c, N'A', 'gray', 0)",
                ("@id", activeRole), ("@c", "ut_a_" + activeRole.ToString("N")[..6]));
            await IntegrationDb.ExecAsync(admin,
                "INSERT producer.ProducerRoles (Id, Code, Name, Color, Status) VALUES (@id, @c, N'I', 'gray', 1)",
                ("@id", inactiveRole), ("@c", "ut_i_" + inactiveRole.ToString("N")[..6]));
            await IntegrationDb.ExecAsync(admin,
                "INSERT producer.ProducerRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, 'product.create')",
                ("@g", Guid.NewGuid()), ("@r", activeRole));
            await IntegrationDb.ExecAsync(admin,
                "INSERT producer.ProducerRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, 'payment.create')",
                ("@g", Guid.NewGuid()), ("@r", inactiveRole));
            await InsertAssignment(admin, assignActive, user, activeRole, tenant);
            await InsertAssignment(admin, assignInactive, user, inactiveRole, tenant);

            // Mirrors ProducerRoleRepository.ListEffectivePermissionsAsync(user, tenant): JOIN to Active roles only.
            var inTenant = await EffectivePermissions(admin, user, tenant);
            Assert.Equal(new[] { "product.create" }, inTenant);

            // A different tenant the user was never approved into resolves to nothing (REQ-16.4 tenant scope).
            var otherTenant = await EffectivePermissions(admin, user, IntegrationDb.TenantB);
            Assert.Empty(otherTenant);
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE producer.ProducerRoleAssignments WHERE Id IN (@a,@b)",
                ("@a", assignActive), ("@b", assignInactive));
            await IntegrationDb.ExecAsync(admin, "DELETE producer.ProducerRoles WHERE Id IN (@a,@b)",
                ("@a", activeRole), ("@b", inactiveRole)); // cascade drops the grants
        }
    }

    private static Task InsertAssignment(SqlConnection c, Guid id, Guid user, Guid role, Guid tenant) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT producer.ProducerRoleAssignments (Id, TenantUserId, RoleId, TenantId, AssignedByAdminId, AssignedAt)
            VALUES (@id, @u, @r, @t, @by, SYSUTCDATETIME());
            """,
            ("@id", id), ("@u", user), ("@r", role), ("@t", tenant), ("@by", Guid.NewGuid()));

    private static async Task<string[]> EffectivePermissions(SqlConnection c, Guid user, Guid tenant)
    {
        var keys = new List<string>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT rp.PermissionKey
            FROM producer.ProducerRoleAssignments a
            JOIN producer.ProducerRoles r           ON a.RoleId = r.Id AND r.Status = 0
            JOIN producer.ProducerRolePermissions rp ON rp.RoleId = r.Id
            WHERE a.TenantUserId = @u AND a.TenantId = @t;
            """;
        cmd.Parameters.AddWithValue("@u", user);
        cmd.Parameters.AddWithValue("@t", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys.Add(reader.GetString(0));
        return [.. keys.Order()];
    }
}
