extern alias ApiHost;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Admins.Application.Users;
using Admins.Domain.Users;
using Merchants.Application.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using AdminAuthAuditWriter = Admins.Application.Users.IAuthAuditWriter;
using AdminResolution = Admins.Application.Users.Resolution;
using AdminSessionStore = Admins.Application.Users.ISessionStore;
using AdminSessionCookies = ApiHost::Api.Admins.SessionCookies;

namespace Hosts.Tests;

// E2E callback coverage THROUGH the real OIDC middleware (REQ-6.1/6.2/6.3): challenge -> capture state/nonce +
// correlation cookies -> fake backchannel redeems the code for an id_token SIGNED with a test RSA key -> the
// callback validates it (signature/aud/lifetime/nonce/ISSUER == the static metadata issuer — the framework
// default that replaced the custom validator) and lands in the recording resolver or the deny redirect.
// No network, no DB: resolvers default to NotFound; the Scoped-admin case records session/audit writes in memory.

file static class TestOidc
{
    public static readonly RSA Rsa = RSA.Create(2048);
    public static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "e2e-test-key" };

    public const string WorkforceTenant = "05ab044e-e2c5-47dc-bbfb-fd7ea077fa71";
    public const string WorkforceOid = "abcdefab-cdef-4abc-8def-abcdefabcdef";
    public const string CiamTenant = "2a6d4554-88f1-4089-a995-0bf31c622493";
    public const string WorkforceIssuer = $"https://login.microsoftonline.com/{WorkforceTenant}/v2.0";
    public const string CiamIssuer = $"https://vcpexternaldev.ciamlogin.com/{CiamTenant}/v2.0";

    public static string CreateIdToken(string issuer, string audience, string nonce,
        params (string Type, string Value)[] claims) =>
        CreateIdToken(
            issuer, audience, nonce, SigningKey,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), claims);

    public static string CreateIdToken(
        string issuer,
        string audience,
        string nonce,
        SecurityKey signingKey,
        DateTime notBefore,
        DateTime expires,
        params (string Type, string Value)[] claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)).Append(new Claim("nonce", nonce))),
            notBefore: notBefore,
            expires: expires,
            issuedAt: notBefore,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return handler.WriteToken(token);
    }
}

/// <summary>Answers the token endpoint with the id_token the current test staged.</summary>
file sealed class FakeBackchannel : HttpMessageHandler
{
    public string? IdToken { get; set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(
                Status == HttpStatusCode.OK
                    ? $$"""{"id_token":"{{IdToken}}","access_token":"e2e-access-token","token_type":"Bearer","expires_in":3600}"""
                    : """{"error":"invalid_grant"}""",
                Encoding.UTF8, "application/json"),
        });
}

file sealed record AdminResolved(string Provider, string Subject);

file sealed class RecordingAdminResolver : ApiHost::Api.Admins.ICallbackResolver
{
    public AdminResolved? Resolved;
    public Guid? TenantId;
    public Guid? ObjectId;
    public string? Email;
    public ResolveResult Result { get; set; } = ResolveResult.NotFound;

    public Task<ResolveResult> ResolveAtCallbackAsync(
        SharedKernel.ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken ct)
    {
        Resolved = new AdminResolved(identity.Provider, identity.Subject);
        return Task.FromResult(Result);
    }

    public Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
        Guid tenantId, Guid objectId, string? email, string? employeeId,
        string correlationId, CancellationToken ct)
    {
        Resolved = new AdminResolved("microsoft", objectId.ToString("D"));
        TenantId = tenantId;
        ObjectId = objectId;
        Email = email;
        return Task.FromResult(Result);
    }
}

