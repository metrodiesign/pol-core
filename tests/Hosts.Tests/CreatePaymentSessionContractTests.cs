extern alias ApiHost;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payments.Domain.Psp;

namespace Hosts.Tests;

// captive-payment-alignment REQ-1.4/1.6: POST /api/v1/payments/sessions no longer accepts an amount — the
// charge is priced from the order row server-side. This is the literal-pin the LESSONS entry asks for on a
// flat wire contract: every other assertion in the suite goes through the DTO's members, so silently
// re-adding an Amount property (or dropping the 404/409 declarations the new refusal paths produce) would
// otherwise leave nothing red.

file sealed class PaymentSessionContractFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
    }
}

public sealed class CreatePaymentSessionContractTests
{
    // The host's own converter set (Program.cs ConfigureHttpJsonOptions) so this binds exactly as a real
    // request does — "psp" crosses the wire as its stable code ("2c2p"), never as an int or a C# member name.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ApiHost::PspCodeJsonConverter() },
    };

    [Fact]
    public void The_request_contract_carries_no_amount()
    {
        var members = typeof(ApiHost::CreatePaymentSessionRequest)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(["OrderId", "Method", "Psp", "MerchantId"], members);
        Assert.Null(typeof(ApiHost::CreatePaymentSessionRequest).GetProperty("Amount"));
    }

    [Fact]
    public void A_body_that_still_sends_an_amount_binds_without_carrying_it()
    {
        // A stale client keeps posting "amount": there is nowhere for it to land, so it cannot influence what
        // is charged — the request still binds (no 400 for the extra key) and the order remains the only price.
        const string StaleBody = """
            { "orderId": "22222222-2222-2222-2222-222222222222", "amount": { "amount": "1.0000", "currency": "THB" },
              "method": "card", "psp": "2c2p" }
            """;

        var request = JsonSerializer.Deserialize<ApiHost::CreatePaymentSessionRequest>(StaleBody, Web)!;

        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), request.OrderId);
        Assert.Equal("card", request.Method);
        Assert.Equal(Code.TwoCTwoP, request.Psp);
        Assert.Null(typeof(ApiHost::CreatePaymentSessionRequest).GetProperty("Amount"));
    }

    [Fact]
    public void The_endpoint_declares_the_refusal_status_codes_it_can_now_produce()
    {
        using var factory = new PaymentSessionContractFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v1/payments/sessions"
                && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") ?? false));

        var declared = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
            .Select(m => m.StatusCode)
            .ToHashSet();

        Assert.Contains(StatusCodes.Status400BadRequest, declared);   // method outside the vocabulary
        Assert.Contains(StatusCodes.Status404NotFound, declared);     // order not visible to this merchant
        Assert.Contains(StatusCodes.Status409Conflict, declared);     // order state / connection / adapter / open session
    }
}
