using System.Data.Common;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.Provisioning;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 7 provisioning coordinator (design.md "Provisioning UoW — the ONE
/// cross-context write") end to end on SQLite: the two-context transaction is genuinely atomic (a failpoint
/// after either context's SaveChanges rolls back BOTH sides), the authz recheck rejects a suspended/
/// non-Super/stale-version caller, the idempotency ledger makes a same-key replay return the exact stored
/// result without double-provisioning, a payload/caller mismatch on a reused key is rejected, and a genuine
/// concurrent race on the same key produces exactly one winner. The real SQL-Server-only piece (the
/// UPDLOCK/HOLDLOCK table hint itself) is NOT exercised here — SQLite has no equivalent primitive; see the
/// coordinator's own doc comment for what remains proven only by inspection pending task 8's live wiring.
/// </summary>
public sealed class ProvisioningCoordinatorTests : IDisposable
{
    private readonly string _connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private static readonly VaultKeyring TestKeyring = new("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });

    public ProvisioningCoordinatorTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        // Two DbContext models share ONE physical SQLite database (mirrors production: both runtime
        // contexts bound to the same connection/transaction). EnsureCreated() checks "does the database
        // already exist" as a single yes/no — the SECOND call would no-op once the FIRST has created any
        // table at all, so both contexts' tables are created via the lower-level "create tables
        // unconditionally" API instead.
        using var cp = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connectionString).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        cp.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTables();
        using var mr = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connectionString).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        mr.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>().CreateTables();
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static ControlPlaneDbContext ControlPlaneFactory(DbConnection connection) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private static MerchantRuntimeDbContext MerchantRuntimeFactory(DbConnection connection) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private ProvisioningCoordinator NewCoordinator(
        int maxAttempts = 3, Func<ProvisioningCheckpoint, CancellationToken, Task>? checkpoint = null) =>
        new(OpenConnectionAsync, ControlPlaneFactory, MerchantRuntimeFactory, TestKeyring,
            new FixedClock(DateTime.UtcNow), NoOpSecurityTelemetry.Instance, maxAttempts, checkpoint);

    private async Task<Guid> SeedAdminAsync(Tier tier, UserStatus status)
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var db = ControlPlaneFactory(connection);

        var admin = tier == Tier.Super
            ? User.SelfProvision($"sub-{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.com", DateTime.UtcNow)
            : User.CreateScoped($"{Guid.NewGuid():N}@example.com", DateTime.UtcNow);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        if (status == UserStatus.Suspended)
        {
            admin.Suspend(Guid.NewGuid()); // a different acting admin -> self-suspend guard doesn't fire
            db.Users.Update(admin);
            await db.SaveChangesAsync();
        }

        return admin.Id;
    }

    private static ProvisionSpec Spec(string code = "vprivilege", string name = "Test Co") => new(
        MerchantCode: code,
        Name: name,
        Note: null,
        Country: "TH",
        Currency: "THB",
        EnabledChannels: ["web"],
        MerchantMetadataJson: "{}",
        AdminSubject: "admin-sub",
        CorrelationId: "corr-1",
        Connections:
        [
            new ProvisionConnectionSpec(
                Psp: "omise",
                EnabledMethods: "card",
                ConnectionMetadataJson: "{}",
                SecretName: "psp/omise",
                SecretEnvelopeJson: "{\"apiKey\":\"secret-value-1234\"}",
                MaskedSecretHints: new Dictionary<string, string> { ["apiKey"] = "****1234" }),
        ]);

    private async Task<int> CountMerchantsAsync()
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var db = MerchantRuntimeFactory(connection);
        return await db.Merchants.IgnoreQueryFilters().CountAsync();
    }

    private async Task<int> CountLedgerRowsAsync()
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var db = ControlPlaneFactory(connection);
        return await db.ProvisioningOperations.CountAsync();
    }

    [Fact]
    public async Task Successful_provisioning_creates_the_exact_entity_set_across_both_contexts()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var result = await NewCoordinator().ProvisionAsync(Spec(), callerId, expectedAuthorizationVersion: 0, "op-1", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.MerchantId);
        Assert.Single(result.Connections);

        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await using var mr = MerchantRuntimeFactory(connection);
        Assert.Equal(1, await mr.Merchants.IgnoreQueryFilters().CountAsync(m => m.Id == result.MerchantId));
        Assert.Equal(1, await mr.PspConnections.IgnoreQueryFilters().CountAsync(c => c.MerchantId == result.MerchantId));
        Assert.Equal(1, await mr.VaultSecrets.IgnoreQueryFilters().CountAsync(v => v.MerchantId == result.MerchantId));
        Assert.Equal(0, await mr.VaultRevealAudits.IgnoreQueryFilters().CountAsync(a => a.MerchantId == result.MerchantId));
        Assert.Equal(1, await mr.ProvisioningAudits.IgnoreQueryFilters().CountAsync(a => a.MerchantId == result.MerchantId));

        await using var cp = ControlPlaneFactory(connection);
        var ledgerRow = await cp.ProvisioningOperations.SingleAsync(x => x.OperationKey == "op-1");
        Assert.Equal(result.MerchantId, ledgerRow.MerchantId);
        Assert.NotNull(ledgerRow.Result);
    }

    [Fact]
    public async Task A_failpoint_after_the_control_plane_save_rolls_back_the_whole_transaction_atomically()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var coordinator = NewCoordinator(checkpoint: (point, _) =>
            point == ProvisioningCheckpoint.AfterControlPlaneSave
                ? throw new InvalidOperationException("injected failpoint")
                : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ProvisionAsync(Spec(), callerId, 0, "op-fp1", CancellationToken.None));

        Assert.Equal(0, await CountMerchantsAsync());
        Assert.Equal(0, await CountLedgerRowsAsync());
    }

    [Fact]
    public async Task A_failpoint_after_the_merchant_runtime_save_rolls_back_the_whole_transaction_atomically()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var coordinator = NewCoordinator(checkpoint: (point, _) =>
            point == ProvisioningCheckpoint.AfterMerchantRuntimeSave
                ? throw new InvalidOperationException("injected failpoint")
                : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ProvisionAsync(Spec(), callerId, 0, "op-fp2", CancellationToken.None));

        // The ledger row WAS written (ControlPlane save succeeded) but the transaction never committed -> gone too.
        Assert.Equal(0, await CountMerchantsAsync());
        Assert.Equal(0, await CountLedgerRowsAsync());
    }

    [Fact]
    public async Task A_suspended_caller_is_rejected()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Suspended);

        await Assert.ThrowsAsync<WriteGuardException>(
            () => NewCoordinator().ProvisionAsync(Spec(), callerId, expectedAuthorizationVersion: 1, "op-2", CancellationToken.None));

        Assert.Equal(0, await CountMerchantsAsync());
    }

    [Fact]
    public async Task A_scoped_non_super_caller_is_rejected()
    {
        var callerId = await SeedAdminAsync(Tier.Scoped, UserStatus.Active);

        await Assert.ThrowsAsync<WriteGuardException>(
            () => NewCoordinator().ProvisionAsync(Spec(), callerId, expectedAuthorizationVersion: 0, "op-3", CancellationToken.None));

        Assert.Equal(0, await CountMerchantsAsync());
    }

    [Fact]
    public async Task A_stale_authorization_version_is_rejected()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active); // AuthorizationVersion is 0

        await Assert.ThrowsAsync<WriteGuardException>(
            () => NewCoordinator().ProvisionAsync(Spec(), callerId, expectedAuthorizationVersion: 99, "op-4", CancellationToken.None));

        Assert.Equal(0, await CountMerchantsAsync());
    }

    [Fact]
    public async Task A_replay_with_the_same_key_and_payload_returns_the_exact_stored_result_without_double_provisioning()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var spec = Spec();

        var first = await NewCoordinator().ProvisionAsync(spec, callerId, 0, "op-5", CancellationToken.None);
        var replay = await NewCoordinator().ProvisionAsync(spec, callerId, 0, "op-5", CancellationToken.None);

        Assert.Equal(first.MerchantId, replay.MerchantId);
        Assert.Equal(first.Connections[0].PspConnectionId, replay.Connections[0].PspConnectionId);
        Assert.Equal(1, await CountMerchantsAsync()); // NOT double-provisioned
    }

    [Fact]
    public async Task A_replay_with_the_same_key_but_a_different_payload_is_rejected()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        await NewCoordinator().ProvisionAsync(Spec(name: "Original"), callerId, 0, "op-6", CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            NewCoordinator().ProvisionAsync(Spec(name: "Different Payload"), callerId, 0, "op-6", CancellationToken.None));

        Assert.Equal(1, await CountMerchantsAsync());
    }

    [Fact]
    public async Task A_replay_by_a_different_caller_with_the_same_key_is_rejected()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var otherCallerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var spec = Spec();
        await NewCoordinator().ProvisionAsync(spec, callerId, 0, "op-7", CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            NewCoordinator().ProvisionAsync(spec, otherCallerId, 0, "op-7", CancellationToken.None));

        Assert.Equal(1, await CountMerchantsAsync());
    }

    [Fact]
    public async Task Concurrent_attempts_on_the_same_key_produce_exactly_one_winner_and_the_loser_replays_the_stored_result()
    {
        var callerId = await SeedAdminAsync(Tier.Super, UserStatus.Active);
        var spec = Spec();

        var results = await Task.WhenAll(
            NewCoordinator().ProvisionAsync(spec, callerId, 0, "op-race", CancellationToken.None),
            NewCoordinator().ProvisionAsync(spec, callerId, 0, "op-race", CancellationToken.None));

        Assert.Equal(results[0].MerchantId, results[1].MerchantId);
        Assert.Equal(1, await CountMerchantsAsync());
        Assert.Equal(1, await CountLedgerRowsAsync());
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }
}
