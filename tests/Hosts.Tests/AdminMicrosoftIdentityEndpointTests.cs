extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

file sealed class MicrosoftIdentityTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MicrosoftIdentityTestAdmin";
    public const string TierHeader = "X-Test-Admin-Tier";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TierHeader, out var tier))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
        [
            new Claim("admin_tier", tier.ToString()),
            new Claim(ClaimTypes.NameIdentifier, MicrosoftIdentityAdminScope.AdminId.ToString("D")),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class MicrosoftIdentityAdminScope : IAdminScope
{
    public static readonly Guid AdminId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    public bool IsBound => true;
    public Resolution Current { get; } = new(
        AdminId,
        "super@example.test",
        Tier.Super,
        AccessibleMerchants.All)
    {
        AuthorizationVersion = 17,
    };
    public AccessibleMerchants Accessible => Current.Accessible;
}

file sealed class RecordingMicrosoftIdentityMediator : IMediator
{
    public PreProvisionMicrosoftIdentityCommand? Command { get; private set; }
    public int Calls { get; private set; }
    public Exception? Failure { get; set; }

    private ValueTask<TResponse> Dispatch<TResponse>(object message)
    {
        if (message is not PreProvisionMicrosoftIdentityCommand command)
            throw new NotSupportedException($"Unexpected message '{message.GetType().Name}'.");

        Command = command;
        Calls++;
        if (Failure is { } failure)
            throw failure;
        object result = new PreProvisionMicrosoftIdentityResult(
            command.TargetAdminId,
            User.MicrosoftProvider,
            SubjectBound: true,
            Version: 42);
        return ValueTask.FromResult((TResponse)result);
    }

    public ValueTask<TResponse> Send<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default) => Dispatch<TResponse>(request);
    public ValueTask<TResponse> Send<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken = default) => Dispatch<TResponse>(command);
    public ValueTask<TResponse> Send<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken = default) => Dispatch<TResponse>(query);
    public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
        Dispatch<object?>(message);
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(
        object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask Publish<TNotification>(
        TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification => throw new NotSupportedException();
    public ValueTask Publish(object notification, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

file sealed class MicrosoftIdentityEndpointFactory(bool useTestAdmin = true)
    : WebApplicationFactory<ApiHost::Program>
{
    public const string Tenant = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string UnusedConnection =
        "Server=(local);Database=pol_test;Trusted_Connection=True;";

    public RecordingMicrosoftIdentityMediator Mediator { get; } = new();
    public TestWorkforceTenantBindingStore TenantBindingStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(ApiContentRoot());
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", UnusedConnection);
        builder.UseSetting("ConnectionStrings:Admin", UnusedConnection);
        builder.UseSetting("ConnectionStrings:Worker", UnusedConnection);
        builder.UseSetting("AdminAuth:Providers:Google:ClientId", "");
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority",
            $"https://login.microsoftonline.com/{Tenant}/v2.0");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", "identity-contract-client");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath",
            "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("MerchantAuth:Providers:Google:ClientId", "");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", "");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.IgnoreMachineLocalDevelopmentSettings();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
            services.PostConfigure<OpenIdConnectOptions>(
                ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft",
                options => options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                    {
                        Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                        AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                        TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                        JwksUri = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys",
                    }));
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkforceTenantBindingStore>();
            services.AddSingleton<IWorkforceTenantBindingStore>(TenantBindingStore);
            services.AddSingleton<IMediator>(Mediator);

            if (!useTestAdmin)
                return;

            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MicrosoftIdentityTestAuthHandler>(
                MicrosoftIdentityTestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(MicrosoftIdentityTestAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));
            services.AddScoped<IAdminScope>(_ => new MicrosoftIdentityAdminScope());
        });
    }

    private static string ApiContentRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
                return Path.Combine(directory.FullName, "src", "Hosts", "Api");
        }
        throw new DirectoryNotFoundException("Could not locate the repository root for the Api test host.");
    }
}

public sealed class AdminMicrosoftIdentityEndpointTests
{
    private static readonly Guid TargetAdminId =
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid TenantId = Guid.Parse(MicrosoftIdentityEndpointFactory.Tenant);
    private static readonly Guid ObjectId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly string Route =
        $"/api/v1/admins/{TargetAdminId:D}/microsoft-identity";

