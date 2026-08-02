extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Products.Application;
using Products.Domain;

namespace Hosts.Tests;

// purchase-flow-completion — the merchant cart/checkout lifecycle at the HTTP boundary, driven through the
// REAL route + policy + CSRF filter + minimal-API lambda (the layer InsuranceCheckoutEndToEndTests, which
// runs handlers directly on SQLite, cannot reach). The "merchant-user" policy is re-pointed at a fake
// always-present scheme (same trick as RegistrationHistoryEndpointTests) and IProductRepository is faked, so
// no live DB is touched on the paths asserted here.

file sealed class TestMerchantUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestMerchantUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "merchant-user-sub-1")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class OneProductRepository(Product? product) : IProductRepository
{
    public void Add(Product product) => throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
        IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(product);
}

file sealed class CartFactory(Product? product) : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestMerchantUserAuthHandler>(
                    TestMerchantUserAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", p => p
                .AddAuthenticationSchemes(TestMerchantUserAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            // Last-registered wins: the add-item gate's product read runs for real, no DB behind it.
            services.AddScoped<IProductRepository>(_ => new OneProductRepository(product));
        });
    }
}

public sealed class MerchantLifecycleEndpointTests
{
    // REQ-1.3 — a document that is no longer UNPAID is already sold, so POST /carts/{id}/items refuses it
    // with 400 at the endpoint, before any cart write is attempted. Removing or widening the gate makes the
    // request fall through to AddItemToCartCommand and stop being a 400.
    [Fact]
    public async Task Adding_a_product_that_is_not_UNPAID_is_rejected_with_400()
    {
        var sold = Product.Create(new ProductInput(
            ProductGroup.VMI, DocumentType.POLICY, "00098-69100/กธ/037676-10", "00098", 1200m,
            PaymentStatus.PAID, new DateTime(2026, 7, 15)));

        using var factory = new CartFactory(sold);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/carts/{Guid.NewGuid()}/items")
        {
            Content = new StringContent(
                $$"""{"productId":"{{sold.Id}}","quantity":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"{UserSessionCookies.CsrfCookieName}=tok-1");
        request.Headers.Add(UserCsrfFilter.HeaderName, "tok-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
