extern alias ApiHost;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Hosts.Tests;

/// <summary>
/// Admin-provisioning guards (Codex re-review): a secret-looking field captured as readable config must be
/// rejected (P1 — never persist/echo plaintext outside the vault), and a blank-password admin connection
/// must fail fast (P2 — the runtime secret was not injected). Plus the boot guards a deploy cannot be
/// allowed past: the OIDC confidential clients, and the public origin every per-connection PSP webhook URL
/// is derived from. All DB-free, and none of them boots a host.
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

    // --- Psp:PublicBaseUrl boot guard (captive-payment-alignment REQ-4.3/4.6) ---

    private static IConfiguration Psp(string? publicBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Psp:PublicBaseUrl"] = publicBaseUrl })
            .Build();

    [Theory]
    [InlineData(null)]                      // key absent entirely
    [InlineData("")]                        // the committed appsettings.json placeholder
    [InlineData("   ")]
    [InlineData("api.example.com")]         // no scheme — not an absolute URI
    [InlineData("/api/v1")]                 // a bare path: absolute-as-file:// on Unix, unreachable for a PSP
    [InlineData("ftp://api.example.com")]   // absolute, but not a scheme a PSP can POST a callback to
    public void A_public_base_url_a_psp_could_not_call_back_on_fails_fast(string? publicBaseUrl)
    {
        // Every PSP callback URL is derived from this origin per connection, so a blank value ships charges
        // whose confirmation never arrives: the customer pays and the order stays AwaitingPayment.
        var config = Psp(publicBaseUrl);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequirePublicBaseUrl(config));
        Assert.Contains("Psp:PublicBaseUrl", failure.Message, StringComparison.Ordinal); // names the key (REQ-4.3)
    }

    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("https://api.example.com/")]   // trailing slash is the adapter's problem, not the guard's
    [InlineData("http://api.internal:8080")]  // a non-prod deploy behind a plain-http proxy still boots
    public void An_absolute_public_base_url_passes(string publicBaseUrl)
    {
        ApiHost::ProvisioningGuards.RequirePublicBaseUrl(Psp(publicBaseUrl));
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
        ApiHost::ProvisioningGuards.RequireOidcProviders(config, "MerchantAuth", requireAtLeastOne: false);
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
            ("MerchantAuth:Providers:Microsoft:ClientId", "an-entra-app-id"),
            ("MerchantAuth:Providers:Microsoft:ClientSecret", clientSecret),
            ("MerchantAuth:Providers:Microsoft:Authority", "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0"),
            ("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback"));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(config, "MerchantAuth", requireAtLeastOne: false));
    }

    // Codex review (PR #123) Medium #3: credentials alone must not satisfy the guard — the committed Microsoft
    // Authority ships a REPLACE_WITH_TENANT_ID placeholder, and booting with it (or plain http, or a missing/
    // duplicated callback) fails only at the first login's metadata fetch instead of at boot.
    [Theory]
    [InlineData("")]                                                             // blank
    [InlineData("https://login.microsoftonline.com/REPLACE_WITH_TENANT_ID/v2.0")] // committed placeholder
    [InlineData("http://login.microsoftonline.com/x/v2.0")]                       // not https
    public void A_configured_provider_with_a_bad_authority_fails_fast(string authority)
    {
        var config = Oidc(
            ("MerchantAuth:Providers:Microsoft:ClientId", "an-entra-app-id"),
            ("MerchantAuth:Providers:Microsoft:ClientSecret", "an-injected-secret"),
            ("MerchantAuth:Providers:Microsoft:Authority", authority),
            ("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback"));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(config, "MerchantAuth", requireAtLeastOne: false));
    }

    [Fact]
    public void A_missing_or_duplicated_callback_path_fails_fast()
    {
        var missing = Oidc(
            ("AdminAuth:Providers:Google:ClientId", "id"), ("AdminAuth:Providers:Google:ClientSecret", "secret"),
            ("AdminAuth:Providers:Google:Authority", "https://accounts.google.com"));
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(missing, "AdminAuth", requireAtLeastOne: true));

        var duplicated = Oidc(
            ("AdminAuth:Providers:Google:ClientId", "id"), ("AdminAuth:Providers:Google:ClientSecret", "secret"),
            ("AdminAuth:Providers:Google:Authority", "https://accounts.google.com"),
            ("AdminAuth:Providers:Google:CallbackPath", "/api/v1/admins/auth/callback"),
            ("AdminAuth:Providers:Microsoft:ClientId", "id2"), ("AdminAuth:Providers:Microsoft:ClientSecret", "secret2"),
            ("AdminAuth:Providers:Microsoft:Authority", "https://login.microsoftonline.com/3f2504e0-4f89-11d3-9a0c-0305e82c3301/v2.0"),
            ("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/callback")); // same path
        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireOidcProviders(duplicated, "AdminAuth", requireAtLeastOne: true));
    }

    // REQ-3.1/6.6: issuer validation is the framework default against the Authority's metadata issuer, so a
    // multi-tenant Authority would admit ANY Entra tenant — rejected for BOTH planes, and AllowedTenants (an
    // optional EXTRA gate) does not excuse it.
    [Theory]
    [InlineData("AdminAuth", "/common")]
    [InlineData("AdminAuth", "/organizations")]
    [InlineData("AdminAuth", "/consumers")]
    [InlineData("MerchantAuth", "/common")]
    [InlineData("MerchantAuth", "/organizations")]
    [InlineData("MerchantAuth", "/consumers")]
    public void A_microsoft_multi_tenant_authority_is_rejected_for_both_planes(string section, string tenantSegment)
    {
        (string, string?)[] Base(string authority, params (string, string?)[] extra) =>
        [
            ($"{section}:Providers:Microsoft:ClientId", "an-entra-app-id"),
            ($"{section}:Providers:Microsoft:ClientSecret", "an-injected-secret"),
            ($"{section}:Providers:Microsoft:Authority", authority),
            ($"{section}:Providers:Microsoft:CallbackPath", "/api/v1/x/auth/microsoft/callback"),
            .. extra,
        ];
        var authority = $"https://login.microsoftonline.com{tenantSegment}/v2.0";

        Assert.Throws<InvalidOperationException>(() => ApiHost::ProvisioningGuards.RequireOidcProviders(
            Oidc(Base(authority)), section, requireAtLeastOne: false));

        // AllowedTenants does NOT rescue a multi-tenant Authority (REQ-6.6).
        Assert.Throws<InvalidOperationException>(() => ApiHost::ProvisioningGuards.RequireOidcProviders(
            Oidc(Base(authority, ($"{section}:Providers:Microsoft:AllowedTenants:0", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"))),
            section, requireAtLeastOne: false));

        // Tenant-pinned passes: workforce for admin, CIAM for merchant.
        ApiHost::ProvisioningGuards.RequireOidcProviders(
            Oidc(Base("https://login.microsoftonline.com/3f2504e0-4f89-11d3-9a0c-0305e82c3301/v2.0")),
            section, requireAtLeastOne: false);
        ApiHost::ProvisioningGuards.RequireOidcProviders(
            Oidc(Base("https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0")),
            section, requireAtLeastOne: false);
    }

    [Fact]
    public void Injected_confidential_clients_pass_and_blank_providers_are_skipped()
    {
        var config = Oidc(
            ("AdminAuth:Providers:Google:ClientId", "333-admin.apps.googleusercontent.com"),
            ("AdminAuth:Providers:Google:ClientSecret", "GOCSPX-an-injected-secret"),
            ("AdminAuth:Providers:Google:Authority", "https://accounts.google.com"),
            ("AdminAuth:Providers:Google:CallbackPath", "/api/v1/admins/auth/google/callback"),
            ("AdminAuth:Providers:Microsoft:ClientId", "")); // blank = disabled, not an error (placeholder Authority ignored too)
        ApiHost::ProvisioningGuards.RequireOidcProviders(config, "AdminAuth", requireAtLeastOne: true); // does not throw
    }

    private static IConfiguration Workforce(params (string Key, string? Value)[] overrides)
    {
        var pairs = new List<(string Key, string? Value)>
        {
            ("AdminAuth:Providers:Microsoft:Authority",
                "https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0"),
            ("AdminAuth:Providers:Microsoft:ClientId", "workforce-client"),
            ("AdminAuth:Providers:Microsoft:ClientSecret", "injected-secret"),
            ("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback"),
        };
        pairs.AddRange(overrides);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            values[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Production_workforce_provider_guard_accepts_complete_microsoft_configuration()
    {
        ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(Workforce());
    }

    [Theory]
    [InlineData("AdminAuth:Providers:Microsoft:ClientId")]
    [InlineData("AdminAuth:Providers:Microsoft:ClientSecret")]
    [InlineData("AdminAuth:Providers:Microsoft:Authority")]
    [InlineData("AdminAuth:Providers:Microsoft:CallbackPath")]
    public void Production_workforce_provider_guard_rejects_missing_required_settings(string missingKey)
    {
        var config = Workforce((missingKey, ""));

        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(config));
    }

    // tier0-graph-employee-profile REQ-12.8: the employee-profile switch on with an unconfigured Microsoft provider
    // fails Production boot with the existing workforce diagnostic (the switch cannot enable an absent provider).
    [Theory]
    [InlineData("AdminAuth:Providers:Microsoft:ClientId")]
    [InlineData("AdminAuth:Providers:Microsoft:ClientSecret")]
    public void Production_workforce_provider_guard_rejects_employee_profile_switch_without_a_provider(string missingKey)
    {
        var config = Workforce(
            ("AdminAuth:Providers:Microsoft:RequireEmployeeProfile", "true"),
            (missingKey, ""));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(config));
        Assert.Contains("AdminAuth:Providers:Microsoft", error.Message, StringComparison.Ordinal);

        // The same switch with a complete provider boots.
        ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(
            Workforce(("AdminAuth:Providers:Microsoft:RequireEmployeeProfile", "true")));
    }

    [Fact]
    public void Production_workforce_provider_guard_rejects_enabled_google()
    {
        var config = Workforce(("AdminAuth:Providers:Google:ClientId", "google-client"));

        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(config));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/REPLACE_WITH_TENANT_ID/v2.0")]
    [InlineData("https://accounts.google.com")]
    public void Production_workforce_provider_guard_rejects_unpinned_or_wrong_authority(string authority)
    {
        var config = Workforce(("AdminAuth:Providers:Microsoft:Authority", authority));

        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(config));
    }

    [Fact]
    public void Production_workforce_provider_guard_rejects_non_contract_callback_path()
    {
        var config = Workforce(("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/callback"));

        Assert.Throws<InvalidOperationException>(() =>
            ApiHost::ProvisioningGuards.RequireWorkforceAdminProvider(config));
    }
}
