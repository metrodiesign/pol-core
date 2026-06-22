using System.Security.Cryptography;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Tests;

/// <summary>
/// Observable contract of <see cref="LocalEnvelopeVaultStore"/> + the key-rotation behaviour: a stored secret
/// reveals back verbatim; the masked view shows only **** + last4; a blob written under an OLD key id still
/// reveals after the active key rolls; an unknown key id fails CLOSED; and re-wrap moves a blob onto the
/// active key without ever exposing the plaintext. Encryption internals are not asserted — only the seam.
/// </summary>
public sealed class LocalEnvelopeVaultStoreTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);

    private static VaultKeyring Keyring(string activeId, params (string Id, byte[] Key)[] keys) =>
        new(activeId, keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal));

    private static VaultKeyring SingleKeyring(byte[] key) =>
        Keyring(VaultOptions.LegacyKeyId, (VaultOptions.LegacyKeyId, key));

    // Store tests inject a no-op audit writer (the audit chain is exercised in VaultRevealAuditTests and the
    // live-SQL integration suite); the wiring test below uses RecordingAuditWriter to prove reveal audits.
    private static LocalEnvelopeVaultStore NewStore(ProducerDbContext db, IClock clock, VaultKeyring keyring) =>
        new(db, clock, keyring, new NoopAuditWriter());

    private sealed class NoopAuditWriter : IVaultRevealAuditWriter
    {
        public Task AppendAsync(Guid tenantId, string secretName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingAuditWriter : IVaultRevealAuditWriter
    {
        public List<(Guid TenantId, string Name)> Calls { get; } = [];
        public Task AppendAsync(Guid tenantId, string secretName, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, secretName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RevealAsync_records_a_reveal_audit_for_the_tenant_and_secret()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var keyring = SingleKeyring(RandomKey());
        var writer = new RecordingAuditWriter();

        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), keyring).StoreAsync(Tenant, "psp-secret", "sk_live_audit00", CancellationToken.None);

        await using var db2 = harness.NewContext();
        var store = new LocalEnvelopeVaultStore(db2, new FixedClock(Now), keyring, writer);
        await store.RevealAsync(Tenant, "psp-secret", CancellationToken.None);

        Assert.Equal((Tenant, "psp-secret"), Assert.Single(writer.Calls));
    }

    [Fact]
    public async Task Store_then_Reveal_round_trips_the_plaintext()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var keyring = SingleKeyring(RandomKey());
        const string secret = "sk_live_abcd1234";

        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), keyring)
                .StoreAsync(Tenant, "psp-secret", secret, CancellationToken.None);

        await using var db2 = harness.NewContext();
        var revealed = await NewStore(db2, new FixedClock(Now), keyring)
            .RevealAsync(Tenant, "psp-secret", CancellationToken.None);

        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Masked_returns_stars_plus_last4_and_hides_the_secret()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        await store.StoreAsync(Tenant, "psp-secret", "sk_live_abcd1234", CancellationToken.None);
        var masked = await store.MaskedAsync(Tenant, "psp-secret", CancellationToken.None);

        Assert.Equal("****1234", masked);
        Assert.DoesNotContain("abcd", masked);
    }

    [Fact]
    public async Task Masked_returns_null_for_unknown_secret()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        Assert.Null(await store.MaskedAsync(Tenant, "does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task Store_overwrites_existing_secret_with_rotated_value()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        await store.StoreAsync(Tenant, "psp-secret", "first_value_0000", CancellationToken.None);
        await store.StoreAsync(Tenant, "psp-secret", "second_value_9999", CancellationToken.None);

        Assert.Equal("second_value_9999", await store.RevealAsync(Tenant, "psp-secret", CancellationToken.None));
        Assert.Equal("****9999", await store.MaskedAsync(Tenant, "psp-secret", CancellationToken.None));
    }

    [Fact]
    public async Task Exists_reflects_whether_a_secret_was_stored()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = NewStore(db, new FixedClock(Now), SingleKeyring(RandomKey()));

        Assert.False(await store.ExistsAsync(Tenant, "psp-secret", CancellationToken.None));
        await store.StoreAsync(Tenant, "psp-secret", "value_5678", CancellationToken.None);
        Assert.True(await store.ExistsAsync(Tenant, "psp-secret", CancellationToken.None));
    }

    [Fact]
    public async Task A_blob_written_under_an_old_key_id_still_reveals_after_the_active_key_rolls()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var oldKey = RandomKey();
        var newKey = RandomKey();
        const string secret = "sk_live_rotate99";

        // Written under the legacy/active id "local-envelope-v1".
        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(oldKey))
                .StoreAsync(Tenant, "psp-secret", secret, CancellationToken.None);

        // Active key now rolls to "v2"; the keyring still carries the old id so the old blob decrypts.
        var rolled = Keyring("v2", (VaultOptions.LegacyKeyId, oldKey), ("v2", newKey));

        await using var db2 = harness.NewContext();
        var store = NewStore(db2, new FixedClock(Now), rolled);

        Assert.Equal(secret, await store.RevealAsync(Tenant, "psp-secret", CancellationToken.None));

        // A NEW secret is written under the active id "v2".
        await store.StoreAsync(Tenant, "other", "sk_live_new00000", CancellationToken.None);
        await using var db3 = harness.NewContext();
        var fresh = await db3.VaultSecrets.SingleAsync(b => b.Name == "other");
        Assert.Equal("v2", fresh.KeyId);
    }

    [Fact]
    public async Task Reveal_fails_closed_when_the_blob_key_id_is_not_in_the_keyring()
    {
        using var harness = ProducerDbContextTestHarness.Create();

        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(RandomKey()))
                .StoreAsync(Tenant, "psp-secret", "sk_live_orphaned", CancellationToken.None);

        // A keyring that does NOT contain "local-envelope-v1" (the id the blob carries) — must NOT fall back.
        var wrong = Keyring("v2", ("v2", RandomKey()));

        await using var db2 = harness.NewContext();
        var store = NewStore(db2, new FixedClock(Now), wrong);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RevealAsync(Tenant, "psp-secret", CancellationToken.None));
        Assert.Contains(VaultOptions.LegacyKeyId, ex.Message); // the id is named (non-secret); no plaintext leaks
    }

    [Fact]
    public async Task Rewrap_moves_the_blob_to_the_active_key_and_still_reveals_after_the_old_key_is_retired()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var oldKey = RandomKey();
        var newKey = RandomKey();
        const string secret = "sk_live_rewrap00";

        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(oldKey))
                .StoreAsync(Tenant, "psp-secret", secret, CancellationToken.None);

        var rolled = Keyring("v2", (VaultOptions.LegacyKeyId, oldKey), ("v2", newKey));

        await using (var db2 = harness.NewContext())
        {
            var rewrapped = await new VaultMaintenance(db2, new FixedClock(Now), rolled)
                .RewrapTenantToActiveKeyAsync(Tenant, CancellationToken.None);
            Assert.Equal(1, rewrapped);
        }

        // The blob now carries the active id, and the OLD key can be retired entirely — reveal still works.
        await using var db3 = harness.NewContext();
        var blob = await db3.VaultSecrets.SingleAsync(b => b.Name == "psp-secret");
        Assert.Equal("v2", blob.KeyId);

        await using var db4 = harness.NewContext();
        var newKeyOnly = Keyring("v2", ("v2", newKey)); // old key gone from the keyring
        var revealed = await NewStore(db4, new FixedClock(Now), newKeyOnly)
            .RevealAsync(Tenant, "psp-secret", CancellationToken.None);
        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Rewrap_is_idempotent_when_all_blobs_are_already_on_the_active_key()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var key = RandomKey();

        await using (var db1 = harness.NewContext())
            await NewStore(db1, new FixedClock(Now), SingleKeyring(key))
                .StoreAsync(Tenant, "psp-secret", "sk_live_already0", CancellationToken.None);

        await using var db2 = harness.NewContext();
        var rewrapped = await new VaultMaintenance(db2, new FixedClock(Now), SingleKeyring(key))
            .RewrapTenantToActiveKeyAsync(Tenant, CancellationToken.None);

        Assert.Equal(0, rewrapped); // nothing to do — all already active
    }
}
