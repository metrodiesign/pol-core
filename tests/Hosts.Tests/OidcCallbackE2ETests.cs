extern alias ApiHost;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Admins.Application.Users;
using Merchants.Application.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Hosts.Tests;

// E2E callback coverage THROUGH the real OIDC middleware (REQ-6.1/6.2/6.3): challenge -> capture state/nonce +
// correlation cookies -> fake backchannel redeems the code for an id_token SIGNED with a test RSA key -> the
// callback validates it (signature/aud/lifetime/nonce/ISSUER == the static metadata issuer — the framework
// default that replaced the custom validator) and lands in the recording resolver or the deny redirect.
// No network, no DB: resolvers answer NotFound (admin -> reason=not-provisioned redirect, merchant -> /register
// ticket redirect), and the deny audit write fails silently on the DB-less host (DenyAsync catches it).

file static class TestOidc
{
    public static readonly RSA Rsa = RSA.Create(2048);
    public static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "e2e-test-key" };

    public const string WorkforceTenant = "05ab044e-e2c5-47dc-bbfb-fd7ea077fa71";
    public const string CiamTenant = "1aee3cad-1e4d-4de5-9e25-424d0d12520b";
    public const string WorkforceIssuer = $"https://login.microsoftonline.com/{WorkforceTenant}/v2.0";
    public const string CiamIssuer = $"https://{CiamTenant}.ciamlogin.com/{CiamTenant}/v2.0";
    public const string GoogleIssuer = "https://accounts.google.com";

    public static string CreateIdToken(string issuer, string audience, string nonce,
        params (string Type, string Value)[] claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)).Append(new Claim("nonce", nonce))),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            issuedAt: DateTime.UtcNow,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
        return handler.WriteToken(token);
    }
}