file sealed class RecordingAdminSessionStore : AdminSessionStore
{
    public List<Session> Added { get; } = [];
    public int SaveCount { get; private set; }

    public void Add(Session session) => Added.Add(session);
    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        SaveCount++;
        return Task.FromResult(1);
    }

    public Task<Session?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => Task.FromResult<Session?>(null);
    public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<bool> TrySupersedeAsync(Guid id, Guid successorId, DateTime now, CancellationToken ct) => Task.FromResult(false);
    public Task SlideIdleAsync(Guid id, DateTime idleExpiresAt, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeAllForAdminAsync(Guid adminId, CancellationToken ct) => Task.CompletedTask;
    public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    public Task<IReadOnlyList<Session>> ListByAdminAsync(Guid adminId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Session>>([]);
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<Session?>(null);
}

file sealed class RecordingAdminAuthAudit : AdminAuthAuditWriter
{
    public List<AuthAudit> Appended { get; } = [];
    public void Append(AuthAudit entry) => Appended.Add(entry);
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
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
    public const string AdminMicrosoftClient = "admin-microsoft-client";
    public const string MerchantMicrosoftClient = "merchant-microsoft-client";

    public FakeBackchannel Backchannel { get; } = new();
    public FakeGraphHandler Graph { get; } = new();
    public RecordingAdminResolver AdminResolver { get; } = new();
    public RecordingUserResolver UserResolver { get; } = new();
    public TestWorkforceTenantBindingStore TenantBindingStore { get; } = new();
    public RecordingAdminSessionStore AdminSessions { get; } = new();
    public RecordingAdminAuthAudit AdminAuthAudits { get; } = new();

    private readonly Dictionary<string, string?> _extraSettings;

    public OidcE2EFactory(Dictionary<string, string?>? extraSettings = null) =>
        _extraSettings = extraSettings ?? [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Pin the web-app base URLs: deny redirects are WebAppBaseUrl + ErrorPath, and Reason() reads the absolute
        // Location's query. On CI there is no appsettings.Development.json to supply them (host-test-config-precedence).
        builder.UseSetting("AdminSession:WebAppBaseUrl", "https://localhost:3001");
        builder.UseSetting("MerchantSession:WebAppBaseUrl", "https://localhost:3002");
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority", $"https://login.microsoftonline.com/{TestOidc.WorkforceTenant}/v2.0");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", AdminMicrosoftClient);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("AdminAuth:GraphBaseUrl", GraphTestOidc.GraphOrigin);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority", TestOidc.CiamIssuer);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", MerchantMicrosoftClient);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback");
        foreach (var (key, value) in _extraSettings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/",
                ["AdminSession:ReturnUrlAllowlist:1"] = "/dashboard",
                ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
                ["MerchantSession:ReturnUrlAllowlist:1"] = "/dashboard",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IWorkforceTenantBindingStore>();
            services.AddSingleton<IWorkforceTenantBindingStore>(TenantBindingStore);
            services.RemoveAll<AdminSessionStore>();
            services.AddSingleton<AdminSessionStore>(AdminSessions);
            services.RemoveAll<AdminAuthAuditWriter>();
            services.AddSingleton<AdminAuthAuditWriter>(AdminAuthAudits);
            services.AddScoped<ApiHost::Api.Admins.ICallbackResolver>(_ => AdminResolver);
            services.AddScoped<ApiHost::Api.Merchants.IUserCallbackResolver>(_ => UserResolver);
            services.AddHttpClient(ApiHost::Api.Admins.MicrosoftGraphEmployeeIdReader.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Graph);

            // Static metadata per scheme: the ISSUER here is the literal the framework-default validation
            // compares the token's iss against (M5) — workforce for admin, CIAM for merchant.
            Configure(services, ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft", TestOidc.WorkforceIssuer);
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
    public async Task Merchant_microsoft_local_https_origin_uses_vcp_external_dev_web_callback()
    {
        const string clientId = "dd7d2f17-60dc-4bd9-99a4-e2a93077bc9a";
        using var factory = new OidcE2EFactory(new Dictionary<string, string?>
        {
            ["MerchantAuth:Providers:Microsoft:Authority"] = TestOidc.CiamIssuer,
            ["MerchantAuth:Providers:Microsoft:ClientId"] = clientId,
            ["MerchantAuth:Providers:Microsoft:CallbackPath"] = "/api/v1/merchants/auth/microsoft/callback",
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost:5001"),
        });

        var response = await client.GetAsync("/api/v1/merchants/auth/microsoft/login");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(clientId, query["client_id"].ToString());
        Assert.Equal("https://localhost:5001/api/v1/merchants/auth/microsoft/callback",
            query["redirect_uri"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["state"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(query["nonce"].ToString()));
        Assert.DoesNotContain("client_secret", query.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // Google is retired on BOTH planes: no scheme is registered for it, so neither the login nor the callback
    // route resolves, whatever MerchantAuth/AdminAuth still carry in configuration.
    [Theory]
    [InlineData("admins")]
    [InlineData("merchants")]
    public async Task Google_login_and_callback_are_not_registered(string plane)
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/{plane}/auth/google/login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/{plane}/auth/google/callback")).StatusCode);
    }

    [Fact]
    public async Task Admin_microsoft_callback_uses_validated_object_id_and_keeps_email_out_of_identity()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("oid", TestOidc.WorkforceOid.ToUpperInvariant()),
            ("tid", TestOidc.WorkforceTenant.ToUpperInvariant()), ("roles", "unrelated"),
            ("email", "  OPS@VIRIYAH.CO.TH  "));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal(new AdminResolved("microsoft", TestOidc.WorkforceOid),
            factory.AdminResolver.Resolved);
        Assert.Equal(Guid.Parse(TestOidc.WorkforceTenant), factory.AdminResolver.TenantId);
        Assert.Equal(Guid.Parse(TestOidc.WorkforceOid), factory.AdminResolver.ObjectId);
        Assert.Equal("OPS@VIRIYAH.CO.TH", factory.AdminResolver.Email);
        Assert.Equal("not-provisioned", Reason(response));
    }

    [Fact]
    public async Task Admin_microsoft_callback_without_email_uses_the_tuple_and_never_falls_back_to_username()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce,
            ("sub", "pairwise"), ("oid", TestOidc.WorkforceOid), ("tid", TestOidc.WorkforceTenant),
            ("preferred_username", "mutable@outside.example"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal(new AdminResolved(User.MicrosoftProvider, TestOidc.WorkforceOid),
            factory.AdminResolver.Resolved);
        Assert.Equal(Guid.Parse(TestOidc.WorkforceTenant), factory.AdminResolver.TenantId);
        Assert.Equal(Guid.Parse(TestOidc.WorkforceOid), factory.AdminResolver.ObjectId);
        Assert.Null(factory.AdminResolver.Email);
        Assert.Equal("not-provisioned", Reason(response));
    }

    [Fact]
    // Exact workforce tuple resolves existing Scoped admin through real callback pipeline.
    public async Task Admin_microsoft_callback_resolves_existing_scoped_admin_and_creates_a_session()
    {
        var adminId = Guid.Parse("f5ebca84-4997-4a5d-b26b-6818f94f08f8");
        var merchantId = Guid.Parse("12b19f6a-2020-4ad8-ae1d-9567ec0b0cf4");
        const string pairwiseSubject = "pairwise-subject-must-not-be-audited";
        using var factory = new OidcE2EFactory();
        factory.AdminResolver.Result = ResolveResult.Of(new AdminResolution(
            adminId, "employee@example.com", Tier.Scoped,
            AccessibleMerchants.Of(new HashSet<Guid> { merchantId })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client,
            "/api/v1/admins/auth/microsoft/login?returnTo=%2Fdashboard",
            "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce,
            ("sub", pairwiseSubject), ("tid", TestOidc.WorkforceTenant.ToUpperInvariant()),
            ("oid", TestOidc.WorkforceOid), ("email", "employee@viriyah.co.th"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://localhost:3001/dashboard", response.Headers.Location?.ToString());
        Assert.Equal(new AdminResolved(
            User.MicrosoftProvider, TestOidc.WorkforceOid),
            factory.AdminResolver.Resolved);
        var session = Assert.Single(factory.AdminSessions.Added);
        Assert.Equal(adminId, session.AdminUserId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(1, factory.AdminSessions.SaveCount);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith($"{AdminSessionCookies.SessionCookieNameDevHttp}=", StringComparison.Ordinal));

        var audit = Assert.Single(factory.AdminAuthAudits.Appended);
        Assert.Equal(AuthEventType.LoginSuccess, audit.EventType);
        Assert.Equal(adminId, audit.AdminUserId);
        Assert.Null(audit.Subject);
        var safeAudit = string.Join('\n', audit.EventType, audit.Subject, audit.Reason, audit.CorrelationId);
        Assert.DoesNotContain(TestOidc.WorkforceTenant, safeAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestOidc.WorkforceOid, safeAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pairwiseSubject, safeAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("employee@viriyah.co.th", safeAudit, StringComparison.OrdinalIgnoreCase);
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

    // ---- fixed workforce tenant-aware identity gate through middleware ----

    [Fact]
    public async Task Admin_microsoft_requires_tid_even_when_the_optional_allowlist_is_empty()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("oid", TestOidc.WorkforceOid),
            ("email", "ops@viriyah.co.th"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("workforce-access-denied", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }

    [Fact]
    public async Task Admin_microsoft_rejects_tid_outside_the_pinned_tenant()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce,
            ("tid", "bbbbbbbb-0000-0000-0000-000000000000"),
            ("oid", TestOidc.WorkforceOid), ("email", "ops@viriyah.co.th"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("workforce-access-denied", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }

    [Theory]
    [InlineData("tid", "not-a-uuid")]
    [InlineData("tid", "00000000-0000-0000-0000-000000000000")]
    [InlineData("oid", null)]
    [InlineData("oid", "not-a-uuid")]
    [InlineData("oid", "00000000-0000-0000-0000-000000000000")]
    public async Task Admin_microsoft_rejects_invalid_or_empty_tuple_claim(
        string claimType, string? invalidValue)
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        var claims = new Dictionary<string, string>
        {
            ["tid"] = TestOidc.WorkforceTenant,
            ["oid"] = TestOidc.WorkforceOid,
            ["email"] = "ops@viriyah.co.th",
        };
        if (invalidValue is null)
            claims.Remove(claimType);
        else
            claims[claimType] = invalidValue;
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce,
            [.. claims.Select(pair => (pair.Key, pair.Value))]);

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("workforce-access-denied", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
    }

    // ---- framework token/protocol validation ----

    [Theory]
    [InlineData("audience")]
    [InlineData("nonce")]
    [InlineData("lifetime")]
    [InlineData("signature")]
    public async Task Admin_microsoft_rejects_unvalidated_tokens_before_resolution_or_session(string invalidPart)
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        var claims = new[]
        {
            ("tid", TestOidc.WorkforceTenant),
            ("oid", TestOidc.WorkforceOid),
            ("email", "private@example.com"),
        };

        factory.Backchannel.IdToken = invalidPart switch
        {
            "audience" => TestOidc.CreateIdToken(
                TestOidc.WorkforceIssuer, "wrong-audience", challenge.Nonce, claims),
            "nonce" => TestOidc.CreateIdToken(
                TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, "wrong-nonce", claims),
            "lifetime" => TestOidc.CreateIdToken(
                TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce,
                TestOidc.SigningKey, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5), claims),
            "signature" => CreateTokenWithUntrustedKey(challenge.Nonce, claims),
            _ => throw new InvalidOperationException("Unknown validation case."),
        };

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("auth-failed", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
        Assert.Empty(factory.AdminSessions.Added);
        var denied = Assert.Single(factory.AdminAuthAudits.Appended);
        Assert.Equal(AuthEventType.AuthDenied, denied.EventType);
        Assert.Null(denied.Subject);
    }

    [Fact]
    public async Task Admin_microsoft_invalid_issuer_uses_the_workforce_denial_reason()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            "https://login.microsoftonline.com/bbbbbbbb-0000-4000-8000-000000000000/v2.0",
            OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce,
            ("tid", TestOidc.WorkforceTenant), ("oid", TestOidc.WorkforceOid));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("workforce-access-denied", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
        Assert.Empty(factory.AdminSessions.Added);
    }

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
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");

        var response = await CallbackAsync(client, challenge, error: "access_denied");

        Assert.Equal("access-denied", Reason(response));
    }

    [Fact]
    public async Task A_state_mismatch_maps_to_auth_failed()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient, challenge.Nonce,
            ("tid", TestOidc.WorkforceTenant), ("oid", TestOidc.WorkforceOid));

        var response = await CallbackAsync(client, challenge, overrideState: "forged-state-value");

        Assert.Equal("auth-failed", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
        Assert.Empty(factory.AdminSessions.Added);
    }

    [Fact]
    public async Task A_code_exchange_failure_maps_to_auth_failed_before_resolution()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.Status = HttpStatusCode.BadRequest;

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("auth-failed", Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);
        Assert.Empty(factory.AdminSessions.Added);
    }

    [Fact]
    public async Task A_workforce_token_with_non_corporate_email_still_resolves_by_object_id()
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce, ("sub", "pairwise"), ("tid", TestOidc.WorkforceTenant),
            ("oid", TestOidc.WorkforceOid), ("email", "x@example.com"));

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("not-provisioned", Reason(response));
        Assert.Equal(new AdminResolved("microsoft", TestOidc.WorkforceOid), factory.AdminResolver.Resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unrelated")]
    public async Task A_workforce_token_ignores_role_claim(string? role)
    {
        using var factory = new OidcE2EFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = await StartAsync(client, "/api/v1/admins/auth/microsoft/login", "/api/v1/admins/auth/microsoft/callback");
        var claims = new List<(string Type, string Value)>
        {
            ("sub", "pairwise"), ("tid", TestOidc.WorkforceTenant),
            ("oid", TestOidc.WorkforceOid), ("email", "ops@viriyah.co.th"),
        };
        if (role is not null)
            claims.Add(("roles", role));
        factory.Backchannel.IdToken = TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer, OidcE2EFactory.AdminMicrosoftClient,
            challenge.Nonce, [.. claims]);

        var response = await CallbackAsync(client, challenge);

        Assert.Equal("not-provisioned", Reason(response));
        Assert.Equal(new AdminResolved("microsoft", TestOidc.WorkforceOid), factory.AdminResolver.Resolved);
    }

    private static string CreateTokenWithUntrustedKey(
        string nonce, params (string Type, string Value)[] claims)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "untrusted-e2e-key" };
        return TestOidc.CreateIdToken(
            TestOidc.WorkforceIssuer,
            OidcE2EFactory.AdminMicrosoftClient,
            nonce,
            key,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            claims);
    }
}
