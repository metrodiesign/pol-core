extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application;
using SharedKernel;

namespace Hosts.Tests;

// purchase-flow-completion REQ-7.3's third surface at the HTTP boundary: the anonymous
// GET /orders/{token}/summary response must carry the order number ON THE WIRE. The reader is faked (its
// real SQL is proven in Integration.Tests/OrderSummaryReaderIntegrationTests against a live SQL Server);
// this test pins the response contract itself, asserting the serialized JSON property — dropping OrderNo
// from OrderSummaryResponse (or renaming it on the wire) goes red here and nowhere else. It also pins the
// two fields that must NOT be there: the merchant id the reader now carries for the host's actor binding
// (REQ-8.4 — projecting it would tell an anonymous customer who the merchant is) and the payment-session id
// REQ-8.9 removed.

file sealed class FakeSummaryReader(OrderSummary summary) : IOrderSummaryReader
{
    public const string Token = "tok-summary-1";

    public Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(token == Token ? summary : null);
}

file sealed class SummaryFactory(OrderSummary summary) : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
            services.AddScoped<IOrderSummaryReader>(_ => new FakeSummaryReader(summary)));
    }
}

public sealed class OrderSummaryEndpointTests
{
    [Fact]
    public async Task The_summary_response_carries_the_order_number_and_leaks_neither_merchant_nor_session()
    {
        var summary = new OrderSummary(
            Guid.NewGuid(), Guid.NewGuid(), "ORD6900000042", Money.Of(15000m, "THB"), "Paid", "CARD",
            DateTime.UtcNow.AddHours(1),
            [new OrderSummaryLine(
                "00098-69100/กธ/900001-10", "VMI", "ประกันรถยนต์", 1, Money.Of(15000m, "THB"))]);
        using var factory = new SummaryFactory(summary);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/orders/{FakeSummaryReader.Token}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ORD6900000042", json.GetProperty("orderNo").GetString());
        var line = Assert.Single(json.GetProperty("lines").EnumerateArray());
        Assert.Equal("00098-69100/กธ/900001-10", line.GetProperty("productCode").GetString());
        Assert.Equal("VMI", line.GetProperty("variantCode").GetString());
        Assert.Equal(1, line.GetProperty("quantity").GetInt32());
        Assert.False(line.TryGetProperty("metadata", out _));
        Assert.False(line.TryGetProperty("documentNo", out _));
        Assert.False(line.TryGetProperty("insuredIdNumber", out _));
        Assert.False(json.TryGetProperty("merchantId", out _));
        Assert.False(json.TryGetProperty("paymentSessionId", out _));
    }
}
