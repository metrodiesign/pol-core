extern alias ApiHost;

using System.Net;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// PR2 HTTP-surface + observability hardening, asserted against the real hosts via WebApplicationFactory
// with NO live database (the SQL DbContexts get never-opened/fast-failing connection strings, and the
// outbox dispatcher BackgroundService is removed). Each test exercises a single hardening: split health
// endpoints, correlation id, auth on the redirect endpoint, webhook rate limiting, and readiness non-leak.

file sealed class HardeningFactory<TEntry> : WebApplicationFactory<TEntry>
    where TEntry : class
{
    // Default: a never-opened connection (the dependency-free paths never touch it). Tests that probe the
    // DB override this with a fast-failing endpoint so the assertion is quick and deterministic.
    public const string UnusedConn = "Server=(local);Database=pol_test;Trusted_Connection=True;";
    public const string FastFailConn = "Server=127.0.0.1,1;Database=pol_test;Connect Timeout=1;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Google:Audiences is read EAGERLY at service registration (to register the per-role policies), so it
        // must be host config (UseSetting) — an in-memory source added via ConfigureAppConfiguration lands too
        // late and the "tenant" policy never registers. Production supplies it via env at process start.
        builder.UseSetting("Google:Audiences:tenant", "test-client-id.apps.googleusercontent.com");
        // Anything a developer's local appsettings.Development.json or user-secrets could override must be host
        // config (UseSetting), not an in-memory source added via ConfigureAppConfiguration — those layers sit
        // ABOVE that in-memory source, so a real local connection string or admin OIDC client id would otherwise
        // leak in (reachable DB, configured OIDC) and defeat these hermetic assertions.
        builder.UseSetting("ConnectionStrings:Producer", UnusedConn);
        builder.UseSetting("ConnectionStrings:Worker", UnusedConn);
        // Pin the admin OIDC client UNCONFIGURED (blank id): this hardening surface must stay up even when the
        // admin BFF login is not configured. The OIDC scheme is a per-request handler whose options are validated
        // on every request, so a blank ClientId used to 400 the WHOLE API (AddAdminOidcAuthentication now skips
        // the scheme when blank). Forced blank — overriding any local non-blank value — so this regression is
        // caught on every platform.
        builder.UseSetting("Google:Oidc:ClientId", "");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Tenant:DevTenantId"] = "00000000-0000-0000-0000-000000000001",
            });
        });
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null)
                services.Remove(dispatcher);
        });
    }
}

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Liveness_returns_200_without_touching_a_database()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_returns_503_with_a_minimal_body_when_the_database_is_unreachable()
    {
        using var factory = new HardeningFactory<ApiHost::Program>()
            .WithFastFailDatabase();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not_ready", body);
        // No topology, connection string, DbContext name, or exception text may leak in the probe body.
        Assert.DoesNotContain("producer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Liveness_stays_200_while_readiness_is_503_when_the_database_is_down()
    {
        // The discriminating test: with a CONFIRMED-dead database, liveness must still be 200 (it runs no
        // checks) while readiness is 503 — proving the split is real, not incidental to a reachable DB.
        using var factory = new HardeningFactory<ApiHost::Program>().WithFastFailDatabase();
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }
}

public sealed class CorrelationIdTests
{
    [Fact]
    public async Task A_correlation_id_is_generated_when_the_client_sends_none()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        var id = values!.Single();
        Assert.Equal(32, id.Length);
        Assert.True(id.All(Uri.IsHexDigit));
    }

    [Fact]
    public async Task A_well_formed_client_correlation_id_is_echoed()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "trace-abc-123");
        var response = await client.SendAsync(request);

        Assert.Equal("trace-abc-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task A_malformed_client_correlation_id_is_rejected_and_replaced()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        // Over-length + disallowed characters: the IsWellFormed guard must reject this (which also blocks
        // response-header injection) and mint a fresh id instead of echoing the attacker-controlled value.
        var malformed = new string('x', 200) + " bad:value";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", malformed);
        var response = await client.SendAsync(request);

        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.NotEqual(malformed, echoed);
        Assert.Equal(32, echoed.Length);
        Assert.True(echoed.All(Uri.IsHexDigit));
    }
}