/// <summary>Answers the token endpoint with the id_token the current test staged.</summary>
file sealed class FakeBackchannel : HttpMessageHandler
{
    public string? IdToken { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"id_token":"{{IdToken}}","access_token":"e2e-access-token","token_type":"Bearer","expires_in":3600}""",
                Encoding.UTF8, "application/json"),
        });
}

file sealed record AdminResolved(string Provider, string Subject, string Email, bool EmailVerified);

file sealed class RecordingAdminResolver : ApiHost::Api.Admins.ICallbackResolver
{
    public AdminResolved? Resolved;
    public Task<ResolveResult> ResolveAtCallbackAsync(
        SharedKernel.ProviderIdentity identity, string email, bool emailVerified, string correlationId, CancellationToken ct)
    {
        Resolved = new AdminResolved(identity.Provider, identity.Subject, email, emailVerified);
        return Task.FromResult(ResolveResult.NotFound);
    }
}

file sealed class RecordingUserResolver : ApiHost::Api.Merchants.IUserCallbackResolver
{
    public (string Provider, string Subject)? Resolved;
    public Task<LoginResult> ResolveAtCallbackAsync(SharedKernel.ProviderIdentity identity, CancellationToken ct)
    {
        Resolved = (identity.Provider, identity.Subject);
        return Task.FromResult(LoginResult.NotFound);
    }
}

file sealed class OidcE2EFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string AdminGoogleClient = "admin-google-client";
    public const string AdminMicrosoftClient = "admin-microsoft-client";
    public const string MerchantGoogleClient = "merchant-google-client";
    public const string MerchantMicrosoftClient = "merchant-microsoft-client";

    public FakeBackchannel Backchannel { get; } = new();
    public RecordingAdminResolver AdminResolver { get; } = new();
    public RecordingUserResolver UserResolver { get; } = new();

    private readonly Dictionary<string, string?> _extraSettings;

    public OidcE2EFactory(Dictionary<string, string?>? extraSettings = null) =>
        _extraSettings = extraSettings ?? [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Pin the SPA base urls: deny redirects are SpaBaseUrl + ErrorPath, and Reason() reads the absolute
        // Location's query. On CI there is no appsettings.Development.json to supply them (host-test-config-precedence).
        builder.UseSetting("AdminSession:SpaBaseUrl", "http://localhost:5200");
        builder.UseSetting("MerchantUser:Session:SpaBaseUrl", "http://localhost:5300");
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("AdminAuth:Providers:Google:ClientId", AdminGoogleClient);
        builder.UseSetting("AdminAuth:Providers:Google:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Google:CallbackPath", "/api/v1/admins/auth/google/callback");
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority", $"https://login.microsoftonline.com/{TestOidc.WorkforceTenant}/v2.0");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", AdminMicrosoftClient);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("MerchantAuth:Providers:Google:ClientId", MerchantGoogleClient);
        builder.UseSetting("MerchantAuth:Providers:Google:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Google:CallbackPath", "/api/v1/merchants/auth/google/callback");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority", $"https://viriyahexternal.ciamlogin.com/{TestOidc.CiamTenant}/v2.0");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", MerchantMicrosoftClient);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback");
        foreach (var (key, value) in _extraSettings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/dashboard",
                ["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/dashboard",
            }));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddScoped<ApiHost::Api.Admins.ICallbackResolver>(_ => AdminResolver);
            services.AddScoped<ApiHost::Api.Merchants.IUserCallbackResolver>(_ => UserResolver);

            // Static metadata per scheme: the ISSUER here is the literal the framework-default validation
            // compares the token's iss against (M5) — workforce for admin, CIAM for merchant, Google for both.
            Configure(services, ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Google", TestOidc.GoogleIssuer);
            Configure(services, ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft", TestOidc.WorkforceIssuer);
            Configure(services, ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Google", TestOidc.GoogleIssuer);
            Configure(services, ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft", TestOidc.CiamIssuer);
        });
    }

    private void Configure(IServiceCollection services, string scheme, string issuer) =>
        services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = issuer,
                AuthorizationEndpoint = "https://idp.example.com/authorize",
                TokenEndpoint = "https://idp.example.com/token",
                JwksUri = "https://idp.example.com/keys",
            };
            configuration.SigningKeys.Add(TestOidc.SigningKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.Backchannel = new HttpClient(Backchannel);
        });
}

public sealed class OidcCallbackE2ETests
{
    private sealed record Challenge(string State, string Nonce, string Cookies, string CallbackPath);

    private static async Task<Challenge> StartAsync(HttpClient client, string loginPath, string callbackPath)
    {
        var response = await client.GetAsync(loginPath);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var cookies = string.Join("; ",
            response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return new Challenge(query["state"].ToString(), query["nonce"].ToString(), cookies, callbackPath);
    }

    private static async Task<HttpResponseMessage> CallbackAsync(
        HttpClient client, Challenge challenge, string? code = "e2e-code", string? overrideState = null,
        string? error = null)
    {
        var url = challenge.CallbackPath + "?" + (error is not null
            ? $"error={error}&state={Uri.EscapeDataString(overrideState ?? challenge.State)}"
            : $"code={code}&state={Uri.EscapeDataString(overrideState ?? challenge.State)}");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", challenge.Cookies);
        return await client.SendAsync(request);
    }

    private static string Reason(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["reason"].ToString();
    }

    // ---- callback happy-path per provider x plane (REQ-6.1) ----

    [Fact]
    public async Task Merchant_google_callback_maps_sub_and_redirects_an_unknown_user_to_register()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/merchants/auth/google/login", "/api/v1/merchants/auth/google/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.GoogleIssuer, OidcE2EFactory.MerchantGoogleClient,
            challenge.Nonce, ("sub", "google-sub-e2e"), ("email", "somchai@example.com"), ("email_verified", "true"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal(("google", "google-sub-e2e"), factory.UserResolver.Resolved);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/register?ticket=", response.Headers.Location!.ToString()); // NotFound -> registration ticket
    }

    [Fact]
    public async Task Merchant_microsoft_callback_through_the_ciam_issuer_maps_oid_never_sub()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/merchants/auth/microsoft/login", "/api/v1/merchants/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.CiamIssuer, OidcE2EFactory.MerchantMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise-sub"), ("oid", "entra-oid-e2e"), ("tid", TestOidc.CiamTenant),
            ("preferred_username", "agent@customer-org.example")); // email via @-shaped UPN fallback (REQ-1.6)

        var response = await CallbackAsync(client, challenge);

        // REQ-1.3/6.3: the CIAM issuer from (static) discovery metadata passed the FRAMEWORK-DEFAULT validation.
        Assert.Equal(("microsoft", "entra-oid-e2e"), factory.UserResolver.Resolved);
        Assert.Contains("/register?ticket=", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Admin_callbacks_map_google_sub_verified_and_microsoft_oid_unverified()
    {
        using (var factory = new OidcE2EFactory())
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var challenge = await StartAsync(client, "/api/v1/admins/auth/google/login", "/api/v1/admins/auth/google/callback");
            factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.GoogleIssuer, OidcE2EFactory.AdminGoogleClient,
                challenge.Nonce, ("sub", "admin-google-sub"), ("email", "ops@example.com"), ("email_verified", "true"));

            var response = await CallbackAsync(client, challenge);

            Assert.Equal(new AdminResolved("google", "admin-google-sub", "ops@example.com", EmailVerified: true),
                factory.AdminResolver.Resolved);
            Assert.Equal("not-provisioned", Reason(response)); // NotFound + empty allowlist -> deny (fail closed)
        }

        using (var factory = new OidcE2EFactory())
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
            factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
                challenge.Nonce, ("sub", "pairwise"), ("oid", "admin-entra-oid"), ("tid", TestOidc.WorkforceTenant),
                ("email", "ops@example.com"));

            var response = await CallbackAsync(client, challenge);

            // REQ-2.2/6.3: the workforce tenant-pinned issuer passed; subject = oid; Entra email stays UNVERIFIED.
            Assert.Equal(new AdminResolved("microsoft", "admin-entra-oid", "ops@example.com", EmailVerified: false),
                factory.AdminResolver.Resolved);
            Assert.Equal("not-provisioned", Reason(response));
        }
    }

    [Fact]
    public async Task Merchant_microsoft_without_email_or_at_shaped_upn_denies_missing_identity()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/merchants/auth/microsoft/login", "/api/v1/merchants/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.CiamIssuer, OidcE2EFactory.MerchantMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("oid", "entra-oid-no-mail"), ("tid", TestOidc.CiamTenant),
            ("preferred_username", "host/machine-account")); // not @-shaped -> no email (M8 pre-rollout gate)

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("missing-identity", Reason(response));
        Assert.Null(factory.UserResolver.Resolved); // hard-fail BEFORE any resolution
    }

    // ---- the AllowedTenants gate through the middleware (REQ-2.4, P1-1) ----

    [Fact]
    public async Task An_empty_allowlist_admits_a_token_without_tid_after_issuer_validation()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("oid", "oid-no-tid"), ("email", "ops@example.com"));

        await CallbackAsync(client, challenge);

        Assert.Equal("oid-no-tid", factory.AdminResolver.Resolved?.Subject); // gate inactive -> login proceeds
    }

    [Theory]
    [InlineData(null, "tenant-missing")]                                   // allowlist set + tid absent
    [InlineData("bbbbbbbb-0000-0000-0000-000000000000", "tenant-not-allowed")] // allowlist set + tid outside
    public async Task A_non_empty_allowlist_gates_tid_with_the_specific_reason(string? tid, string expectedReason)
    {
        using var factory = new OidcE2EFactory(new Dictionary<string, string?>
        {
            ["AdminAuth:Providers:Microsoft:AllowedTenants:0"] = TestOidc.WorkforceTenant,
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        var claims = new List<(string, string)> { ("sub", "pairwise"), ("oid", "gated-oid"), ("email", "ops@example.com") };
        if (tid is not null)
            claims.Add(("tid", tid));
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce, [.. claims]);

        var response = await CallbackAsync(client, challenge);

        Assert.Equal(expectedReason, Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }

    // ---- issuer validation (REQ-1.4/6.3) ----

    [Fact]
    public async Task A_token_from_a_different_tenant_than_the_pinned_authority_is_rejected()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/merchants/auth/microsoft/login", "/api/v1/merchants/auth/microsoft/callback");
        var foreignIssuer = "https://cccccccc-0000-0000-0000-000000000000.ciamlogin.com/cccccccc-0000-0000-0000-000000000000/v2.0";
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(foreignIssuer, OidcE2EFactory.MerchantMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("oid", "foreign-oid"), ("tid", "cccccccc-0000-0000-0000-000000000000"),
            ("email", "x@example.com"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("auth-failed", Reason(response)); // SecurityTokenInvalidIssuerException -> OnRemoteFailure
        Assert.Null(factory.UserResolver.Resolved);
    }

    // ---- provider error paths (REQ-6.2) ----

    [Fact]
    public async Task An_access_denied_error_from_the_provider_maps_to_access_denied()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/google/login", "/api/v1/admins/auth/google/callback");

        var response = await CallbackAsync(client, challenge, error: "access_denied");

        Assert.Equal("access-denied", Reason(response));
    }

    [Fact]
    public async Task A_state_mismatch_maps_to_auth_failed()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/merchants/auth/google/login", "/api/v1/merchants/auth/google/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.GoogleIssuer, OidcE2EFactory.MerchantGoogleClient,
            challenge.Nonce, ("sub", "any"), ("email", "x@example.com"), ("email_verified", "true"));

        var response = await CallbackAsync(client, challenge, overrideState: "forged-state-value");

        Assert.Equal("auth-failed", Reason(response));
        Assert.Null(factory.UserResolver.Resolved);
    }

    [Fact]
    public async Task A_google_token_without_a_verified_email_maps_to_email_unverified()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/google/login", "/api/v1/admins/auth/google/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.GoogleIssuer, OidcE2EFactory.AdminGoogleClient,
            challenge.Nonce, ("sub", "unverified-sub"), ("email", "x@example.com")); // email_verified absent

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("email-unverified", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }

    [Fact]
    public async Task A_google_token_outside_the_hosted_domain_maps_to_hd_mismatch()
    {
        using var factory = new OidcE2EFactory(new Dictionary<string, string?>
        {
            ["AdminAuth:Providers:Google:HostedDomain"] = "example.co.th",
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/google/login", "/api/v1/admins/auth/google/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.GoogleIssuer, OidcE2EFactory.AdminGoogleClient,
            challenge.Nonce, ("sub", "outsider"), ("email", "x@another.org"), ("email_verified", "true"), ("hd", "another.org"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("hd-mismatch", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }
}
