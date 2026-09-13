using BuildingBlocks.Infrastructure.Vault;

namespace BuildingBlocks.Tests;

/// <summary>
/// Fail-fast custody validation: a misconfigured keyring must throw at build time (host boot) with a
/// secret-free, id-only message — never a runtime decrypt failure, never echoing key material. Also covers
/// the legacy MasterKeyBase64 shim and the FILE-over-inline precedence.
/// </summary>
public sealed class VaultKeyringFactoryTests
{
    private static string Key32() => Convert.ToBase64String(new byte[32]);

    [Fact]
    public void Build_throws_when_no_keys_and_no_master_key_are_configured()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(new VaultOptions()));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public void Build_shims_the_legacy_master_key_into_the_local_envelope_v1_id()
    {
        var keyring = VaultKeyringFactory.Build(new VaultOptions { MasterKeyBase64 = Key32() });

        Assert.Equal(VaultOptions.LegacyKeyId, keyring.ActiveKeyId);
        Assert.Equal(VaultOptions.LegacyKeyId, keyring.Active.Id);
        Assert.NotNull(keyring.ResolveOrNull(VaultOptions.LegacyKeyId));
    }

    [Fact]
    public void Build_throws_when_both_legacy_master_key_and_keys_are_set()
    {
        // The legacy->keyring migration trap: operator added a Keys entry but left the old key in
        // MasterKeyBase64. Must fail fast (not silently drop the legacy key -> fail-closed reveal outage).
        var options = new VaultOptions
        {
            MasterKeyBase64 = Key32(),
            ActiveKeyId = "v2",
            Keys = { ["v2"] = new VaultKeyEntry { KeyBase64 = Key32() } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.Contains(VaultOptions.LegacyKeyId, ex.Message);
    }

    [Fact]
    public void Build_throws_on_a_key_that_does_not_decode_to_32_bytes()
    {
        var options = new VaultOptions
        {
            ActiveKeyId = "k1",
            Keys = { ["k1"] = new VaultKeyEntry { KeyBase64 = Convert.ToBase64String(new byte[16]) } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.Contains("k1", ex.Message);
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void Build_throws_on_invalid_base64_without_echoing_the_value()
    {
        const string garbage = "not-valid-base64!!";
        var options = new VaultOptions
        {
            ActiveKeyId = "k1",
            Keys = { ["k1"] = new VaultKeyEntry { KeyBase64 = garbage } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.DoesNotContain(garbage, ex.Message);
    }

    [Fact]
    public void Build_throws_when_a_key_has_no_source()
    {
        var options = new VaultOptions
        {
            ActiveKeyId = "k1",
            Keys = { ["k1"] = new VaultKeyEntry() },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.Contains("k1", ex.Message);
    }

    [Fact]
    public void Build_throws_when_active_key_id_is_not_in_the_keyring()
    {
        var options = new VaultOptions
        {
            ActiveKeyId = "missing",
            Keys = { ["k1"] = new VaultKeyEntry { KeyBase64 = Key32() } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Build_throws_when_multiple_keys_have_no_active_id()
    {
        var options = new VaultOptions
        {
            Keys =
            {
                ["k1"] = new VaultKeyEntry { KeyBase64 = Key32() },
                ["k2"] = new VaultKeyEntry { KeyBase64 = Key32() },
            },
        };

        Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
    }

    [Fact]
    public void Build_reads_a_key_from_a_mounted_file_in_preference_to_inline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vault-key-{Guid.NewGuid():N}.key");
        File.WriteAllText(path, Key32() + "\n"); // trailing newline must be tolerated
        try
        {
            var options = new VaultOptions
            {
                ActiveKeyId = "k1",
                Keys = { ["k1"] = new VaultKeyEntry { KeyFile = path, KeyBase64 = "ignored-because-file-wins" } },
            };

            var keyring = VaultKeyringFactory.Build(options);
            Assert.Equal(32, keyring.Active.Key.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_throws_when_the_key_file_is_missing()
    {
        var options = new VaultOptions
        {
            ActiveKeyId = "k1",
            Keys = { ["k1"] = new VaultKeyEntry { KeyFile = "/no/such/vault-key.key" } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VaultKeyringFactory.Build(options));
        Assert.Contains("k1", ex.Message);
    }
}
