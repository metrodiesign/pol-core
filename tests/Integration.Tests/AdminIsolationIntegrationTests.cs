using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the admin.Users constraint floor against live SQL Server 2025 at migration HEAD. The filtered unique
/// index on (Provider, TenantId, Subject) rejects a duplicate bound Google subject but exempts NULL subjects;
/// Email is nullable/non-unique contact data. Updated for rls-to-query-filter task 8: pol_admin is retired — pol_app
/// is the sole runtime principal and holds full CRUD on admin.Users/MerchantAccess/UserAudits now (it IS the
/// control-plane's own connection in production), so the old "pol_app has no grant on admin tables" isolation
/// tests are gone — there is only one principal left. Tagged Integration so the default unit run skips them;
/// CI runs them against a migrated service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminIsolationIntegrationTests
{
    private const int Super = 2;
    private const int Scoped = 1;
    private const int Active = 1;

    private static int AsInt(object? o) => (int)o!;
    private static string UniqueSubject() => "g-" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@example.com";

    [Fact]
    public async Task Admin_can_insert_and_read_platform_users()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, id, UniqueSubject(), UniqueEmail(), Super, Active);

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.Users WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Duplicate_bound_subject_is_rejected()
    {
        // The filtered unique index stops the same Google identity from being bound twice (REQ-3.1).
        var subject = UniqueSubject();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject, UniqueEmail(), Super, Active);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject, UniqueEmail(), Super, Active));
    }

    [Fact]
    public async Task Null_subject_invites_do_not_collide()
    {
        // Two invited Scoped accounts (NULL subject, distinct emails) must coexist — the unique index is
        // filtered to non-NULL subjects (REQ-3.1).
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, UniqueEmail(), Scoped, Active);
        await IntegrationDb.InsertPlatformUserAsync(admin, Guid.NewGuid(), subject: null, UniqueEmail(), Scoped, Active);
        // no throw == pass
    }

    [Fact]
    public async Task Duplicate_email_contacts_are_allowed_for_distinct_identity_rows()
    {
        var email = UniqueEmail();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.InsertPlatformUserAsync(admin, firstId, subject: null, email, Scoped, Active);
        await IntegrationDb.InsertPlatformUserAsync(admin, secondId, subject: null, email, Scoped, Active);

        Assert.Equal(2, AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.Users WHERE Id IN (@first, @second) AND Email = @email;",
            ("@first", firstId), ("@second", secondId), ("@email", email))));
    }
}
