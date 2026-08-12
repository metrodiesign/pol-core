using Iam.Domain.Roles;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Live-SQL floor under the rf2 per-request permission resolution and the RBAC edge cases (REQ-3/4/7/8). Every
/// query here mirrors the resolution repositories (<c>Admins/Merchants.Infrastructure...RoleRepository</c>) so
/// the DB proves what the app-layer visibility filters rest on: a merchant sees only its own + shared roles,
/// an assignment pointing at another merchant's role contributes nothing (defense-in-depth), deactivation takes
/// effect on the next resolution (no cache), the two assignment tables never point cross-side (drift guard),
/// role gives action independently of Tier (orthogonality), and bootstrap binds platform_admin by code
/// idempotently. Tagged Integration: skipped by the default unit run.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IamCatalogCollection.Name)]
public sealed class IamRoleResolutionTests
{
    // Mirrors Merchants.Infrastructure...RoleRepository.ListEffectivePermissionsAsync (RoleVisibility.For(Merchant,m)
    // + Status=Active): union of keys over the user's assignments in THIS merchant, joined to roles that are
    // Merchant-scoped, Active, and visible to the merchant (own or shared).
    private const string MerchEffectiveSql =
        """
        SELECT DISTINCT p.PermissionKey
        FROM merch.RoleAssignments a
        JOIN iam.Roles r ON a.RoleId = r.Id
        JOIN iam.RolePermissions p ON p.RoleId = r.Id
        WHERE a.UserId = @u AND a.MerchantId = @m
          AND r.Status = 1 AND r.Scope = 2 AND (r.MerchantId IS NULL OR r.MerchantId = @m);
        """;

    // Mirrors Admins.Infrastructure...RoleRepository.ListEffectivePermissionsAsync (RoleVisibility.For(Platform,null)
    // + Status=Active): Platform-scope, shared, Active roles only.
    private const string AdminEffectiveSql =
        """
        SELECT DISTINCT p.PermissionKey
        FROM admin.RoleAssignments a
        JOIN iam.Roles r ON a.RoleId = r.Id
        JOIN iam.RolePermissions p ON p.RoleId = r.Id
        WHERE a.AdminUserId = @u
          AND r.Status = 1 AND r.Scope = 1 AND r.MerchantId IS NULL;
        """;

    [Fact]
    public async Task Merchant_effective_permissions_union_only_active_visible_roles()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var user = Guid.NewGuid();
        var merchant = IntegrationDb.MerchantA;
        var activeRole = Guid.NewGuid();
        var inactiveRole = Guid.NewGuid();

