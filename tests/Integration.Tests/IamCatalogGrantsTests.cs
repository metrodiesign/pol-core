using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Static posture of the single central <c>iam.*</c> catalog against live SQL Server (rf2 REQ-2/9/10): the seeded
/// rows equal the code-canonical <see cref="Keys"/> vocabulary (drift guard), the grant matrix is exactly the
/// design's least-privilege shape (pol_admin SELECT on the vocabulary + CRUD on Roles/RolePermissions; pol_app
/// nothing on <c>iam.*</c>), no RLS policy covers the schema, the catalog FK rejects a phantom-key grant, the
/// shared-NULL bucket is unique on Code, and the reset dropped the two legacy per-side catalogs. Tagged
/// Integration: the default unit run skips it; the runbook applies the migration to live SQL first.
/// The behavioural 409/400 role-management responses are unit-covered in Iam.Tests over a real PolDbContext —
/// this suite pins only what live SQL Server uniquely establishes.
/// </summary>
/// <remarks>Shares the <see cref="IamCatalogCollection"/> so it runs sequentially with
/// <see cref="IamRoleResolutionTests"/> — both mutate the one shared <c>iam.*</c> catalog on the live DB, so the
/// absolute seed-count and SetEquals drift assertions here must not race that class's transient probe rows.</remarks>
[Trait("Category", "Integration")]
[Collection(IamCatalogCollection.Name)]
public sealed class IamCatalogGrantsTests
{
    private static readonly Guid PlatformAdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlatformAuditorRoleId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid MerchantManagerRoleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantStaffRoleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Catalog_seed_matches_the_advertised_shape()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // 8 groups / 20 keys / 4 roles / 28 grants (REQ-2.1/2.3/10.1).
        Assert.Equal(8, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM iam.PermissionGroups")));
        Assert.Equal(20, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM iam.Permissions")));
        Assert.Equal(4, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM iam.Roles")));
        Assert.Equal(28, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM iam.RolePermissions")));

        // Per-role grant counts (design matrix): platform_admin 13, platform_auditor 4, merchant_manager 7,
        // merchant_staff 4.
        Assert.Equal(13, await GrantCount(admin, PlatformAdminRoleId));
        Assert.Equal(4, await GrantCount(admin, PlatformAuditorRoleId));
        Assert.Equal(7, await GrantCount(admin, MerchantManagerRoleId));
        Assert.Equal(4, await GrantCount(admin, MerchantStaffRoleId));

        // The two anchors are Merchant/Platform as planned; all four seed roles are shared (MerchantId NULL) and
        // Active (Status 0). Scope column: 0 = Platform, 1 = Merchant.
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT Scope FROM iam.Roles WHERE Code=N'platform_admin'")));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin, "SELECT Scope FROM iam.Roles WHERE Code=N'merchant_manager'")));
        Assert.Equal(4, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM iam.Roles WHERE MerchantId IS NULL AND Status=0")));
    }

    [Fact]
    public async Task Seeded_catalog_equals_the_code_vocabulary()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // The drift guard (REQ-10.2): the seeded rows are EXACTLY the code-canonical vocabulary in Iam.Domain —
        // keys, groups, group side, and the four role codes all SetEqual their code counterparts.
        var dbKeys = await ReadStrings(admin, "SELECT [Key] FROM iam.Permissions");
        Assert.True(dbKeys.SetEquals(Keys.AllKeys),
            $"key drift: db=[{string.Join(",", dbKeys.Order())}] code=[{string.Join(",", Keys.AllKeys.Order())}]");

        var dbGroups = await ReadStrings(admin, "SELECT [Key] FROM iam.PermissionGroups");
        Assert.True(dbGroups.SetEquals(Keys.GroupKeys),
            $"group drift: db=[{string.Join(",", dbGroups.Order())}] code=[{string.Join(",", Keys.GroupKeys.Order())}]");

        // Each group's side in the DB equals Keys.GroupScope (0 = Platform, 1 = Merchant) — the side-awareness the
        // parity guard and console filters rely on (REQ-2.1).
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = "SELECT [Key], Scope FROM iam.PermissionGroups";
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                Assert.Equal((int)Keys.GroupScope[reader.GetString(0)], reader.GetInt32(1));

        var dbRoles = await ReadStrings(admin, "SELECT Code FROM iam.Roles");
        Assert.True(dbRoles.SetEquals(new[] { "platform_admin", "platform_auditor", "merchant_manager", "merchant_staff" }),
            $"role drift: db=[{string.Join(",", dbRoles.Order())}]");
    }

    [Fact]
    public async Task Admin_has_full_crud_on_roles_but_the_vocabulary_is_read_only()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var roleId = Guid.NewGuid();
        var code = "it_" + roleId.ToString("N")[..8];

        try
        {
            await IntegrationDb.ExecAsync(admin,
                "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @code, N'IT', 0, 0, NULL)",
                ("@id", roleId), ("@code", code));
            await IntegrationDb.ExecAsync(admin,
                "INSERT iam.RolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @id, 'txn.view')",
                ("@g", Guid.NewGuid()), ("@id", roleId));
            await IntegrationDb.ExecAsync(admin, "UPDATE iam.Roles SET Name=N'IT2' WHERE Id=@id", ("@id", roleId));
            await IntegrationDb.ExecAsync(admin, "DELETE iam.Roles WHERE Id=@id", ("@id", roleId)); // cascade drops the grant

            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
                "SELECT COUNT(*) FROM iam.Roles WHERE Id=@id", ("@id", roleId))));
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE iam.Roles WHERE Id=@id", ("@id", roleId));
        }

