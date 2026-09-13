using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Persistence.MerchantUsers.Outbox;
using Persistence.Provisioning;

namespace Architecture.Tests;

public sealed class NativeJsonPayloadPrivacyTests
{
    [Fact]
    public void Public_registration_event_payload_contains_no_form_PII_or_KYC_key()
    {
        var userId = Guid.NewGuid();
        var serialized = MerchantUserOutboxEventRegistry.Serialize(
            new MerchantUserRegistrationSubmitted(userId, DateTime.UnixEpoch));

        Assert.Contains(userId.ToString(), serialized.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", serialized.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", serialized.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("displayName", serialized.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kyc", serialized.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", serialized.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merchant_user_outbox_rejects_unregistered_event_types_and_unknown_payload_fields()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MerchantUserOutboxEventRegistry.Serialize(new UnregisteredNotification()));

        Assert.Throws<JsonException>(() => MerchantUserOutboxEventRegistry.Deserialize(
            nameof(MerchantUserRegistrationSubmitted),
            $$"""{"userId":"{{Guid.NewGuid()}}","occurredAt":"2026-08-07T00:00:00Z","secret":"x"}"""));
    }

    [Fact]
    public void Provisioning_ledger_payload_omits_masked_secret_hints_and_reconstructs_them_from_hash_matched_request()
    {
        var merchantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var request = Spec(new Dictionary<string, string> { ["secretKey"] = "****1234" });
        var result = new ProvisioningWriteResult(merchantId,
            [new ProvisionedConnectionWrite(connectionId, "2c2p", request.Connections[0].MaskedSecretHints)]);

        var json = ProvisioningLedgerResultCodec.Serialize(result);
        var replay = ProvisioningLedgerResultCodec.Deserialize(json, request);

        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234", json, StringComparison.Ordinal);
        Assert.Equal("****1234", replay.Connections[0].MaskedSecrets["secretKey"]);
    }

    [Fact]
    public void Provisioning_ledger_rejects_unknown_or_secret_shaped_fields()
    {
        var json = $$"""
            {
              "merchantId": "{{Guid.NewGuid()}}",
              "status": "succeeded",
              "connections": [],
              "secret": "must-not-bind"
            }
            """;

        Assert.Throws<JsonException>(() => ProvisioningLedgerResultCodec.Deserialize(
            json, Spec(new Dictionary<string, string>())));
    }

    private static ProvisionSpec Spec(IReadOnlyDictionary<string, string> hints) => new(
        "vcommerce", "vCommerce", null, "TH", "THB", [], "{}", "admin", "corr",
        [new ProvisionConnectionSpec("2c2p", "card", null, "psp/2c2p", "{}", hints)]);

    private sealed record UnregisteredNotification : INotification;
}