public sealed class ExceptionHandlerPipelineTests
{
    [Fact]
    public async Task An_unhandled_error_is_returned_as_problem_json_without_leaking_internals()
    {
        // A single webhook POST (well under the rate limit) reaches the tenant resolver, whose DB call fails
        // on the fast-fail connection. That exception must surface through UseExceptionHandler ->
        // ProblemDetailsExceptionHandler as an OPAQUE 500 (application/problem+json, no internal detail),
        // proving the handler is wired into the real pipeline and never leaks SQL/connection text.
        using var factory = new HardeningFactory<ApiHost::Program>().WithFastFailDatabase();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/v1/webhooks/{Guid.NewGuid()}", new StringContent("{}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("Server=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", body);
        Assert.DoesNotContain("StackTrace", body);
    }
}

public sealed class RedirectEndpointAuthTests
{
    [Fact]
    public async Task Redirect_endpoint_requires_authentication_and_returns_problem_details()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/v1/payments/sessions/{Guid.NewGuid()}/redirect", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // UseStatusCodePages + AddProblemDetails render the framework 401 as RFC7807, not an empty body.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

public sealed class OpenApiDocumentTests
{
    [Fact]
    public async Task OpenApi_document_is_served_in_development_and_describes_psp_codes()
    {
        // The SPA teams' machine-readable contract. Development env (HardeningFactory) maps it.
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The PspCode schema must document the real wire shape (string codes), not the int the custom
        // converter would otherwise leave unschematized — or a generated client would send the wrong type.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var psp = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("PspCode");
        Assert.Equal("string", psp.GetProperty("type").GetString());
        var codes = psp.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("2c2p", codes);
        Assert.Contains("omise", codes);
    }
}

public sealed class WebhookRateLimitTests
{
    [Fact]
    public async Task A_flood_to_one_connection_is_rejected_with_429_and_Retry_After()
    {
        // Fast-failing DB so the admitted requests (which reach the tenant resolver) return immediately;
        // the 429 path never touches the DB. We only assert the rate-limit decision, which is DB-independent.
        using var factory = new HardeningFactory<ApiHost::Program>()
            .WithFastFailDatabase();
        using var client = factory.CreateClient();

        var connectionId = Guid.NewGuid();

        // Fire concurrently so all requests reach the limiter within one window regardless of each admitted
        // request's downstream DB latency — the 429 decision happens in middleware, before the DB call, and
        // all requests share one partition (the loopback source IP).
        var responses = await Task.WhenAll(Enumerable.Range(0, 70).Select(_ =>
            client.PostAsync($"/api/v1/webhooks/{connectionId}", new StringContent("{}"))));

        try
        {
            var tooMany = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
            Assert.NotEmpty(tooMany);
            // Every rejection must carry a positive Retry-After (the OnRejected fallback guarantees one).
            Assert.All(tooMany, r =>
            {
                Assert.True(r.Headers.TryGetValues("Retry-After", out var values));
                Assert.True(int.TryParse(values!.Single(), out var seconds) && seconds > 0);
            });
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }
}

public sealed class ForwardedHeadersConfigTests
{
    [Fact]
    public async Task Configured_known_networks_parse_and_blank_entries_are_skipped_without_failing_boot()
    {
        // A valid CIDR must parse into KnownIPNetworks, and a blank entry (what an unset `${VAR:-}` env
        // expands to) must be skipped — not crash boot with IPNetwork.Parse(""). Building the host runs the
        // UseForwardedHeaders config block, so a 200 from liveness proves the parse succeeded.
        using var factory = new HardeningFactory<ApiHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "172.18.0.0/16");
            builder.UseSetting("ForwardedHeaders:KnownNetworks:1", "");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

file static class FactoryExtensions
{
    public static WebApplicationFactory<TEntry> WithFastFailDatabase<TEntry>(
        this WebApplicationFactory<TEntry> factory) where TEntry : class =>
        factory.WithWebHostBuilder(builder =>
        {
            // UseSetting (host config), not ConfigureAppConfiguration: a local appsettings.Development.json or
            // user-secrets connection string would otherwise win and make the DB reachable, defeating the
            // unreachable-DB assertions.
            builder.UseSetting("ConnectionStrings:Producer", HardeningFactory<TEntry>.FastFailConn);
            builder.UseSetting("ConnectionStrings:Worker", HardeningFactory<TEntry>.FastFailConn);
        });
}