        try
        {
            // One Active custom role (payment.redirect) + one Inactive one (payment.create), both this merchant's own.
            await InsertMerchRole(admin, activeRole, "res_a_" + activeRole.ToString("N")[..6], status: 1, merchant);
            await InsertMerchRole(admin, inactiveRole, "res_i_" + inactiveRole.ToString("N")[..6], status: 2, merchant);
            await Grant(admin, activeRole, "payment.redirect");
            await Grant(admin, inactiveRole, "payment.create");
            await InsertMerchAssignment(admin, user, activeRole, merchant);
            await InsertMerchAssignment(admin, user, inactiveRole, merchant);

            // The Inactive role contributes nothing (REQ-4.6); a merchant the user was never approved into is empty.
            Assert.Equal(new[] { "payment.redirect" }, await Effective(admin, MerchEffectiveSql, user, merchant));
            Assert.Empty(await Effective(admin, MerchEffectiveSql, user, IntegrationDb.MerchantB));
        }
        finally
        {
            await CleanupMerchUser(admin, user, activeRole, inactiveRole);
        }
    }

    [Fact]
    public async Task Deactivating_a_role_drops_its_permissions_on_the_next_resolution()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var user = Guid.NewGuid();
        var merchant = IntegrationDb.MerchantA;
        var role = Guid.NewGuid();

        try
        {
            await InsertMerchRole(admin, role, "rev_" + role.ToString("N")[..6], status: 1, merchant);
            await Grant(admin, role, "payment.create");
            await InsertMerchAssignment(admin, user, role, merchant);
            Assert.Equal(new[] { "payment.create" }, await Effective(admin, MerchEffectiveSql, user, merchant));

            // No cache: deactivating the role removes it from the very next resolution (REQ-4.4).
            await IntegrationDb.ExecAsync(admin, "UPDATE iam.Roles SET Status=2 WHERE Id=@id", ("@id", role));
            Assert.Empty(await Effective(admin, MerchEffectiveSql, user, merchant));
        }
        finally
        {
            await CleanupMerchUser(admin, user, role);
        }
    }

    [Fact]
    public async Task A_merchant_assignment_pointing_at_another_merchants_role_does_not_contribute()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var user = Guid.NewGuid();
        var role = Guid.NewGuid();

        try
        {
            // Role owned by Merchant B, but an assignment row (bypassing validation) says the user is in Merchant A.
            await InsertMerchRole(admin, role, "did_" + role.ToString("N")[..6], status: 1, IntegrationDb.MerchantB);
            await Grant(admin, role, "payment.create");
            await InsertMerchAssignment(admin, user, role, IntegrationDb.MerchantA);

            // Defense-in-depth (REQ-4.2): resolution for Merchant A joins through RoleVisibility for A, which
            // excludes B's role — so nothing is contributed even though the assignment row itself carries A.
            Assert.Empty(await Effective(admin, MerchEffectiveSql, user, IntegrationDb.MerchantA));
        }
        finally
        {
            await CleanupMerchUser(admin, user, role);
        }
    }

    [Fact]
    public async Task Merchant_B_cannot_see_merchant_A_custom_role()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var roleA = Guid.NewGuid();
        var code = "iso_" + roleA.ToString("N")[..6];

        try
        {
            await InsertMerchRole(admin, roleA, code, status: 1, IntegrationDb.MerchantA);

            // The merchant-side visible set (RoleVisibility.For(Merchant, m) = Scope=Merchant AND MerchantId IN
            // {NULL, m}) — REQ-3.6. B does not see A's custom role; A does; both see the shared seeds.
            var visibleToB = await VisibleMerchantRoleCodes(admin, IntegrationDb.MerchantB);
            var visibleToA = await VisibleMerchantRoleCodes(admin, IntegrationDb.MerchantA);
            Assert.DoesNotContain(code, visibleToB);
            Assert.Contains(code, visibleToA);
            Assert.Contains("merchant_manager", visibleToB);
            Assert.Contains("merchant_staff", visibleToB);
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE iam.Roles WHERE Id=@id", ("@id", roleA));
        }
    }

    [Fact]
    public async Task A_shared_seed_role_is_not_in_any_merchants_owned_mutable_set()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // The DB floor under the 409 for "merchant edits a shared seed role" (REQ-3.8): the store's mutation
        // filter matches only MerchantId = @m; the shared anchor (MerchantId NULL) is never in that owned set,
        // so a merchant-scoped update/delete matches zero rows and the handler returns 409.
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM iam.Roles WHERE Code=N'merchant_staff' AND MerchantId=@m",
            ("@m", IntegrationDb.MerchantA))));
    }

    [Fact]
    public async Task Platform_admin_role_grants_every_action_key_regardless_of_tier()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var user = Guid.NewGuid();
        var platformAdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        try
        {
            // Orthogonality (REQ-8.2/8.3): a SCOPED-tier admin (Tier=1) holding platform_admin still resolves the
            // full 18-key Platform action set after Admin console permissions land — action comes
            // from the role, not the Tier. (Its narrow VISIBILITY under fn_merchant_predicate is the separate
            // axis, covered by RlsIsolationTests.)
            await IntegrationDb.InsertPlatformUserAsync(admin, user, "orth-" + user.ToString("N")[..8],
                user.ToString("N")[..8] + "@example.com", tier: 1, status: 1);
            await InsertAdminAssignment(admin, user, platformAdminId);
            Assert.Equal(18, (await Effective(admin, AdminEffectiveSql, user, Guid.Empty)).Length);
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE admin.RoleAssignments WHERE AdminUserId=@u", ("@u", user));
        }
    }

    [Fact]
    public async Task Tier_alone_grants_no_action_and_admin_ignores_merchant_scope_roles()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var superNoRole = Guid.NewGuid();
        var adminWithMerchRole = Guid.NewGuid();
        var merchRole = Guid.NewGuid();

        try
        {
            // A SUPER user with no role assignment has zero effective permissions — no Super-bypass (REQ-8.3).
            await IntegrationDb.InsertPlatformUserAsync(admin, superNoRole, "sup-" + superNoRole.ToString("N")[..8],
                superNoRole.ToString("N")[..8] + "@example.com", tier: 2, status: 1);
            Assert.Empty(await Effective(admin, AdminEffectiveSql, superNoRole, Guid.Empty));

            // And even if a Merchant-scope role were assigned via admin.RoleAssignments (a bypassed write), the
            // Platform resolution's Scope=1 filter drops it — the platform side can never resolve a merchant role
            // (REQ-3.4 floor).
            await IntegrationDb.InsertPlatformUserAsync(admin, adminWithMerchRole, "amr-" + adminWithMerchRole.ToString("N")[..8],
                adminWithMerchRole.ToString("N")[..8] + "@example.com", tier: 2, status: 1);
            await InsertMerchRole(admin, merchRole, "amr_" + merchRole.ToString("N")[..6], status: 1, merchantId: null);
            await Grant(admin, merchRole, "payment.create");
            await InsertAdminAssignment(admin, adminWithMerchRole, merchRole);
            Assert.Empty(await Effective(admin, AdminEffectiveSql, adminWithMerchRole, Guid.Empty));
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE admin.RoleAssignments WHERE AdminUserId IN (@a,@b)",
                ("@a", superNoRole), ("@b", adminWithMerchRole));
            await IntegrationDb.ExecAsync(admin, "DELETE iam.Roles WHERE Id=@id", ("@id", merchRole));
        }
    }

    [Fact]
    public async Task Every_assignment_row_points_at_a_same_side_role()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // Drift guard (REQ-7.6): the single FK to iam.Roles does not constrain scope, so assert at the data level
        // that no assignment row escaped the write-path validation — every admin.* assignment points at a
        // Platform role; every merch.* assignment points at a Merchant role whose MerchantId is NULL or the
        // assignment's own MerchantId.
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            """
            SELECT COUNT(*) FROM admin.RoleAssignments a
            JOIN iam.Roles r ON a.RoleId = r.Id
            WHERE r.Scope <> 1;
            """)));
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            """
            SELECT COUNT(*) FROM merch.RoleAssignments a
            JOIN iam.Roles r ON a.RoleId = r.Id
            WHERE r.Scope <> 2 OR (r.MerchantId IS NOT NULL AND r.MerchantId <> a.MerchantId);
            """)));
    }

    [Fact]
    public async Task Bootstrap_binds_platform_admin_by_code_idempotently()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var user = Guid.NewGuid();

        try
        {
            // Bootstrap resolves the seed anchor BY CODE through the Platform visible set (REQ-8.1), then assigns.
            var roleId = (Guid)(await IntegrationDb.ScalarAsync(admin,
                "SELECT Id FROM iam.Roles WHERE Code=@c AND Scope=1 AND MerchantId IS NULL",
                ("@c", Role.PlatformAdminCode)))!;

            await IntegrationDb.InsertPlatformUserAsync(admin, user, "boot-" + user.ToString("N")[..8],
                user.ToString("N")[..8] + "@example.com", tier: 2, status: 1);
            await InsertAdminAssignment(admin, user, roleId);

            // Idempotent: the unique (AdminUserId, RoleId) index rejects a second bind of the same role — the
            // handler's AssignmentExists check + this DB backstop mean a retry/race never double-assigns.
            await Assert.ThrowsAsync<SqlException>(() => InsertAdminAssignment(admin, user, roleId));

            // The bootstrap account is usable immediately: platform_admin's full 18-key Platform set resolves.
            Assert.Equal(18, (await Effective(admin, AdminEffectiveSql, user, Guid.Empty)).Length);
        }
        finally
        {
            await IntegrationDb.ExecAsync(admin, "DELETE admin.RoleAssignments WHERE AdminUserId=@u", ("@u", user));
        }
    }

    // --- helpers ---

    private static Task InsertMerchRole(SqlConnection c, Guid id, string code, int status, Guid? merchantId) =>
        IntegrationDb.ExecAsync(c,
            "INSERT iam.Roles (Id, Code, Name, Status, Scope, MerchantId) VALUES (@id, @c, N'probe', @s, 2, @m)",
            ("@id", id), ("@c", code), ("@s", status), ("@m", (object?)merchantId ?? DBNull.Value));

    private static Task Grant(SqlConnection c, Guid roleId, string key) =>
        IntegrationDb.ExecAsync(c,
            "INSERT iam.RolePermissions (Id, RoleId, PermissionKey) VALUES (@g, @r, @k)",
            ("@g", Guid.NewGuid()), ("@r", roleId), ("@k", key));

    private static Task InsertMerchAssignment(SqlConnection c, Guid user, Guid role, Guid merchant) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT merch.RoleAssignments (Id, UserId, RoleId, MerchantId, AssignedById, AssignedAt)
            VALUES (@id, @u, @r, @m, @by, SYSUTCDATETIME());
            """,
            ("@id", Guid.NewGuid()), ("@u", user), ("@r", role), ("@m", merchant), ("@by", Guid.NewGuid()));

    private static Task InsertAdminAssignment(SqlConnection c, Guid user, Guid role) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT admin.RoleAssignments (Id, AdminUserId, RoleId, AssignedById, AssignedAt)
            VALUES (@id, @u, @r, @by, SYSUTCDATETIME());
            """,
            ("@id", Guid.NewGuid()), ("@u", user), ("@r", role), ("@by", user));

    private static async Task<string[]> Effective(SqlConnection c, string sql, Guid user, Guid merchant)
    {
        var keys = new List<string>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", user);
        cmd.Parameters.AddWithValue("@m", merchant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys.Add(reader.GetString(0));
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    private static async Task<HashSet<string>> VisibleMerchantRoleCodes(SqlConnection c, Guid merchant)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Code FROM iam.Roles WHERE Scope=2 AND (MerchantId IS NULL OR MerchantId=@m)";
        cmd.Parameters.AddWithValue("@m", merchant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            set.Add(reader.GetString(0));
        return set;
    }

    private static async Task CleanupMerchUser(SqlConnection c, Guid user, params Guid[] roleIds)
    {
        await IntegrationDb.ExecAsync(c, "DELETE merch.RoleAssignments WHERE UserId=@u", ("@u", user));
        foreach (var r in roleIds)
            await IntegrationDb.ExecAsync(c, "DELETE iam.Roles WHERE Id=@id", ("@id", r)); // cascade drops grants
    }
}
