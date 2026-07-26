using BuildingBlocks.Application;
using Payments.Infrastructure.Psp;
using Merchants.Application.ProvisionMerchant;

namespace Merchants.Tests;

public sealed class ProvisionMerchantHandlerTests
{
    private static readonly Guid CallerAdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const long ExpectedAuthorizationVersion = 3;

    private static ProvisionMerchantCommand ValidCommand(string code = "vcommerce") => new(
        new MerchantSpec(code, "vCommerce Co., Ltd.", "0105560000000", "TH", "THB", ["card", "promptpay"], null),
        [
            new PspConnectionSpec("2c2p", ["card"], "merchant-1",
                new Dictionary<string, string> { ["secretKey"] = "sk2c2pAAAA1234" }, null),
            new PspConnectionSpec("omise", ["card"], null,
                new Dictionary<string, string> { ["secretKey"] = "skey_test_BBBB5678" }, null),
        ],
        "ops-007", "corr-1", CallerAdminId, ExpectedAuthorizationVersion);

    private static (ProvisionMerchantHandler Handler, FakeMerchantRepository Merchants, FakeProvisioningWriter Writer) NewHandler()
    {
        var merchants = new FakeMerchantRepository();
        var writer = new FakeProvisioningWriter();
        var handler = new ProvisionMerchantHandler(merchants, writer, new PspSecretEnvelopeFactory(), new FixedClock());
        return (handler, merchants, writer);
    }

    [Fact]
    public async Task Valid_provision_calls_the_provisioning_writer_with_the_correct_spec()
    {
        var (handler, _, writer) = NewHandler();

        var result = await handler.Handle(ValidCommand("VCommerce"), default);

        Assert.NotEqual(Guid.Empty, result.MerchantId);
        Assert.Equal(2, result.Connections.Count);

        var call = Assert.Single(writer.Calls);
        Assert.Equal("vcommerce", call.Spec.MerchantCode); // normalized
        Assert.Equal(CallerAdminId, call.CallerAdminId);
        Assert.Equal(ExpectedAuthorizationVersion, call.ExpectedAuthorizationVersion);
        Assert.Equal("provision-merchant:vcommerce", call.OperationKey);
        Assert.Equal("ops-007", call.Spec.AdminSubject);
        Assert.Equal("corr-1", call.Spec.CorrelationId);
        Assert.Equal(2, call.Spec.Connections.Count);
        Assert.Contains(call.Spec.Connections, c => c.SecretName == "psp/2c2p");
        Assert.Contains(call.Spec.Connections, c => c.SecretName == "psp/omise");

        var twoC2P = result.Connections.Single(c => c.Psp == "2c2p");
        Assert.Equal("****1234", twoC2P.MaskedSecrets["secretKey"]);

        // merchantId (non-secret, the PSP's OWN merchant id) is persisted on the readable connection metadata,
        // not only in the vault envelope, so the masked read-back can surface it (Codex re-review P2 / REQ-9.1).
        Assert.Contains(call.Spec.Connections, c => c.ConnectionMetadataJson is not null && c.ConnectionMetadataJson.Contains("merchant-1"));
    }

    [Fact]
    public async Task Rejects_code_outside_allowlist_without_writing()
    {
        var (handler, _, writer) = NewHandler();

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(ValidCommand("evilcorp"), default));

        Assert.Empty(writer.Calls);
    }

    // REQ-3.7: EnabledMethods is stored as a csv that Connection.Supports later compares ORDINALLY, so a
    // value the vocabulary does not know must be refused at provisioning time rather than accepted and then
    // silently refusing every payment of that merchant. A KNOWN method in the wrong case is a different
    // case: it is normalized, not rejected — see Stores_enabled_methods_as_canonical_codes.
    [Theory]
    [InlineData("CC")]          // the PSP's own channel code, not our vocabulary
    [InlineData("paypal")]      // a method this platform does not offer
    [InlineData("")]            // blank entry in the list
    [InlineData("   ")]
    public async Task Rejects_an_enabled_method_outside_the_canonical_vocabulary(string method)
    {
        var (handler, _, writer) = NewHandler();
        var command = ValidCommand() with
        {
            PspConnections = [new PspConnectionSpec("2c2p", [method], "merchant-1",
                new Dictionary<string, string> { ["secretKey"] = "sk2c2pAAAA1234" }, null)],
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));

        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task Stores_enabled_methods_as_canonical_codes()
    {
        var (handler, _, writer) = NewHandler();
        var command = ValidCommand() with
        {
            PspConnections = [new PspConnectionSpec("2c2p", [" CARD ", "PromptPay", "installment"], "merchant-1",
                new Dictionary<string, string> { ["secretKey"] = "sk2c2pAAAA1234" }, null)],
        };

        await handler.Handle(command, default);

        var connection = Assert.Single(Assert.Single(writer.Calls).Spec.Connections);
        Assert.Equal("card,promptpay,installment", connection.EnabledMethods);
    }

    [Fact]
    public async Task Rejects_empty_psp_connections()
    {
        var (handler, _, _) = NewHandler();
        var command = ValidCommand() with { PspConnections = [] };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Rejects_duplicate_psp_in_submission()
    {
        var (handler, _, _) = NewHandler();
        var omise = new PspConnectionSpec("omise", ["card"], null,
            new Dictionary<string, string> { ["secretKey"] = "skey_test_X1234" }, null);
        var command = ValidCommand() with { PspConnections = [omise, omise] };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Rejects_connection_missing_required_secret()
    {
        var (handler, _, _) = NewHandler();
        var command = ValidCommand() with
        {
            PspConnections = [new PspConnectionSpec("omise", ["card"], null, new Dictionary<string, string>(), null)],
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Duplicate_code_throws_conflict()
    {
        var (handler, merchants, _) = NewHandler();
        merchants.Exists = true;

        await Assert.ThrowsAsync<ConflictException>(async () => await handler.Handle(ValidCommand(), default));
    }

    [Fact]
    public async Task Same_code_derives_the_same_operation_key_for_idempotent_replay()
    {
        var (handler, _, writer) = NewHandler();

        // Two dispatches for the SAME merchant code (e.g. a client retry after a lost response) must derive
        // the identical operationKey — the coordinator's own ledger (ProvisioningCoordinatorTests) is what
        // turns that into an actual idempotent replay end-to-end; this test isolates the derivation itself.
        await handler.Handle(ValidCommand(), default);
        await handler.Handle(ValidCommand(), default);

        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal(writer.Calls[0].OperationKey, writer.Calls[1].OperationKey);
        Assert.Equal("provision-merchant:vcommerce", writer.Calls[0].OperationKey);
    }

    [Fact]
    public async Task Masked_secrets_never_expose_plaintext()
    {
        var (handler, _, _) = NewHandler();

        var result = await handler.Handle(ValidCommand(), default);

        var masked = result.Connections.SelectMany(c => c.MaskedSecrets.Values).ToList();
        Assert.All(masked, v => Assert.StartsWith("****", v));
        Assert.DoesNotContain(masked, v => v.Contains("AAAA") || v.Contains("BBBB"));
    }
}
