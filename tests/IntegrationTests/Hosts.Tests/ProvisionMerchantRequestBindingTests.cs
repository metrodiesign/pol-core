extern alias ApiHost;
using System.Text.Json;

namespace Hosts.Tests;

/// <summary>
/// The admin provisioning DTO must accept the documented reference-2.4 body verbatim: merchant fields are
/// nested under "merchant" (Codex P1 — a flat DTO bound them null -> 400), and non-secret PSP config rides at
/// the top level of each connection alongside "psp"/"secrets" and must be preserved (Codex P2 — a lone
/// "config" property dropped it). These bind with the host's Web JSON defaults — no DB needed.
/// </summary>
public sealed class ProvisionMerchantRequestBindingTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string DocumentedBody = """
        {
          "merchant": {
            "code": "vcommerce",
            "name": "vCommerce Co., Ltd.",
            "note": "commerce merchant",
            "country": "TH",
            "currency": "THB",
            "enabledChannels": ["card", "promptpay", "installment"],
            "branding": { "logoUrl": "https://pay.vgroup.internal/logo.png" },
            "routing": { "installment": ["omise", "2c2p"] },
            "session": { "ttlSeconds": 900 }
          },
          "pspConnections": [
            {
              "psp": "2c2p",
              "environment": "production",
              "currencyCode": "764",
              "card": { "secure3ds": true },
              "frontendReturnUrl": "https://pay.vgroup.internal/return/vcommerce/2c2p",
              "enabledMethods": ["card", "installment"],
              "secrets": { "secretKey": "merchant_secret_value" }
            }
          ]
        }
        """;

    [Fact]
    public void Nested_merchant_fields_bind() // Codex P1
    {
        var req = JsonSerializer.Deserialize<ApiHost::ProvisionMerchantRequest>(DocumentedBody, Web)!;

        Assert.NotNull(req.Merchant);
        Assert.Equal("vcommerce", req.Merchant!.Code);
        Assert.Equal("THB", req.Merchant.Currency);
        Assert.Equal(3, req.Merchant.EnabledChannels!.Count);
    }

    [Fact]
    public void Flexible_merchant_keys_are_captured_not_dropped()
    {
        var req = JsonSerializer.Deserialize<ApiHost::ProvisionMerchantRequest>(DocumentedBody, Web)!;

        Assert.Equal("https://pay.vgroup.internal/logo.png", req.Merchant!.Branding!.LogoUrl);
        Assert.Equal(["omise", "2c2p"], req.Merchant.Routing!.Installment);
        Assert.Equal(900, req.Merchant.Session!.TtlSeconds);
    }

    [Fact]
    public void Psp_secret_binds_separately_from_config() // Codex P2
    {
        var req = JsonSerializer.Deserialize<ApiHost::ProvisionMerchantRequest>(DocumentedBody, Web)!;
        var conn = Assert.Single(req.PspConnections!);

        Assert.Equal("2c2p", conn.Psp);
        Assert.Equal("merchant_secret_value", conn.Secrets!["secretKey"]);
        Assert.Equal(2, conn.EnabledMethods!.Count);

        var config = conn.Config!;
        Assert.Contains("environment", config.Keys);
        Assert.Contains("currencyCode", config.Keys);
        Assert.Contains("card", config.Keys);
        Assert.Contains("frontendReturnUrl", config.Keys);
        Assert.DoesNotContain("secrets", config.Keys); // secrets never leak into stored config
        Assert.DoesNotContain("psp", config.Keys);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("token")]
    [InlineData("password")]
    [InlineData("apiKey")]
    [InlineData("privateKey")]
    [InlineData("connectionString")]
    [InlineData("unknownField")]
    public void Merchant_metadata_rejects_secret_shaped_and_unknown_fields(string field)
    {
        var body = $$"""
            {
              "merchant": {
                "code": "vcommerce",
                "name": "vCommerce",
                "country": "TH",
                "currency": "THB",
                "enabledChannels": [],
                "{{field}}": "must-not-bind"
              },
              "pspConnections": []
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApiHost::ProvisionMerchantRequest>(body, Web));
    }
}
