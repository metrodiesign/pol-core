using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Catalog seed + grant posture for the Admin Role RBAC tables (admin-role-rbac REQ-1/3/12). The catalog tables
/// are SELECT-only for pol_admin (dev-seeded by migration, never written at runtime); the role/grant/assignment
/// tables are full CRUD; pol_app sees none of them. A FK from a role's grant to the catalog stops a role ever
/// granting a permission that does not exist. Tagged Integration: the default unit run skips it; CI runs it
/// against live SQL with the migration applied.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminRoleRbacGrantsTests
{
    private const string SuperAdminRoleId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Catalog_seed_matches_the_advertised_shape()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // 6 groups / 16 perms: the merchant_user group (formerly producer) adds merchant_user.approve/reject.
        Assert.Equal(6, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM admin.AdminPermissionGroups")));
        Assert.Equal(16, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM admin.AdminPermissions")));
        Assert.Equal(5, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM admin.AdminRoles")));

        // super_admin holds the full 16 (the +2 merchant_user keys are seed-granted to it); auditor ships Inactive (Status = 1).
        Assert.Equal(16, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.AdminRolePermissions WHERE RoleId=@r", ("@r", Guid.Parse(SuperAdminRoleId)))));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT Status FROM admin.AdminRoles WHERE Code=N'auditor'")));
    }

    [Fact]
    public async Task Admin_has_full_crud_on_roles_but_the_catalog_is_read_only()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var roleId = Guid.NewGuid();
        var code = "it_" + roleId.ToString("N")[..8];

        await IntegrationDb.ExecAsync(admin,
            "INSERT admin.AdminRoles (Id, Code, Name, Color, Status) VALUES (@id, @code, N'IT', 'gray', 0)",
            ("@id", roleId), ("@code", code));
        await IntegrationDb.ExecAsync(admin,
            "INSERT admin.AdminRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @id, 'txn.view')",
            ("@g", Guid.NewGuid()), ("@id", roleId));
        await IntegrationDb.ExecAsync(admin, "UPDATE admin.AdminRoles SET Name=N'IT2' WHERE Id=@id", ("@id", roleId));
        await IntegrationDb.ExecAsync(admin, "DELETE admin.AdminRoles WHERE Id=@id", ("@id", roleId)); // cascade drops the grant

        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.AdminRoles WHERE Id=@id", ("@id", roleId))));

        // Catalog is SELECT-only for pol_admin — a runtime INSERT is refused.
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT admin.AdminPermissions ([Key], GroupKey, LabelTh, SortOrder) VALUES ('x.y','system',N'x',99)"));
    }

    [Fact]
    public async Task A_role_cannot_grant_a_permission_outside_the_catalog()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // FK AdminRolePermissions.PermissionKey -> AdminPermissions.Key (REQ-3.2).
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT admin.AdminRolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, 'bogus.key')",
            ("@g", Guid.NewGuid()), ("@r", Guid.Parse(SuperAdminRoleId))));
    }

    [Fact]
    public async Task App_principal_cannot_touch_the_role_or_catalog_tables()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.AdminRoles"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.AdminPermissions"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.AdminRoleAssignments"));
    }
}
