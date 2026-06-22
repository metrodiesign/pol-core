extern alias ApiHost;

using System.Net;
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
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = UnusedConn,
                ["ConnectionStrings:Admin"] = UnusedConn,
                ["ConnectionStrings:Worker"] = UnusedConn,
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Tenant:DevTenantId"] = "00000000-0000-0000-0000-000000000001",
                ["Google:ClientId"] = "test-client-id.apps.googleusercontent.com",
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

        var response = await client.PostAsync($"/webhooks/{Guid.NewGuid()}", new StringContent("{}"));
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
    public async Task Redirect_endpoint_requires_authentication()
    {
        using var factory = new HardeningFactory<ApiHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/payment-sessions/{Guid.NewGuid()}/redirect", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
            client.PostAsync($"/webhooks/{connectionId}", new StringContent("{}"))));

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

file static class FactoryExtensions
{
    public static WebApplicationFactory<TEntry> WithFastFailDatabase<TEntry>(
        this WebApplicationFactory<TEntry> factory) where TEntry : class =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Producer"] = HardeningFactory<TEntry>.FastFailConn,
                    ["ConnectionStrings:Admin"] = HardeningFactory<TEntry>.FastFailConn,
                    ["ConnectionStrings:Worker"] = HardeningFactory<TEntry>.FastFailConn,
                })));
}
