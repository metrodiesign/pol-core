extern alias ApiHost;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Admins.Application.Users;
using Admins.Domain.Users;
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

namespace Hosts.Tests;

// tier0-graph-employee-profile task 4: the Graph employeeId acquisition THROUGH the real OIDC middleware with a fake
// backchannel (code exchange) and a fake Graph handler (REQ-11.6). Switch on: the challenge carries User.Read, a 200
// hands the NORMALISED employeeId to the resolver, and every Graph failure class denies with the right browser
// reason BEFORE the resolver runs — no session, one denied audit with no PII. Switch off: no Graph request at all.

internal static class GraphTestOidc
{
    public static readonly RSA Rsa = RSA.Create(2048);
    public static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "graph-e2e-test-key" };
    public const string WorkforceTenant = "05ab044e-e2c5-47dc-bbfb-fd7ea077fa71";
    public const string WorkforceObject = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    public const string WorkforceIssuer = $"https://login.microsoftonline.com/{WorkforceTenant}/v2.0";
    public const string AccessToken = "graph-access-token-dummy-canary";
    public const string GraphOrigin = "https://graph.test.invalid";

    public static string CreateIdToken(string audience, string nonce, params (string Type, string Value)[] claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: WorkforceIssuer,
            audience: audience,
            subject: new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)).Append(new Claim("nonce", nonce))),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            issuedAt: DateTime.UtcNow,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
        return handler.WriteToken(token);
    }
}

