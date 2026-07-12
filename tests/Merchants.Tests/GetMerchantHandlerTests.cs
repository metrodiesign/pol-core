using BuildingBlocks.Application;
using Payments.Domain;
using Merchants.Application.GetMerchant;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Merchants.Domain.Users.Permissions;

namespace Merchants.Tests;

public sealed class GetMerchantHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Missing_merchant_throws_not_found()
    {
        var handler = new GetMerchantHandler(new FakeMerchantRepository { ByCode = null }, new FakePspConnectionRepository());

        await Assert.ThrowsAsync<NotFoundException>(async () => await handler.Handle(new GetMerchantQuery("vcommerce"), default));
    }

    [Fact]
    public async Task Returns_view_with_masked_secret_hints_from_connection_metadata()
    {
        var merchant = Merchant.Create("vcommerce", "vCommerce", "0105560000000", "TH", "THB", ["card"], null, Now);
        var connection = PspConnection.Create(merchant.Id, PspCode.Omise, "card", "psp/omise", Now,
            """{"config":{"accountId":"acct_1"},"secretHints":{"secretKey":"5678"}}""");
        var psp = new FakePspConnectionRepository();
        psp.Add(connection);
        var handler = new GetMerchantHandler(new FakeMerchantRepository { ByCode = merchant }, psp);

        var view = await handler.Handle(new GetMerchantQuery("VCommerce"), default);

        Assert.Equal("vcommerce", view.Code);
        Assert.Equal("Active", view.Status);
        var c = Assert.Single(view.Connections);
        Assert.Equal("omise", c.Psp);
        Assert.Equal("****5678", c.MaskedSecrets["secretKey"]);
    }

    [Fact]
    public async Task Read_back_surfaces_stored_config_and_psp_merchant_id() // Codex re-review P2 (REQ-9.1)
    {
        var merchant = Merchant.Create("vcommerce", "vCommerce", "0105560000000", "TH", "THB", ["card", "installment"],
            """{"branding":{"logo":"x"},"routing":{"installment":["2c2p","omise"]}}""", Now);
        var connection = PspConnection.Create(merchant.Id, PspCode.TwoCTwoP, "card,installment", "psp/2c2p", Now,
            """{"config":{"environment":"production","currencyCode":"764"},"merchantId":"merch_42","secretHints":{"secretKey":"3a9f"}}""");
        var psp = new FakePspConnectionRepository();
        psp.Add(connection);
        var handler = new GetMerchantHandler(new FakeMerchantRepository { ByCode = merchant }, psp);

        var view = await handler.Handle(new GetMerchantQuery("vcommerce"), default);

        // Merchant non-secret config (branding/routing) is returned for verification.
        Assert.True(view.Metadata!.Value.TryGetProperty("branding", out _));
        Assert.True(view.Metadata!.Value.TryGetProperty("routing", out _));

        // Connection config + the PSP's OWN merchant id + enabled methods are returned; secret stays masked, no
        // plaintext. (PspConnectionSpec.MerchantId is frozen PSP vocabulary — the 2C2P/Omise gateway's own
        // merchant id, unrelated to the actor MerchantId this rf1 adds — see task-3-handoff.md.)
        var c = Assert.Single(view.Connections);
        Assert.Equal("2c2p", c.Psp);
        Assert.Equal("merch_42", c.MerchantId);
        Assert.Equal(["card", "installment"], c.EnabledMethods);
        Assert.Equal("production", c.Config!.Value.GetProperty("environment").GetString());
        Assert.Equal("764", c.Config!.Value.GetProperty("currencyCode").GetString());
        Assert.Equal("****3a9f", c.MaskedSecrets["secretKey"]);
    }
}
