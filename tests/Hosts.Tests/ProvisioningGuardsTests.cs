extern alias ApiHost;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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

    // --- OIDC confidential-client boot guards (REQ-8.2/14.1/14.2), provider-scoped ---

    private static IConfiguration Oidc(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    [Fact]
    public void A_side_with_no_configured_provider_fails_fast_when_one_is_required()
    {
        var config = Oidc(("AdminAuth:Providers:Google:ClientId", ""), ("AdminAuth:Providers:Microsoft:ClientId", ""));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(config, "AdminAuth", requireAtLeastOne: true));

        // ...but is allowed when the side may be intentionally disabled (merchant-user, REQ-14.2).
        ApiHost::ProvisioningGuards.RequireOidcProviders(config, "MerchantUserAuth", requireAtLeastOne: false);
    }

    [Fact]
    public void A_placeholder_client_id_fails_fast()
    {
        var config = Oidc(
            ("AdminAuth:Providers:Google:ClientId", "REPLACE_WITH_ADMIN_CONSOLE_GOOGLE_CLIENT_ID.apps.googleusercontent.com"),
            ("AdminAuth:Providers:Google:ClientSecret", "GOCSPX-an-injected-secret"));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(config, "AdminAuth", requireAtLeastOne: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_WITH_ADMIN_OIDC_CLIENT_SECRET")] // committed placeholder — secret was not injected
    public void A_configured_client_id_with_a_missing_or_placeholder_secret_fails_fast(string? clientSecret)
    {
        var config = Oidc(
            ("MerchantUserAuth:Providers:Microsoft:ClientId", "an-entra-app-id"),
            ("MerchantUserAuth:Providers:Microsoft:ClientSecret", clientSecret));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(config, "MerchantUserAuth", requireAtLeastOne: false));
    }

    [Fact]
    public void Injected_confidential_clients_pass_and_blank_providers_are_skipped()
    {
        var config = Oidc(
            ("AdminAuth:Providers:Google:ClientId", "333-admin.apps.googleusercontent.com"),
            ("AdminAuth:Providers:Google:ClientSecret", "GOCSPX-an-injected-secret"),
            ("AdminAuth:Providers:Microsoft:ClientId", "")); // blank = disabled, not an error
        ApiHost::ProvisioningGuards.RequireOidcProviders(config, "AdminAuth", requireAtLeastOne: true); // does not throw
    }
}