internal sealed class GraphE2EBackchannel : HttpMessageHandler
{
    public string? IdToken { get; set; }
    public bool OmitAccessToken { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var access = OmitAccessToken ? "" : ",\"access_token\":\"" + GraphTestOidc.AccessToken + "\"";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"id_token":"{{IdToken}}"{{access}},"token_type":"Bearer","expires_in":3600}""",
                Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Stands in for Microsoft Graph: records every request and answers with the staged response.</summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = """{"employeeId":"e12"}""";
    public Exception? Throw { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed record GraphAdminResolved(
    string Provider, Guid TenantId, string Subject, string? Email, string? EmployeeId);

internal sealed class GraphRecordingAdminResolver : ApiHost::Api.Admins.ICallbackResolver
{
    public GraphAdminResolved? Resolved;
    public ResolveResult Result { get; set; } = ResolveResult.NotFound;

    public Task<ResolveResult> ResolveAtCallbackAsync(
        SharedKernel.ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken ct)
    {
        Resolved = new GraphAdminResolved(identity.Provider, Guid.Empty, identity.Subject, null, employeeId);
        return Task.FromResult(Result);
    }

    public Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
        Guid tenantId, Guid objectId, string? email, string? employeeId,
        string correlationId, CancellationToken ct)
    {
        Resolved = new GraphAdminResolved("microsoft", tenantId, objectId.ToString("D"), email, employeeId);
        return Task.FromResult(Result);
    }
}

internal sealed class GraphRecordingAdminSessionStore : AdminSessionStore
{
    public List<Session> Added { get; } = [];
    public void Add(Session session) => Added.Add(session);
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
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

internal sealed class GraphRecordingAdminAuthAudit : AdminAuthAuditWriter
{
    public List<AuthAudit> Appended { get; } = [];
    public void Append(AuthAudit entry) => Appended.Add(entry);
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
}

internal sealed class GraphE2EFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string AdminMicrosoftClient = "admin-microsoft-client";

    public GraphE2EBackchannel Backchannel { get; } = new();
    public FakeGraphHandler Graph { get; } = new();
    public GraphRecordingAdminResolver AdminResolver { get; } = new();
    public GraphRecordingAdminSessionStore AdminSessions { get; } = new();
    public GraphRecordingAdminAuthAudit AdminAuthAudits { get; } = new();
    private readonly bool _requireEmployeeProfile;

    public GraphE2EFactory(bool requireEmployeeProfile) => _requireEmployeeProfile = requireEmployeeProfile;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("AdminSession:WebAppBaseUrl", "https://localhost:3001");
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("AdminAuth:GraphBaseUrl", GraphTestOidc.GraphOrigin);
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority", GraphTestOidc.WorkforceIssuer);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", AdminMicrosoftClient);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("AdminAuth:Providers:Microsoft:RequireEmployeeProfile", _requireEmployeeProfile ? "true" : "false");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/",
                ["AdminSession:ReturnUrlAllowlist:1"] = "/dashboard",
                ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IWorkforceTenantBindingStore>();
            services.AddSingleton<IWorkforceTenantBindingStore>(new TestWorkforceTenantBindingStore());
            services.RemoveAll<AdminSessionStore>();
            services.AddSingleton<AdminSessionStore>(AdminSessions);
            services.RemoveAll<AdminAuthAuditWriter>();
            services.AddSingleton<AdminAuthAuditWriter>(AdminAuthAudits);
            services.AddScoped<ApiHost::Api.Admins.ICallbackResolver>(_ => AdminResolver);
            // REQ-11.6: the named Graph client gets the fake handler; nothing leaves the process.
            services.AddHttpClient(ApiHost::Api.Admins.MicrosoftGraphEmployeeIdReader.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Graph);

            services.PostConfigure<OpenIdConnectOptions>(
                ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft", options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = GraphTestOidc.WorkforceIssuer,
                        AuthorizationEndpoint = "https://idp.example.com/authorize",
                        TokenEndpoint = "https://idp.example.com/token",
                        JwksUri = "https://idp.example.com/keys",
                    };
                    configuration.SigningKeys.Add(GraphTestOidc.SigningKey);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(Backchannel);
                });
        });
    }
}

public sealed class AdminGraphEmployeeProfileE2ETests
{
    private const string Login = "/api/v1/admins/auth/microsoft/login";
    private const string Callback = "/api/v1/admins/auth/microsoft/callback";
    private const string Email = "employee@viriyah.co.th";

    private sealed record Challenge(string State, string Nonce, string Cookies, string Scope);

    private static async Task<Challenge> StartAsync(HttpClient client)
    {
        var response = await client.GetAsync(Login);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var cookies = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return new Challenge(query["state"].ToString(), query["nonce"].ToString(), cookies, query["scope"].ToString());
    }

    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, GraphE2EFactory factory, Challenge challenge)
    {
        factory.Backchannel.IdToken = GraphTestOidc.CreateIdToken(GraphE2EFactory.AdminMicrosoftClient, challenge.Nonce,
            ("sub", "pairwise"), ("tid", GraphTestOidc.WorkforceTenant),
            ("oid", GraphTestOidc.WorkforceObject), ("email", Email));
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{Callback}?code=e2e-code&state={Uri.EscapeDataString(challenge.State)}");
        request.Headers.Add("Cookie", challenge.Cookies);
        return await client.SendAsync(request);
    }

    private static string Reason(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["reason"].ToString();
    }

    private static (GraphE2EFactory Factory, HttpClient Client) Build(bool requireEmployeeProfile)
    {
        var factory = new GraphE2EFactory(requireEmployeeProfile);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return (factory, client);
    }

    // ---- switch on ----

    [Fact]
    public async Task Switch_on_requests_user_read_and_hands_the_normalised_employee_id_to_the_resolver()
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Graph.Body = """{"@odata.context":"x","employeeId":"  ab12  "}""";
            var challenge = await StartAsync(client);
            Assert.Equal("openid email profile User.Read", challenge.Scope); // REQ-1.1/1.2/12.2

            var response = await CallbackAsync(client, factory, challenge);

            Assert.Equal("not-provisioned", Reason(response)); // recording resolver answers NotFound
            Assert.Equal(new GraphAdminResolved(
                User.MicrosoftProvider, Guid.Parse(GraphTestOidc.WorkforceTenant),
                GraphTestOidc.WorkforceObject, Email, "AB12"), factory.AdminResolver.Resolved); // REQ-2.1/2.16
            var graph = Assert.Single(factory.Graph.Requests); // REQ-1.3/1.18: exactly one GET /v1.0/me?$select=employeeId
            Assert.Equal(HttpMethod.Get, graph.Method);
            Assert.Equal(GraphTestOidc.GraphOrigin + "/v1.0/me?$select=employeeId", graph.RequestUri!.ToString());
            Assert.Equal("Bearer", graph.Headers.Authorization!.Scheme);
            Assert.Equal(GraphTestOidc.AccessToken, graph.Headers.Authorization.Parameter);
            // REQ-1.5/1.6: the token reaches neither the browser nor the audit.
            Assert.DoesNotContain(GraphTestOidc.AccessToken, response.Headers.Location!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.Contains(GraphTestOidc.AccessToken, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Switch_on_success_establishes_a_session_when_the_resolver_resolves()
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            var adminId = Guid.NewGuid();
            factory.AdminResolver.Result = ResolveResult.Of(new AdminResolution(
                adminId, "employee@example.com", Tier.Scoped, AccessibleMerchants.Of(new HashSet<Guid>())));
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            Assert.Equal("https://localhost:3001/", response.Headers.Location?.ToString());
            Assert.Equal(adminId, Assert.Single(factory.AdminSessions.Added).AdminUserId);
            var audit = Assert.Single(factory.AdminAuthAudits.Appended);
            Assert.Equal(AuthEventType.LoginSuccess, audit.EventType);
            Assert.Null(audit.Subject);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Non_200_from_graph_is_employee_profile_unavailable(HttpStatusCode status)
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Graph.Status = status;
            factory.Graph.Body = """{"error":{"code":"x","message":"employee-secret-canary"}}""";
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            AssertDenied(factory, response, "employee-profile-unavailable"); // REQ-1.15
            Assert.Single(factory.Graph.Requests); // REQ-1.18 no retry
        }
    }

    [Fact]
    public async Task Graph_timeout_or_transport_failure_is_employee_profile_unavailable()
    {
        foreach (var failure in new Exception[] { new TaskCanceledException("timeout"), new HttpRequestException("dns") })
        {
            var (factory, client) = Build(requireEmployeeProfile: true);
            using (factory)
            using (client)
            {
                factory.Graph.Throw = failure;
                var challenge = await StartAsync(client);

                var response = await CallbackAsync(client, factory, challenge);

                AssertDenied(factory, response, "employee-profile-unavailable"); // REQ-1.14
            }
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"employeeId": """)]
    public async Task Malformed_graph_json_is_employee_profile_unavailable(string body)
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Graph.Body = body;
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            AssertDenied(factory, response, "employee-profile-unavailable"); // REQ-1.16
        }
    }

    [Theory]
    [InlineData("""{"displayName":"x"}""")]
    [InlineData("""{"employeeId":null}""")]
    [InlineData("""{"employeeId":""}""")]
    [InlineData("""{"employeeId":"   "}""")]
    public async Task Missing_or_blank_employee_id_is_employee_profile_missing(string body)
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Graph.Body = body;
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            AssertDenied(factory, response, "employee-profile-missing"); // REQ-1.17 / 2.2
        }
    }

    [Theory]
    [InlineData("""{"employeeId":"12345678901234567"}""")]   // 17 chars (REQ-2.4)
    [InlineData("""{"employeeId":"ab 12"}""")]              // inner whitespace (REQ-2.3)
    [InlineData("""{"employeeId":"ab\u0007cd"}""")]      // JSON-escaped control character (REQ-2.3)
    [InlineData("""{"employeeId":123}""")]                   // wrong JSON type
    public async Task Malformed_employee_id_is_employee_profile_invalid(string body)
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Graph.Body = body;
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            AssertDenied(factory, response, "employee-profile-invalid");
        }
    }

    [Fact]
    public async Task Missing_access_token_in_the_code_exchange_is_employee_profile_unavailable_without_a_graph_call()
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            factory.Backchannel.OmitAccessToken = true;
            var challenge = await StartAsync(client);

            var response = await CallbackAsync(client, factory, challenge);

            AssertDenied(factory, response, "employee-profile-unavailable"); // REQ-1.4
            Assert.Empty(factory.Graph.Requests);
        }
    }

    [Fact]
    public async Task Workforce_gate_failure_still_wins_and_never_calls_graph()
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            var challenge = await StartAsync(client);
            factory.Backchannel.IdToken = GraphTestOidc.CreateIdToken(GraphE2EFactory.AdminMicrosoftClient, challenge.Nonce,
                ("sub", "pairwise"), ("tid", GraphTestOidc.WorkforceTenant),
                ("oid", "not-a-guid"), ("email", "outsider@example.com"));
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{Callback}?code=e2e-code&state={Uri.EscapeDataString(challenge.State)}");
            request.Headers.Add("Cookie", challenge.Cookies);

            var response = await client.SendAsync(request);

            Assert.Equal("workforce-access-denied", Reason(response)); // REQ-1.8: gate BEFORE Graph
            Assert.Empty(factory.Graph.Requests);
            Assert.Null(factory.AdminResolver.Resolved);
        }
    }

    [Theory]
    [InlineData("tid")]
    [InlineData("oid")]
    public async Task Duplicate_tuple_claim_is_denied_before_graph_resolution_or_session(string duplicateType)
    {
        var (factory, client) = Build(requireEmployeeProfile: true);
        using (factory)
        using (client)
        {
            var challenge = await StartAsync(client);
            var claims = new List<(string Type, string Value)>
            {
                ("sub", "pairwise"),
                ("tid", GraphTestOidc.WorkforceTenant),
                ("oid", GraphTestOidc.WorkforceObject),
                ("email", Email),
            };
            claims.Add((duplicateType, claims.Single(claim => claim.Type == duplicateType).Value));
            factory.Backchannel.IdToken = GraphTestOidc.CreateIdToken(
                GraphE2EFactory.AdminMicrosoftClient, challenge.Nonce, [.. claims]);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{Callback}?code=e2e-code&state={Uri.EscapeDataString(challenge.State)}");
            request.Headers.Add("Cookie", challenge.Cookies);

            var response = await client.SendAsync(request);

            AssertDenied(factory, response, "workforce-access-denied");
            Assert.Empty(factory.Graph.Requests);
        }
    }

    // ---- switch off (REQ-10.13, 12.2-12.5) ----

    [Fact]
    public async Task Switch_off_keeps_the_original_scopes_never_calls_graph_and_passes_a_null_employee_id()
    {
        var (factory, client) = Build(requireEmployeeProfile: false);
        using (factory)
        using (client)
        {
            var challenge = await StartAsync(client);
            Assert.Equal("openid email profile", challenge.Scope);

            var response = await CallbackAsync(client, factory, challenge);

            Assert.Equal("not-provisioned", Reason(response));
            Assert.Equal(new GraphAdminResolved(
                User.MicrosoftProvider, Guid.Parse(GraphTestOidc.WorkforceTenant),
                GraphTestOidc.WorkforceObject, Email, null), factory.AdminResolver.Resolved);
            Assert.Empty(factory.Graph.Requests);
        }
    }

    private static void AssertDenied(GraphE2EFactory factory, HttpResponseMessage response, string reason)
    {
        Assert.Equal(reason, Reason(response));
        Assert.Null(factory.AdminResolver.Resolved);        // REQ-1.20: no resolve/JIT/bind
        Assert.Empty(factory.AdminSessions.Added);          // REQ-1.19: no session
        var audit = Assert.Single(factory.AdminAuthAudits.Appended);
        Assert.Equal(AuthEventType.AuthDenied, audit.EventType);
        Assert.Equal(reason, audit.Reason);                 // REQ-1.21
        Assert.Null(audit.Subject);
        // REQ-9.7/9.8: no token, employeeId, email or Graph body in the redirect or the audit.
        var surface = string.Join('\n', response.Headers.Location!.ToString(), audit.Subject, audit.Reason, audit.CorrelationId);
        Assert.DoesNotContain(GraphTestOidc.AccessToken, surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canary", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("employeeId\"", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Email, surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c =>
            c.StartsWith("__Host-adm_session", StringComparison.Ordinal) || c.StartsWith("adm_session", StringComparison.Ordinal));
    }
}
