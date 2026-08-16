using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Users;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// Proves the pre-bind read ports: each resolves BEFORE any actor is bound (REQ-1.4-adjacent — no
/// <see cref="IActorContext"/> dependency at all), returns a narrow projection (not the tracked aggregate),
/// and — for the merchant account resolver specifically (<c>IAccountResolver</c>,
/// bugfix-merchant-prebind-wiring F1/F6) — the sanctioned <c>IgnoreQueryFilters()</c> genuinely defeats what
/// would otherwise be a 100% blind read (an unbound caller's <c>CurrentMerchant</c> is
/// <see cref="Guid.Empty"/>, which never matches any real row).
/// </summary>
public sealed class PreBindReadPortTests
{
    [Fact]
    public async Task Admin_session_by_token_hash_returns_a_narrow_projection()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        ControlPlaneDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
                FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        var hash = System.Security.Cryptography.SHA256.HashData("token-1"u8.ToArray());
        Guid sessionId, adminId;
        using (var writer = NewContext())
        {
            var admin = User.SelfProvision("google", "g-sub-1", "ops@example.com", DateTime.UtcNow);
            writer.Users.Add(admin);
            var session = Session.Start(admin.Id, hash, DateTime.UtcNow,
                new SessionPolicy(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)));
            writer.Sessions.Add(session);
            await writer.SaveChangesAsync();
            sessionId = session.Id;
            adminId = admin.Id;
        }

        using var reader = NewContext();
        var lookup = await new AdminSessionByTokenHash(reader).FindByTokenHashAsync(hash, CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal(sessionId, lookup!.SessionId);
        Assert.Equal(adminId, lookup.OwnerId);
        Assert.Equal(SessionLookupStatus.Active, lookup.Status);
    }

    [Fact]
    public async Task Merchant_session_by_token_hash_returns_a_narrow_projection()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        var hash = System.Security.Cryptography.SHA256.HashData("token-2"u8.ToArray());
        Guid sessionId, userId;
        using (var writer = NewContext())
        {
            var user = MerchantUserAccount.Register("google", "m-sub-1", "merchant@example.com", DateTime.UtcNow);
            writer.Users.Add(user);
            var session = Merchants.Domain.Users.Session.Start(user.Id, hash, DateTime.UtcNow,
                new Merchants.Domain.Users.SessionPolicy(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)));
            writer.Sessions.Add(session);
            await writer.SaveChangesAsync();
            sessionId = session.Id;
            userId = user.Id;
        }

        using var reader = NewContext();
        var lookup = await new MerchantSessionByTokenHash(reader).FindByTokenHashAsync(hash, CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal(sessionId, lookup!.SessionId);
        Assert.Equal(userId, lookup.OwnerId);
        Assert.Equal(SessionLookupStatus.Active, lookup.Status);
    }

    [Fact]
    public async Task Merchant_login_by_subject_resolves_a_still_pending_applicant()
    {
        // REQ-11.7-adjacent: a pending row (MerchantId NULL) is normally invisible under the read floor to
        // ANY bound merchant actor — but login-by-subject runs with NO actor bound at all, so without the
        // sanctioned IgnoreQueryFilters escape hatch it would ALSO be invisible to itself (CurrentMerchant
        // resolves to Guid.Empty, which never matches NULL either) and login could never discover it.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        using (var writer = NewContext())
        {
            writer.Users.Add(MerchantUserAccount.Register("google", "m-sub-2", "pending@example.com", DateTime.UtcNow));
            await writer.SaveChangesAsync();
        }

        using var reader = NewContext();
        var lookup = await new MerchantAccountResolver(reader).FindBySubjectAsync("google", "m-sub-2", CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal("pending@example.com", lookup!.Email);
        Assert.Null(lookup.MerchantId);
        Assert.Equal(Merchants.Domain.Users.UserStatus.PendingApproval, lookup.Status);
    }

    [Fact]
    public async Task The_same_subject_under_a_different_provider_never_matches()
    {
        // REQ-4.2 (microsoft-oidc-ciam-alignment): identity is the PAIR (Provider, Subject) — a Google sub
        // that happens to equal an Entra oid string must not resolve to the other provider's account.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        using (var writer = NewContext())
        {
            writer.Users.Add(MerchantUserAccount.Register("google", "shared-subject", "g@example.com", DateTime.UtcNow));
            await writer.SaveChangesAsync();
        }

        using var reader = NewContext();
        Assert.NotNull(await new MerchantAccountResolver(reader).FindBySubjectAsync("google", "shared-subject", CancellationToken.None));
        Assert.Null(await new MerchantAccountResolver(reader).FindBySubjectAsync("microsoft", "shared-subject", CancellationToken.None));
    }

    [Fact]
    public async Task Merchant_account_by_id_resolves_without_any_actor_bound()
    {
        // The session auth handler re-resolves the caller by id DURING authentication — before the
        // merchant_id claim exists (bugfix-merchant-prebind-wiring F6).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var writer = NewContext())
        {
            var user = MerchantUserAccount.Register("google", "m-sub-4", "byid@example.com", DateTime.UtcNow);
            user.Approve(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), DateTime.UtcNow);
            writer.Users.Add(user);
            await writer.SaveChangesAsync();
            userId = user.Id;
        }

        using var reader = NewContext();
        var lookup = await new MerchantAccountResolver(reader).FindByIdAsync(userId, CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal("m-sub-4", lookup!.Subject);
        Assert.Equal(Merchants.Domain.Users.UserStatus.Active, lookup.Status);
    }

    [Fact]
    public async Task Merchant_login_by_subject_resolves_an_already_bound_user_regardless_of_which_merchant()
    {
        var merchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        using (var writer = NewContext())
        {
            var user = MerchantUserAccount.Register("google", "m-sub-3", "bound@example.com", DateTime.UtcNow);
            user.Approve(merchantA, DateTime.UtcNow);
            writer.Users.Add(user);
            await writer.SaveChangesAsync();
        }

        // A totally unbound caller (no merchant claim at all) must still resolve this — the whole point of
        // login-by-subject is DISCOVERING which merchant, before any actor is bound.
        using var reader = NewContext();
        var lookup = await new MerchantAccountResolver(reader).FindBySubjectAsync("google", "m-sub-3", CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal(merchantA, lookup!.MerchantId);
    }
}
