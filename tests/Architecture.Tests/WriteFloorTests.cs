using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Governance.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Merchants.Domain.Users;
using Orders.Domain.Items;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;
using SharedKernel;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter write floor (REQ-2, REQ-3.5, REQ-11.7) on all three runtime contexts:
/// a forged detached write is closed by the concurrency token (not the guard), Guid.Empty is always
/// rejected, a tenant key is immutable after insert except the one pending-approval NULL-&gt;value
/// transition, an append-only entity rejects Modified/Deleted, and <see cref="IWriteAuthorizer"/> is
/// consulted default-deny for every tracked write.
/// </summary>
public sealed class WriteFloorTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public WriteFloorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewMerchantRuntimeContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewMerchantRuntimeContext(IActorContext actor, IWriteAuthorizer authorizer) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options, actor, authorizer,
            NoOpSecurityTelemetry.Instance);

    private static Carts.Domain.Cart NewCart(Guid merchantId) =>
        new(Guid.NewGuid(), merchantId, "77001", DateTime.UtcNow);

    [Fact]
    public async Task Forged_detached_write_is_closed_by_the_concurrency_token()
    {
        var cartId = await SeedCartAsync(MerchantA);

        // Simulates an attacker (or a bug) crafting a detached stub for a row it does not own and attaching
        // it as Modified: the WHERE clause EF emits carries the FORGED MerchantId (its "original" value,
        // since a freshly-attached detached entity has Original == Current) — it targets zero real rows
        // because the actual row's MerchantId is MerchantA, not MerchantB.
        using var forger = NewMerchantRuntimeContext(FakeActorContext.For(MerchantB), FakeWriteAuthorizer.AllowAll);
        var forgedStub = NewCart(MerchantB);
        typeof(Carts.Domain.Cart).GetProperty(nameof(Carts.Domain.Cart.Id))!
            .SetValue(forgedStub, cartId);
        forger.Attach(forgedStub).State = EntityState.Modified;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => forger.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_with_MerchantId_Guid_Empty_is_rejected()
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var cart = NewCart(MerchantA);
        writer.Add(cart);
        writer.Entry(cart).Property("MerchantId").CurrentValue = Guid.Empty;

        var ex = await Assert.ThrowsAsync<WriteGuardException>(() => writer.SaveChangesAsync());
        Assert.Contains("Guid.Empty", ex.Message);
    }

    [Fact]
    public async Task Tenant_key_is_immutable_after_insert()
    {
        // On IdempotencyRecord rather than Cart: Cart.MerchantId participates in a key, so EF blocks the
        // mutation before SaveChanges — the guard would never be reached and the test would prove nothing.
        const string key = "immutable-key";
        using (var seed = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll))
        {
            seed.Add(new BuildingBlocks.Infrastructure.Idempotency.IdempotencyRecord(
                key, MerchantA, "webhook", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var record = await writer.IdempotencyRecords.SingleAsync(r => r.Key == key);
        writer.Entry(record).Property("MerchantId").CurrentValue = MerchantB;

        var ex = await Assert.ThrowsAsync<WriteGuardException>(() => writer.SaveChangesAsync());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanWrite_denial_rejects_the_whole_save()
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.DenyAll);
        // A DenyAll authorizer denies unconditionally, so any tracked write proves the CanWrite gate — Cart is
        // merchant-owned and passes the tenant-key floor, leaving CanWrite as the only reason the save fails.
        writer.Add(NewCart(MerchantA));

        await Assert.ThrowsAsync<WriteGuardException>(() => writer.SaveChangesAsync());
    }

    [Fact]
    public async Task Append_only_entity_accepts_insert_but_rejects_modify_and_delete()
    {
        using (var seed = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll))
        {
            seed.Add(VaultRevealAudit.Append(VaultRevealAudit.Genesis, MerchantA, "psp-secret", 1, DateTime.UtcNow));
            await seed.SaveChangesAsync(); // insert must succeed — append-only bans Modified/Deleted, not Added
        }

        using var modifier = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var audit = await modifier.VaultRevealAudits.SingleAsync();
        modifier.Entry(audit).Property(nameof(VaultRevealAudit.SecretName)).CurrentValue = "tampered";

        var modifyEx = await Assert.ThrowsAsync<WriteGuardException>(() => modifier.SaveChangesAsync());
        Assert.Contains("append-only", modifyEx.Message, StringComparison.OrdinalIgnoreCase);

        using var deleter = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var toDelete = await deleter.VaultRevealAudits.SingleAsync();
        deleter.VaultRevealAudits.Remove(toDelete);

        var deleteEx = await Assert.ThrowsAsync<WriteGuardException>(() => deleter.SaveChangesAsync());
        Assert.Contains("append-only", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // insurance-pivot task 4 (REQ-7.5) — mirrors the VaultRevealAudit guard test above exactly.
    [Fact]
    public async Task OrderItem_reveal_audit_accepts_insert_but_rejects_modify_and_delete()
    {
        using (var seed = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll))
        {
            seed.Add(RevealAudit.For(Guid.NewGuid(), MerchantA, "merchant-user", "user-1", "corr-1", DateTime.UtcNow));
            await seed.SaveChangesAsync(); // insert must succeed — append-only bans Modified/Deleted, not Added
        }

        using var modifier = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var audit = await modifier.OrderItemRevealAudits.SingleAsync();
        modifier.Entry(audit).Property(nameof(RevealAudit.ActorId)).CurrentValue = "tampered";

        var modifyEx = await Assert.ThrowsAsync<WriteGuardException>(() => modifier.SaveChangesAsync());
        Assert.Contains("append-only", modifyEx.Message, StringComparison.OrdinalIgnoreCase);

        using var deleter = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var toDelete = await deleter.OrderItemRevealAudits.SingleAsync();
        deleter.OrderItemRevealAudits.Remove(toDelete);

        var deleteEx = await Assert.ThrowsAsync<WriteGuardException>(() => deleter.SaveChangesAsync());
        Assert.Contains("append-only", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_user_MerchantId_may_transition_NULL_to_a_real_merchant_exactly_once()
    {
        var connection = OpenMerchantUserSqlite();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        using (var setup = NewContext())
            await setup.Database.EnsureCreatedAsync();

        Guid userId;
        using (var write = NewContext())
        {
            var user = MerchantUserAccount.Register("google", "pending-subject", "pending@example.com", DateTime.UtcNow);
            write.Add(user);
            await write.SaveChangesAsync(); // insert with MerchantId still NULL must succeed
            userId = user.Id;
        }

        using (var approve = NewContext())
        {
            var user = await approve.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            user.Approve(MerchantA, DateTime.UtcNow); // domain-level NULL -> MerchantA transition
            await approve.SaveChangesAsync(); // must NOT throw — the one legitimate carve-out
        }

        using (var reforge = NewContext())
        {
            var user = await reforge.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            reforge.Entry(user).Property("MerchantId").CurrentValue = MerchantB; // merchant -> merchant, still forbidden

            var ex = await Assert.ThrowsAsync<WriteGuardException>(() => reforge.SaveChangesAsync());
            Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        connection.Dispose();
    }

    // registration-attempt-history REQ-1.7 — mirrors the VaultRevealAudit guard test above on the
    // MerchantUser cluster: a captured form snapshot is immutable history.
    [Fact]
    public async Task Registration_attempt_accepts_insert_but_rejects_modify_and_delete()
    {
        var connection = OpenMerchantUserSqlite();
        MerchantUserDbContext NewContext() =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
                FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        using (var seed = NewContext())
        {
            await seed.Database.EnsureCreatedAsync();
            var user = MerchantUserAccount.Register("google", "attempt-subject", "attempt@example.com", DateTime.UtcNow);
            user.SetDetails("First", "Last", IdentityType.Individual, null, null, null, null);
            seed.Add(user);
            seed.Add(Merchants.Domain.Users.RegistrationAttempt.Capture(
                user, 1, Merchants.Domain.Users.TicketPurpose.Registration, "attempt@example.com", DateTime.UtcNow));
            await seed.SaveChangesAsync(); // insert must succeed — append-only bans Modified/Deleted, not Added
        }

        using (var modifier = NewContext())
        {
            var attempt = await modifier.RegistrationAttempts.SingleAsync();
            modifier.Entry(attempt).Property(nameof(Merchants.Domain.Users.RegistrationAttempt.FirstName))
                .CurrentValue = "tampered";

            var modifyEx = await Assert.ThrowsAsync<WriteGuardException>(() => modifier.SaveChangesAsync());
            Assert.Contains("append-only", modifyEx.Message, StringComparison.OrdinalIgnoreCase);
        }

        using (var deleter = NewContext())
        {
            var toDelete = await deleter.RegistrationAttempts.SingleAsync();
            deleter.RegistrationAttempts.Remove(toDelete);

            var deleteEx = await Assert.ThrowsAsync<WriteGuardException>(() => deleter.SaveChangesAsync());
            Assert.Contains("append-only", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
        }

        connection.Dispose();
    }

    [Fact]
    public async Task ControlPlaneDbContext_append_only_admin_audit_rejects_modify_and_defaults_writes_to_deny()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        ControlPlaneDbContext NewContext(IWriteAuthorizer authorizer) =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options, authorizer,
                NoOpSecurityTelemetry.Instance);

        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        using (var setup = NewContext(FakeWriteAuthorizer.AllowAll))
        {
            await setup.Database.EnsureCreatedAsync();
            var head = AuditHead.Create("platform", GovernanceScopeKind.Platform, null, now);
            var audit = AuditRecord.Append(
                "platform", GovernanceScopeKind.Platform, null, 1, AuditRecord.Genesis, Guid.NewGuid(),
                "admin.test.audit", "admin", Guid.NewGuid().ToString("D"),
                "succeeded", "{}", null, "v2", "corr-write-floor", now);
            head.Advance(audit.Sequence, audit.PreviousHash, audit.Hash, now);
            setup.AuditHeads.Add(head);
            setup.AuditRecords.Add(audit);
            await setup.SaveChangesAsync();
        }

        using (var modifier = NewContext(FakeWriteAuthorizer.AllowAll))
        {
            var audit = await modifier.AuditRecords.SingleAsync();
            modifier.Entry(audit).Property(x => x.Result).CurrentValue = "tampered";

            var error = await Assert.ThrowsAsync<WriteGuardException>(() => modifier.SaveChangesAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        using (var deleter = NewContext(FakeWriteAuthorizer.AllowAll))
        {
            deleter.AuditRecords.Remove(await deleter.AuditRecords.SingleAsync());

            var error = await Assert.ThrowsAsync<WriteGuardException>(() => deleter.SaveChangesAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        using (var denied = NewContext(FakeWriteAuthorizer.DenyAll))
        {
            denied.Positions.Add(global::Positions.Domain.Position.Create("pos", "Position"));
            await Assert.ThrowsAsync<WriteGuardException>(() => denied.SaveChangesAsync());
        }

        connection.Dispose();
    }

    private async Task<Guid> SeedCartAsync(Guid merchantId)
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(merchantId), FakeWriteAuthorizer.AllowAll);
        var cart = NewCart(merchantId);
        writer.Add(cart);
        await writer.SaveChangesAsync();
        return cart.Id;
    }

    private static SqliteConnection OpenMerchantUserSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    public void Dispose() => _connection.Dispose();
}