    [Fact]
    public async Task Valid_request_returns_exact_contract_and_dispatches_canonical_command()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(
            Body(TenantId.ToString("B").ToUpperInvariant(), ObjectId.ToString("P").ToUpperInvariant(),
                "  quarterly access review  "),
            idempotencyKey: "  identity-contract-key  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v42\"", response.Headers.ETag?.Tag);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = body.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.Equal(4, properties.Count);
        Assert.True(properties.SetEquals(["adminId", "provider", "subjectBound", "version"]));
        Assert.Equal(TargetAdminId, body.RootElement.GetProperty("adminId").GetGuid());
        Assert.Equal("microsoft", body.RootElement.GetProperty("provider").GetString());
        Assert.True(body.RootElement.GetProperty("subjectBound").GetBoolean());
        Assert.Equal(42, body.RootElement.GetProperty("version").GetInt64());

        var command = Assert.IsType<PreProvisionMicrosoftIdentityCommand>(factory.Mediator.Command);
        Assert.Equal(TargetAdminId, command.TargetAdminId);
        Assert.Equal(TenantId, command.WorkforceTenantId);
        Assert.Equal(ObjectId, command.EntraObjectId);
        Assert.Equal("quarterly access review", command.Reason);
        Assert.Equal(MicrosoftIdentityAdminScope.AdminId, command.ActingAdminId);
        Assert.Equal(17, command.ExpectedAuthorizationVersion);
        Assert.Equal(7, command.ExpectedTargetVersion);
        Assert.False(string.IsNullOrWhiteSpace(command.CorrelationId));
        Assert.Equal("identity-contract-key", command.IdempotencyKey);
        Assert.Equal(TenantId, command.ConfiguredWorkforceTenantId);
    }

    [Fact]
    public async Task Unknown_request_property_is_400_before_dispatch()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(
            $$"""{"workforceTenantId":"{{TenantId:D}}","entraObjectId":"{{ObjectId:D}}","reason":"review","unexpected":true}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Invalid_tenant_and_object_ids_return_stable_codes_before_dispatch()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        var cases = new[]
        {
            (Body(null, ObjectId.ToString("D"), "review"), "invalid_entra_tenant_id"),
            (Body("not-a-guid", ObjectId.ToString("D"), "review"), "invalid_entra_tenant_id"),
            (Body(Guid.Empty.ToString("D"), ObjectId.ToString("D"), "review"), "invalid_entra_tenant_id"),
            (Body(TenantId.ToString("D"), null, "review"), "invalid_entra_object_id"),
            (Body(TenantId.ToString("D"), "not-a-guid", "review"), "invalid_entra_object_id"),
            (Body(TenantId.ToString("D"), Guid.Empty.ToString("D"), "review"), "invalid_entra_object_id"),
        };

        foreach (var (json, expectedCode) in cases)
        {
            using var response = await client.SendAsync(Request(json));
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedCode);
        }
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Invalid_reason_and_every_guid_representation_are_rejected_before_dispatch()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        var reasons = new List<string?> { null, " ", new('x', 1001), "owner@example.test" };
        foreach (var guid in new[] { TenantId, ObjectId })
        foreach (var format in new[] { "D", "N", "B", "P", "X" })
            reasons.Add($"identity {guid.ToString(format).ToUpperInvariant()}");

        foreach (var reason in reasons)
        {
            using var response = await client.SendAsync(Request(
                Body(TenantId.ToString("D"), ObjectId.ToString("D"), reason)));
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_reason");
        }
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Missing_or_invalid_concurrency_headers_return_stable_codes_before_dispatch()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        var json = Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review");

        foreach (var etag in new string?[] { null, "W/\"v7\"", "\"7\"", "\"v-1\"" })
        {
            using var response = await client.SendAsync(Request(json, etag: etag));
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_etag");
        }
        foreach (var key in new string?[] { null, "", new('k', 201) })
        {
            using var response = await client.SendAsync(Request(json, idempotencyKey: key));
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_idempotency_key");
        }
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Missing_admin_session_returns_stable_401_problem()
    {
        using var factory = new MicrosoftIdentityEndpointFactory(useTestAdmin: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.SendAsync(Request(
            Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review"), tier: null));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "admin_session_required");
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Login_flood_does_not_consume_the_authenticated_identity_mutation_budget()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < 20; i++)
        {
            using var admitted = await client.GetAsync("/api/v1/admins/auth/microsoft/login");
            Assert.Equal(HttpStatusCode.Found, admitted.StatusCode);
        }
        using (var rejectedLogin = await client.GetAsync("/api/v1/admins/auth/microsoft/login"))
            Assert.Equal(HttpStatusCode.TooManyRequests, rejectedLogin.StatusCode);

        for (var i = 0; i < 20; i++)
        {
            using var admitted = await client.SendAsync(Request(
                Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review"),
                idempotencyKey: $"identity-rate-{i}"));
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }
        using var rejectedMutation = await client.SendAsync(Request(
            Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review"),
            idempotencyKey: "identity-rate-over-limit"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedMutation.StatusCode);
    }

    [Fact]
    public void Identity_mutation_budget_is_partitioned_by_internal_admin_id()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        var limiter = factory.Services.GetRequiredService<PartitionedRateLimiter<Guid>>();

        for (var i = 0; i < 20; i++)
        {
            using var admitted = limiter.AttemptAcquire(MicrosoftIdentityAdminScope.AdminId, permitCount: 1);
            Assert.True(admitted.IsAcquired);
        }
        using var rejected = limiter.AttemptAcquire(MicrosoftIdentityAdminScope.AdminId, permitCount: 1);
        Assert.False(rejected.IsAcquired);

        using var otherAdmin = limiter.AttemptAcquire(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), permitCount: 1);
        Assert.True(otherAdmin.IsAcquired);
    }

    [Fact]
    public async Task Missing_csrf_returns_stable_403_problem()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(
            Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review"), csrf: null));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "csrf_failed");
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Fact]
    public async Task Scoped_admin_returns_stable_403_problem()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(
            Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review"), tier: "Scoped"));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "super_required");
        Assert.Equal(0, factory.Mediator.Calls);
    }

    [Theory]
    [InlineData("entra_tenant_mismatch", 400)]
    [InlineData("admin_not_found", 404)]
    [InlineData("microsoft_provider_disabled", 409)]
    [InlineData("target_not_scoped", 409)]
    [InlineData("admin_identity_already_bound", 409)]
    [InlineData("microsoft_identity_already_bound", 409)]
    [InlineData("state_conflict", 409)]
    [InlineData("idempotency_key_reused", 409)]
    [InlineData("operation_in_progress", 409)]
    public async Task Application_failure_branches_keep_stable_problem_contract(string code, int status)
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        factory.Mediator.Failure = code switch
        {
            "entra_tenant_mismatch" => new InvalidRequestException("safe", code),
            "admin_not_found" => new NotFoundException("safe", code),
            "state_conflict" => new ConcurrencyConflictException("safe"),
            _ => new ConflictException("safe", code),
        };
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Request(
            Body(TenantId.ToString("D"), ObjectId.ToString("D"), "review")));

        await AssertProblemAsync(response, (HttpStatusCode)status, code);
    }

    [Fact]
    public async Task OpenApi_requires_both_concurrency_headers_and_exposes_response_etag()
    {
        using var factory = new MicrosoftIdentityEndpointFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admins/{id}/microsoft-identity")
            .GetProperty("put");

        Assert.Equal("PreProvisionAdminMicrosoftIdentity", operation.GetProperty("operationId").GetString());
        AssertRequiredHeader(operation, "If-Match");
        AssertRequiredHeader(operation, "Idempotency-Key");
        AssertRequiredHeader(operation, "X-CSRF-Token");
        Assert.True(operation.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("429", out _));
    }

    private static HttpRequestMessage Request(
        string json,
        string? tier = "Super",
        string? csrf = "identity-csrf",
        string? etag = "\"v7\"",
        string? idempotencyKey = "identity-contract-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (tier is not null)
            request.Headers.TryAddWithoutValidation(MicrosoftIdentityTestAuthHandler.TierHeader, tier);
        if (csrf is not null)
        {
            var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
            request.Headers.TryAddWithoutValidation(
                "Cookie", $"{cookieName}={csrf}");
            request.Headers.TryAddWithoutValidation(ApiHost::Api.Admins.CsrfFilter.HeaderName, csrf);
        }
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static string Body(string? tenant, string? objectId, string? reason) =>
        JsonSerializer.Serialize(new
        {
            workforceTenantId = tenant,
            entraObjectId = objectId,
            reason,
        });

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        var traceId = body.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(traceId, response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.DoesNotContain(TenantId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ObjectId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("review", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRequiredHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(parameter =>
            parameter.GetProperty("in").GetString() == "header"
            && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }
}
