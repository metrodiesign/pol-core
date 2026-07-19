using System.Security.Cryptography;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Vault;

namespace Architecture.Tests;

/// <summary>
/// Observable contract of <see cref="LocalEnvelopeVaultStore"/> + the key-rotation behaviour (ported from
/// <c>BuildingBlocks.Tests.LocalEnvelopeVaultStoreTests</c> onto <see cref="MerchantRuntimeDbContext"/>, task
/// 8.5.8 — the class moved into this assembly and is internal, so only a project with InternalsVisibleTo can
/// construct it directly): a stored secret reveals back verbatim; the masked view shows only **** + last4; a
/// blob written under an OLD key id still reveals after the active key rolls; an unknown key id fails CLOSED;
/// and re-wrap moves a blob onto the active key without ever exposing the plaintext. Encryption internals are
/// not asserted — only the seam.
/// </summary>
public sealed class LocalEnvelopeVaultStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public LocalEnvelopeVaultStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(Merchant), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);

    private static VaultKeyring Keyring(string activeId, params (string Id, byte[] Key)[] keys) =>
        new(activeId, keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal));

    private static VaultKeyring SingleKeyring(byte[] key) =>
        Keyring(VaultOptions.LegacyKeyId, (VaultOptions.LegacyKeyId, key));

    // Store tests inject a no-op audit writer (the audit chain is exercised in VaultAuditAppenderTests and the
    // live-SQL integration suite); the wiring test below uses RecordingAuditWriter to prove reveal audits.
    private static LocalEnvelopeVaultStore NewStore(MerchantRuntimeDbContext db, IClock clock, VaultKeyring keyring) =>
        new(db, clock, keyring, new NoopAuditWriter());

    private sealed class NoopAuditWriter : IVaultRevealAuditWriter
    {
        public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingAuditWriter : IVaultRevealAuditWriter
    {
        public List<(Guid MerchantId, string Name)> Calls { get; } = [];
        public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken)
        {
            Calls.Add((merchantId, secretName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RevealAsync_records_a_reveal_audit_for_the_merchant_and_secret()
    {
        var keyring = SingleKeyring(RandomKey());
        var writer = new RecordingAuditWriter();

        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), keyring).StoreAsync(Merchant, "psp-secret", "sk_live_audit00", CancellationToken.None);

        await using var db2 = NewContext();
        var store = new LocalEnvelopeVaultStore(db2, new FixedClock(Now), keyring, writer);
        await store.RevealAsync(Merchant, "psp-secret", CancellationToken.None);

        Assert.Equal((Merchant, "psp-secret"), Assert.Single(writer.Calls));
    }

    [Fact]
    public async Task Store_then_Reveal_round_trips_the_plaintext()
    {
        var keyring = SingleKeyring(RandomKey());
        const string secret = "sk_live_abcd1234";

        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), keyring)
                .StoreAsync(Merchant, "psp-secret", secret, CancellationToken.None);

        await using var db2 = NewContext();
        var revealed = await NewStore(db2, new FixedClock(Now), keyring)
            .RevealAsync(Merchant, "psp-secret", CancellationToken.None);

        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Insert_stores_a_new_secret_without_reading_first()
    {
        // The provisioning path: InsertAsync skips the read-before-write (so an INSERT-only principal works)
        // and tracks the row WITHOUT saving — the caller's unit of work commits it. Prove it round-trips.
        var keyring = SingleKeyring(RandomKey());
        const string secret = "sk_live_insert99";

        await using (var db1 = NewContext())
        {
            await NewStore(db1, new FixedClock(Now), keyring)
                .InsertAsync(Merchant, "psp-secret", secret, CancellationToken.None);
            await db1.SaveChangesAsync(); // InsertAsync does not save; stand in for the caller's UoW commit
        }

        await using var db2 = NewContext();
        var revealed = await NewStore(db2, new FixedClock(Now), keyring)
            .RevealAsync(Merchant, "psp-secret", CancellationToken.None);

        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Masked_returns_stars_plus_last4_and_hides_the_secret()
    {
        await using var db = NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        await store.StoreAsync(Merchant, "psp-secret", "sk_live_abcd1234", CancellationToken.None);
        var masked = await store.MaskedAsync(Merchant, "psp-secret", CancellationToken.None);

        Assert.Equal("****1234", masked);
        Assert.DoesNotContain("abcd", masked);
    }

    [Fact]
    public async Task Masked_returns_null_for_unknown_secret()
    {
        await using var db = NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        Assert.Null(await store.MaskedAsync(Merchant, "does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task Store_overwrites_existing_secret_with_rotated_value()
    {
        await using var db = NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        await store.StoreAsync(Merchant, "psp-secret", "first_value_0000", CancellationToken.None);
        await store.StoreAsync(Merchant, "psp-secret", "second_value_9999", CancellationToken.None);

        Assert.Equal("second_value_9999", await store.RevealAsync(Merchant, "psp-secret", CancellationToken.None));
        Assert.Equal("****9999", await store.MaskedAsync(Merchant, "psp-secret", CancellationToken.None));
    }

    [Fact]
    public async Task Exists_reflects_whether_a_secret_was_stored()
    {
        await using var db = NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        Assert.False(await store.ExistsAsync(Merchant, "psp-secret", CancellationToken.None));
        await store.StoreAsync(Merchant, "psp-secret", "value_5678", CancellationToken.None);
        Assert.True(await store.ExistsAsync(Merchant, "psp-secret", CancellationToken.None));
    }

    [Fact]
    public async Task A_blob_written_under_an_old_key_id_still_reveals_after_the_active_key_rolls()
    {
        var oldKey = RandomKey();
        var newKey = RandomKey();
        const string secret = "sk_live_rotate99";

        // Written under the legacy/active id "local-envelope-v1".
        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(oldKey))
                .StoreAsync(Merchant, "psp-secret", secret, CancellationToken.None);

        // Active key now rolls to "v2"; the keyring still carries the old id so the old blob decrypts.
        var rolled = Keyring("v2", (VaultOptions.LegacyKeyId, oldKey), ("v2", newKey));

        await using var db2 = NewContext();
        var store = NewStore(db2, new FixedClock(Now), rolled);

        Assert.Equal(secret, await store.RevealAsync(Merchant, "psp-secret", CancellationToken.None));

        // A NEW secret is written under the active id "v2".
        await store.StoreAsync(Merchant, "other", "sk_live_new00000", CancellationToken.None);
        await using var db3 = NewContext();
        var fresh = await db3.VaultSecrets.SingleAsync(b => b.Name == "other");
        Assert.Equal("v2", fresh.KeyId);
    }

    [Fact]
    public async Task Reveal_fails_closed_when_the_blob_key_id_is_not_in_the_keyring()
    {
        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(RandomKey()))
                .StoreAsync(Merchant, "psp-secret", "sk_live_orphaned", CancellationToken.None);

        // A keyring that does NOT contain "local-envelope-v1" (the id the blob carries) — must NOT fall back.
        var wrong = Keyring("v2", ("v2", RandomKey()));

        await using var db2 = NewContext();
        var store = NewStore(db2, new FixedClock(Now), wrong);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RevealAsync(Merchant, "psp-secret", CancellationToken.None));
        Assert.Contains(VaultOptions.LegacyKeyId, ex.Message); // the id is named (non-secret); no plaintext leaks
    }

    [Fact]
    public async Task Rewrap_moves_the_blob_to_the_active_key_and_still_reveals_after_the_old_key_is_retired()
    {
        var oldKey = RandomKey();
        var newKey = RandomKey();
        const string secret = "sk_live_rewrap00";

        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(oldKey))
                .StoreAsync(Merchant, "psp-secret", secret, CancellationToken.None);

        var rolled = Keyring("v2", (VaultOptions.LegacyKeyId, oldKey), ("v2", newKey));

        await using (var db2 = NewContext())
        {
            var rewrapped = await new VaultMaintenance(db2, new FixedClock(Now), rolled)
                .RewrapMerchantToActiveKeyAsync(Merchant, CancellationToken.None);
            Assert.Equal(1, rewrapped);
        }

        // The blob now carries the active id, and the OLD key can be retired entirely — reveal still works.
        await using var db3 = NewContext();
        var blob = await db3.VaultSecrets.SingleAsync(b => b.Name == "psp-secret");
        Assert.Equal("v2", blob.KeyId);

        await using var db4 = NewContext();
        var newKeyOnly = Keyring("v2", ("v2", newKey)); // old key gone from the keyring
        var revealed = await NewStore(db4, new FixedClock(Now), newKeyOnly)
            .RevealAsync(Merchant, "psp-secret", CancellationToken.None);
        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Rewrap_is_idempotent_when_all_blobs_are_already_on_the_active_key()
    {
        var key = RandomKey();

        await using (var db1 = NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(key))
                .StoreAsync(Merchant, "psp-secret", "sk_live_already0", CancellationToken.None);

        await using var db2 = NewContext();
        var rewrapped = await new VaultMaintenance(db2, new FixedClock(Now), SingleKeyring(key))
            .RewrapMerchantToActiveKeyAsync(Merchant, CancellationToken.None);

        Assert.Equal(0, rewrapped); // nothing to do — all already active
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
    }

    public void Dispose() => _connection.Dispose();
}
