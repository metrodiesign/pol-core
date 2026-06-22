using System.Text.Json;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Infrastructure.Psp;

namespace Payments.Tests;

public sealed class PspSecretEnvelopeFactoryTests
{
    private readonly PspSecretEnvelopeFactory _factory = new();

    private static Dictionary<string, string> Secrets(params (string Key, string Value)[] items) =>
        items.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    [Fact]
    public void TwoCTwoP_builds_envelope_with_merchant_and_secret_plus_hint()
    {
        var result = _factory.Build(new PspSecretInput(
            PspCode.TwoCTwoP, Secrets(("secretKey", "abcd1234secret9z")), "merchant-123"));

        using var doc = JsonDocument.Parse(result.EnvelopeJson);
        Assert.Equal("merchant-123", doc.RootElement.GetProperty("merchantId").GetString());
        Assert.Equal("abcd1234secret9z", doc.RootElement.GetProperty("secretKey").GetString());
        Assert.Equal("abcd1234secret9z"[^4..], result.Hints["secretKey"]);
        Assert.False(result.Hints.ContainsKey("merchantId")); // merchantId is not a masked secret
    }

    [Fact]
    public void TwoCTwoP_missing_secretKey_throws() =>
        Assert.Throws<ArgumentException>(() => _factory.Build(new PspSecretInput(
            PspCode.TwoCTwoP, Secrets(), "merchant-123")));

    [Fact]
    public void TwoCTwoP_missing_merchantId_throws() =>
        Assert.Throws<ArgumentException>(() => _factory.Build(new PspSecretInput(
            PspCode.TwoCTwoP, Secrets(("secretKey", "x")), MerchantId: null)));

    [Fact]
    public void Omise_secretKey_only_omits_optional_fields()
    {
        var result = _factory.Build(new PspSecretInput(
            PspCode.Omise, Secrets(("secretKey", "skey_test_abcd1234")), MerchantId: null));

        using var doc = JsonDocument.Parse(result.EnvelopeJson);
        Assert.Equal("skey_test_abcd1234", doc.RootElement.GetProperty("secretKey").GetString());
        Assert.False(doc.RootElement.TryGetProperty("publicKey", out _));
        Assert.False(doc.RootElement.TryGetProperty("webhookSecret", out _));
        Assert.Single(result.Hints);
        Assert.Equal("1234", result.Hints["secretKey"]);
    }

    [Fact]
    public void Omise_full_envelope_stores_all_and_masks_all()
    {
        var result = _factory.Build(new PspSecretInput(PspCode.Omise, Secrets(
            ("secretKey", "skey_live_aaaa1111"),
            ("publicKey", "pkey_live_bbbb2222"),
            ("webhookSecret", "whsec_cccc3333")), MerchantId: null));

        using var doc = JsonDocument.Parse(result.EnvelopeJson);
        Assert.Equal("skey_live_aaaa1111", doc.RootElement.GetProperty("secretKey").GetString());
        Assert.Equal("pkey_live_bbbb2222", doc.RootElement.GetProperty("publicKey").GetString());
        Assert.Equal("whsec_cccc3333", doc.RootElement.GetProperty("webhookSecret").GetString());
        Assert.Equal("1111", result.Hints["secretKey"]);
        Assert.Equal("2222", result.Hints["publicKey"]);
        Assert.Equal("3333", result.Hints["webhookSecret"]);
    }

    [Fact]
    public void Omise_missing_secretKey_throws() =>
        Assert.Throws<ArgumentException>(() => _factory.Build(new PspSecretInput(
            PspCode.Omise, Secrets(("publicKey", "pkey_x")), MerchantId: null)));

    [Fact]
    public void Short_secret_masks_fully()
    {
        var result = _factory.Build(new PspSecretInput(
            PspCode.Omise, Secrets(("secretKey", "ab")), MerchantId: null));

        Assert.Equal("**", result.Hints["secretKey"]);
    }
}