        // The vocabulary (Permissions/PermissionGroups) is SELECT-only for pol_admin — a runtime INSERT is refused.
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES ('x.y','system',N'x',99)"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT iam.PermissionGroups ([Key], Scope, LabelTh, SortOrder) VALUES ('x', 0, N'x', 99)"));
    }

    [Fact]
    public async Task A_role_cannot_grant_a_permission_outside_the_catalog()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // FK RolePermissions.PermissionKey -> Permissions.Key (REQ-2.6).
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
            "INSERT iam.RolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, 'bogus.key')",
            ("@g", Guid.NewGuid()), ("@r", PlatformAdminRoleId)));
    }

    [Fact]
    public async Task App_principal_cannot_touch_any_iam_table()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // pol_app resolves no permissions and is granted NOTHING on iam.* (REQ-9.1) — every table is denied.
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM iam.PermissionGroups"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM iam.Permissions"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM iam.Roles"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM iam.RolePermissions"));
    }

    [Fact]
    public async Task No_row_level_security_policy_covers_the_iam_schema()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // REQ-9.2: iam.* is deliberately outside RLS (vocabulary is public; Roles/RolePermissions use app-layer
        // visibility). Assert no security predicate targets any object in the iam schema.
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            """
            SELECT COUNT(*)
            FROM sys.security_predicates sp
            JOIN sys.objects o ON sp.target_object_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = 'iam';
            """)));
    }

    [Fact]
    public async Task Shared_role_code_is_unique_across_the_null_bucket_but_reusable_per_merchant()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var code = "dup_" + Guid.NewGuid().ToString("N")[..8];
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();

        try
        {
            // Two SHARED (MerchantId NULL) roles with the same Code collide — the unfiltered unique index
            // (MerchantId, Code) treats NULL as a single equal bucket on SQL Server (HasFilter(null), design P2).
            await IntegrationDb.ExecAsync(admin,
                "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @c, N'A', 0, 1, NULL)",
                ("@id", a), ("@c", code));
            await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin,
                "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @c, N'B', 0, 1, NULL)",
                ("@id", b), ("@c", code)));

            // But two DIFFERENT merchants may each own a custom role with the same code — distinct buckets, no
            // 409 leak across tenants (REQ-3.2 rationale).
            await IntegrationDb.ExecAsync(admin,
                "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @c, N'M1', 0, 1, @m)",
                ("@id", Guid.NewGuid()), ("@c", code), ("@m", m1));
            await IntegrationDb.ExecAsync(admin,
                "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @c, N'M2', 0, 1, @m)",
                ("@id", Guid.NewGuid()), ("@c", code), ("@m", m2));
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE iam.Roles WHERE Code=@c", ("@c", code));
        }
    }

    [Fact]
    public async Task Reset_dropped_the_two_legacy_per_side_catalogs()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // REQ-1.4: the eight duplicated admin.*/merch.* catalog tables are gone; only the four iam.* tables remain.
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            """
            SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name IN ('admin','merch')
              AND t.name IN ('Permissions','PermissionGroups','Roles','RolePermissions');
            """)));
        Assert.Equal(4, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            """
            SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'iam'
              AND t.name IN ('Permissions','PermissionGroups','Roles','RolePermissions');
            """)));
    }

    private static async Task<int> GrantCount(SqlConnection c, Guid roleId) =>
        Convert.ToInt32(await IntegrationDb.ScalarAsync(c,
            "SELECT COUNT(*) FROM iam.RolePermissions WHERE RoleId=@r", ("@r", roleId)));

    private static async Task<HashSet<string>> ReadStrings(SqlConnection c, string sql)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            set.Add(reader.GetString(0));
        return set;
    }
}
