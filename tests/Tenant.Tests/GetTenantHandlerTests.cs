using BuildingBlocks.Application;
using Payments.Domain;
using Tenant.Application.GetTenant;
using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Tests;

public sealed class GetTenantHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Missing_tenant_throws_not_found()
    {
        var handler = new GetTenantHandler(new FakeTenantRepository { ByCode = null }, new FakePspConnectionRepository());

        await Assert.ThrowsAsync<NotFoundException>(async () => await handler.Handle(new GetTenantQuery("vcommerce"), default));
    }

    [Fact]
    public async Task Returns_view_with_masked_secret_hints_from_connection_metadata()
    {
        var tenant = DomainTenant.Create("vcommerce", "vCommerce", "0105560000000", "TH", "THB", ["card"], null, Now);
        var connection = PspConnection.Create(tenant.Id, PspCode.Omise, "card", "psp/omise", Now,
            """{"config":{"accountId":"acct_1"},"secretHints":{"secretKey":"5678"}}""");
        var psp = new FakePspConnectionRepository();
        psp.Add(connection);
        var handler = new GetTenantHandler(new FakeTenantRepository { ByCode = tenant }, psp);

        var view = await handler.Handle(new GetTenantQuery("VCommerce"), default);

        Assert.Equal("vcommerce", view.Code);
        Assert.Equal("Active", view.Status);
        var c = Assert.Single(view.Connections);
        Assert.Equal("omise", c.Psp);
        Assert.Equal("****5678", c.MaskedSecrets["secretKey"]);
    }

    [Fact]
    public async Task Read_back_surfaces_stored_config_and_merchant_id() // Codex re-review P2 (REQ-9.1)
    {
        var tenant = DomainTenant.Create("vcommerce", "vCommerce", "0105560000000", "TH", "THB", ["card", "installment"],
            """{"branding":{"logo":"x"},"routing":{"installment":["2c2p","omise"]}}""", Now);
        var connection = PspConnection.Create(tenant.Id, PspCode.TwoCTwoP, "card,installment", "psp/2c2p", Now,
            """{"config":{"environment":"production","currencyCode":"764"},"merchantId":"merch_42","secretHints":{"secretKey":"3a9f"}}""");
        var psp = new FakePspConnectionRepository();
        psp.Add(connection);
        var handler = new GetTenantHandler(new FakeTenantRepository { ByCode = tenant }, psp);

        var view = await handler.Handle(new GetTenantQuery("vcommerce"), default);

        // Tenant non-secret config (branding/routing) is returned for verification.
        Assert.True(view.Metadata!.Value.TryGetProperty("branding", out _));
        Assert.True(view.Metadata!.Value.TryGetProperty("routing", out _));

        // Connection config + merchant id + enabled methods are returned; secret stays masked, no plaintext.
        var c = Assert.Single(view.Connections);
        Assert.Equal("2c2p", c.Psp);
        Assert.Equal("merch_42", c.MerchantId);
        Assert.Equal(["card", "installment"], c.EnabledMethods);
        Assert.Equal("production", c.Config!.Value.GetProperty("environment").GetString());
        Assert.Equal("764", c.Config!.Value.GetProperty("currencyCode").GetString());
        Assert.Equal("****3a9f", c.MaskedSecrets["secretKey"]);
    }
}
