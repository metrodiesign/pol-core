using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the Admin control-plane isolation + grant floor against live SQL Server 2025 with the real
/// principals and the SecurityObjects migration applied. The admin tables (Users,
/// MerchantAccess, UserAudits) are control-plane: pol_admin owns them and pol_app has NO
/// grant at all (REQ-3.2). The filtered unique index on Subject rejects a duplicate bound subject but exempts
/// NULL (invited) subjects (REQ-3.1). Tagged Integration so the default unit run skips them; CI runs them
/// against a migrated service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminIsolationIntegrationTests
{
    private const int Super = 1;
    private const int Scoped = 0;
    private const int Active = 0;

    private static int AsInt(object? o) => (int)o!;
    private static string UniqueSubject() => "g-" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@example.com";

    [Fact]
    public async Task Admin_can_insert_and_read_platform_users()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, id, UniqueSubject(), UniqueEmail(), Super, Active);

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.Users WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task App_cannot_read_admin_control_plane_tables()
    {
        // pol_app has NO grant on any admin table — a merchant principal cannot even SELECT them (REQ-3.2).
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.Users"));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.MerchantAccess"));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.UserAudits"));
    }

    [Fact]
    public async Task App_cannot_write_platform_users()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertPlatformUserAsync(app, Guid.NewGuid(), UniqueSubject(), UniqueEmail(), Super, Active));
    }

    [Fact]
    public async Task Duplicate_bound_subject_is_rejected()
    {
        // The filtered unique index stops the same Google identity from being bound twice (REQ-3.1).
        var subject = UniqueSubject();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject, UniqueEmail(), Super, Active);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject, UniqueEmail(), Super, Active));
    }

    [Fact]
    public async Task Null_subject_invites_do_not_collide()
    {
        // Two invited Scoped accounts (NULL subject, distinct emails) must coexist — the unique index is
        // filtered to non-NULL subjects (REQ-3.1).
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, UniqueEmail(), Scoped, Active);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, UniqueEmail(), Scoped, Active);
        // no throw == pass
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        // Email is the invite key — unique across all platform users (REQ-3.1).
        var email = UniqueEmail();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, email, Scoped, Active);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, email, Scoped, Active));
    }
}
