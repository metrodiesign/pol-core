extern alias ApiHost;
using System.Text.Json;

namespace Hosts.Tests;

/// <summary>
/// Admin-provisioning guards (Codex re-review): a secret-looking field captured as readable config must be
/// rejected (P1 — never persist/echo plaintext outside the vault), and a blank-password admin connection
/// must fail fast (P2 — the runtime secret was not injected). Both are DB-free.
/// </summary>
public sealed class ProvisioningGuardsTests
{
    private static Dictionary<string, JsonElement> Config(params string[] keys) =>
        keys.ToDictionary(k => k, _ => JsonSerializer.SerializeToElement("v"));

    [Theory]
    [InlineData("secretKey")]
    [InlineData("webhookSecret")]
    [InlineData("publicKey")]
    [InlineData("SecretKey")] // case-insensitive — casing typos do not slip through
    public void Secret_field_in_config_is_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() =>
            ApiHost::ProvisioningGuards.RejectSecretsInConfig(Config(key)));
    }

    [Fact]
    public void Non_secret_config_is_allowed()
    {
        ApiHost::ProvisioningGuards.RejectSecretsInConfig(Config("environment", "currencyCode", "card"));
        ApiHost::ProvisioningGuards.RejectSecretsInConfig(null); // no config bag at all
    }

    [Fact]
    public void Blank_password_sql_auth_connection_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireInjectedCredential(
                "Server=x;Database=d;User Id=pol_admin;Password=;Encrypt=True", "Admin"));
    }

    [Theory]
    [InlineData("Server=x;Database=d;Trusted_Connection=True")]                       // integrated security
    [InlineData("Server=x;Database=d;User Id=pol_admin;Password=injected;Encrypt=True")] // secret injected
    public void Usable_connection_passes(string connectionString)
    {
        ApiHost::ProvisioningGuards.RequireInjectedCredential(connectionString, "Admin"); // does not throw
    }

    // --- Admin OIDC confidential-client boot guards (REQ-8.2) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_WITH_ADMIN_CONSOLE_GOOGLE_CLIENT_ID.apps.googleusercontent.com")] // committed placeholder
    public void Missing_or_placeholder_oidc_client_id_fails_fast(string? clientId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireConfidentialClientId(clientId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_WITH_ADMIN_OIDC_CLIENT_SECRET")] // committed placeholder — secret was not injected
    public void Missing_or_placeholder_oidc_client_secret_fails_fast(string? clientSecret)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireConfidentialClientSecret(clientSecret));
    }

    [Fact]
    public void Injected_confidential_client_passes()
    {
        ApiHost::ProvisioningGuards.RequireConfidentialClientId("333-admin.apps.googleusercontent.com");
        ApiHost::ProvisioningGuards.RequireConfidentialClientSecret("GOCSPX-an-injected-secret"); // does not throw
    }
}
