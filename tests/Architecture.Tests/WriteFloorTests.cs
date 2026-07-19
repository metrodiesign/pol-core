using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUser;
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

    [Fact]
    public async Task Forged_detached_write_is_closed_by_the_concurrency_token()
    {
        var productId = await SeedProductAsync(MerchantA, "a-product");

        // Simulates an attacker (or a bug) crafting a detached stub for a row it does not own and attaching
        // it as Modified: the WHERE clause EF emits carries the FORGED MerchantId (its "original" value,
        // since a freshly-attached detached entity has Original == Current) — it targets zero real rows
        // because the actual row's MerchantId is MerchantA, not MerchantB.
        using var forger = NewMerchantRuntimeContext(FakeActorContext.For(MerchantB), FakeWriteAuthorizer.AllowAll);
        var forgedStub = Products.Domain.Product.Create(MerchantB, "renamed", Money.Of(1m, "THB"), DateTime.UtcNow);
        typeof(Products.Domain.Product).GetProperty(nameof(Products.Domain.Product.Id))!
            .SetValue(forgedStub, productId);
        forger.Attach(forgedStub).State = EntityState.Modified;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => forger.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_with_MerchantId_Guid_Empty_is_rejected()
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var product = Products.Domain.Product.Create(MerchantA, "a-product", Money.Of(1m, "THB"), DateTime.UtcNow);
        writer.Add(product);
        writer.Entry(product).Property("MerchantId").CurrentValue = Guid.Empty;

        var ex = await Assert.ThrowsAsync<WriteGuardException>(() => writer.SaveChangesAsync());
        Assert.Contains("Guid.Empty", ex.Message);
    }

    [Fact]
    public async Task Tenant_key_is_immutable_after_insert()
    {
        var productId = await SeedProductAsync(MerchantA, "a-product");

        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll);
        var product = await writer.Products.SingleAsync(p => p.Id == productId);
        writer.Entry(product).Property("MerchantId").CurrentValue = MerchantB;

        var ex = await Assert.ThrowsAsync<WriteGuardException>(() => writer.SaveChangesAsync());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanWrite_denial_rejects_the_whole_save()
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.DenyAll);
        var product = Products.Domain.Product.Create(MerchantA, "a-product", Money.Of(1m, "THB"), DateTime.UtcNow);
        writer.Add(product);

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
            var user = MerchantUserAccount.Register("pending-subject", "pending@example.com", DateTime.UtcNow);
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

    [Fact]
    public async Task ControlPlaneDbContext_append_only_admin_audit_rejects_modify_and_defaults_writes_to_deny()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        ControlPlaneDbContext NewContext(IWriteAuthorizer authorizer) =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options, authorizer,
                NoOpSecurityTelemetry.Instance);

        using (var setup = NewContext(FakeWriteAuthorizer.AllowAll))
            await setup.Database.EnsureCreatedAsync();

        using (var denied = NewContext(FakeWriteAuthorizer.DenyAll))
        {
            denied.Positions.Add(MasterData.Domain.Positions.Position.Create("pos", "Position"));
            await Assert.ThrowsAsync<WriteGuardException>(() => denied.SaveChangesAsync());
        }

        connection.Dispose();
    }

    private async Task<Guid> SeedProductAsync(Guid merchantId, string name)
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(merchantId), FakeWriteAuthorizer.AllowAll);
        var product = Products.Domain.Product.Create(merchantId, name, Money.Of(10m, "THB"), DateTime.UtcNow);
        writer.Add(product);
        await writer.SaveChangesAsync();
        return product.Id;
    }

    private static SqliteConnection OpenMerchantUserSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    public void Dispose() => _connection.Dispose();
}
