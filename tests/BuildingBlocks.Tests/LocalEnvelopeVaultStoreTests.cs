using System.Security.Cryptography;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Tests;

/// <summary>
/// Observable contract of <see cref="LocalEnvelopeVaultStore"/>: a stored secret reveals back
/// verbatim to a server-side caller, while the masked view shows only **** + last4 and never the
/// secret body. Encryption internals are not asserted — only the seam behaviour callers rely on.
/// </summary>
public sealed class LocalEnvelopeVaultStoreTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IOptions<VaultOptions> MasterKey() =>
        Options.Create(new VaultOptions
        {
            // 32-byte (256-bit) master key, base64-encoded — the size the store requires.
            MasterKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });

    [Fact]
    public async Task Store_then_Reveal_round_trips_the_plaintext()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        var key = MasterKey();

        const string secret = "sk_live_abcd1234";

        await using (var db1 = harness.NewContext())
        {
            var store1 = new LocalEnvelopeVaultStore(db1, new FixedClock(Now), key);
            await store1.StoreAsync(Tenant, "psp-secret", secret, CancellationToken.None);
        }

        // The SAME master key must reconstruct the per-tenant KEK and reveal the original.
        await using var db2 = harness.NewContext();
        var store2 = new LocalEnvelopeVaultStore(db2, new FixedClock(Now), key);

        var revealed = await store2.RevealAsync(Tenant, "psp-secret", CancellationToken.None);

        Assert.Equal(secret, revealed);
    }

    [Fact]
    public async Task Masked_returns_stars_plus_last4_and_hides_the_secret()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = new LocalEnvelopeVaultStore(db, new FixedClock(Now), MasterKey());

        const string secret = "sk_live_abcd1234";
        await store.StoreAsync(Tenant, "psp-secret", secret, CancellationToken.None);

        var masked = await store.MaskedAsync(Tenant, "psp-secret", CancellationToken.None);

        Assert.Equal("****1234", masked);
        Assert.DoesNotContain("abcd", masked);
        Assert.NotEqual(secret, masked);
    }

    [Fact]
    public async Task Masked_returns_null_for_unknown_secret()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = new LocalEnvelopeVaultStore(db, new FixedClock(Now), MasterKey());

        var masked = await store.MaskedAsync(Tenant, "does-not-exist", CancellationToken.None);

        Assert.Null(masked);
    }

    [Fact]
    public async Task Store_overwrites_existing_secret_with_rotated_value()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = new LocalEnvelopeVaultStore(db, new FixedClock(Now), MasterKey());

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
        var store = new LocalEnvelopeVaultStore(db, new FixedClock(Now), MasterKey());

        Assert.False(await store.ExistsAsync(Tenant, "psp-secret", CancellationToken.None));

        await store.StoreAsync(Tenant, "psp-secret", "value_5678", CancellationToken.None);

        Assert.True(await store.ExistsAsync(Tenant, "psp-secret", CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_master_key_that_is_not_32_bytes()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        using var db = harness.NewContext();
        var tooShort = Options.Create(new VaultOptions
        {
            MasterKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        });

        Assert.Throws<InvalidOperationException>(
            () => new LocalEnvelopeVaultStore(db, new FixedClock(Now), tooShort));
    }
}
