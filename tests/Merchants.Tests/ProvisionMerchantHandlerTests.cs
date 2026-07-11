using BuildingBlocks.Application;
using Payments.Infrastructure.Psp;
using Merchants.Application.ProvisionMerchant;

namespace Merchants.Tests;

public sealed class ProvisionMerchantHandlerTests
{
    private static ProvisionMerchantCommand ValidCommand(string code = "vcommerce") => new(
        new MerchantSpec(code, "vCommerce Co., Ltd.", "0105560000000", "TH", "THB", ["card", "promptpay"], null),
        [
            new PspConnectionSpec("2c2p", ["card"], "merchant-1",
                new Dictionary<string, string> { ["secretKey"] = "sk2c2pAAAA1234" }, null),
            new PspConnectionSpec("omise", ["card"], null,
                new Dictionary<string, string> { ["secretKey"] = "skey_test_BBBB5678" }, null),
        ],
        "ops-007", "corr-1");

    private static (ProvisionMerchantHandler Handler, FakeMerchantRepository Merchants, FakePspConnectionRepository Psp,
        FakeVault Vault, FakeAuditWriter Audit, FakeUnitOfWork Uow) NewHandler(int retries = 0)
    {
        var merchants = new FakeMerchantRepository();
        var psp = new FakePspConnectionRepository();
        var vault = new FakeVault();
        var audit = new FakeAuditWriter();
        var uow = new FakeUnitOfWork { RetriesToSimulate = retries };
        var handler = new ProvisionMerchantHandler(merchants, psp, vault, audit, new PspSecretEnvelopeFactory(), uow, new FixedClock());
        return (handler, merchants, psp, vault, audit, uow);
    }

    [Fact]
    public async Task Valid_provision_writes_merchant_connections_vault_and_audit()
    {
        var (handler, merchants, psp, vault, audit, _) = NewHandler();

        var result = await handler.Handle(ValidCommand("VCommerce"), default);

        Assert.NotEqual(Guid.Empty, result.MerchantId);
        Assert.Equal(2, result.Connections.Count);
        var m = Assert.Single(merchants.Added);
        Assert.Equal("vcommerce", m.Code);               // normalized
        Assert.Equal(2, psp.Added.Count);
        Assert.Equal(2, vault.Stored.Count);
        Assert.Contains(vault.Stored, s => s.Name == "psp/2c2p");
        Assert.Contains(vault.Stored, s => s.Name == "psp/omise");
        var auditRow = Assert.Single(audit.Appended);
        Assert.Equal("ops-007", auditRow.AdminSubject);
        Assert.Equal("corr-1", auditRow.CorrelationId);
        Assert.Equal(m.Id, auditRow.MerchantId);

        var twoC2P = result.Connections.Single(c => c.Psp == "2c2p");
        Assert.Equal("****1234", twoC2P.MaskedSecrets["secretKey"]);

        // merchantId (non-secret, the PSP's OWN merchant id) is persisted on the readable connection metadata,
        // not only in the vault envelope, so the masked read-back can surface it (Codex re-review P2 / REQ-9.1).
        Assert.Contains(psp.Added, c => c.Metadata is not null && c.Metadata.Contains("merchant-1"));
    }

    [Fact]
    public async Task Rejects_code_outside_allowlist_without_writing()
    {
        var (handler, merchants, psp, vault, _, _) = NewHandler();

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(ValidCommand("evilcorp"), default));

        Assert.Empty(merchants.Added);
        Assert.Empty(psp.Added);
        Assert.Empty(vault.Stored);
    }

    [Fact]
    public async Task Rejects_empty_psp_connections()
    {
        var (handler, _, _, _, _, _) = NewHandler();
        var command = ValidCommand() with { PspConnections = [] };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Rejects_duplicate_psp_in_submission()
    {
        var (handler, _, _, _, _, _) = NewHandler();
        var omise = new PspConnectionSpec("omise", ["card"], null,
            new Dictionary<string, string> { ["secretKey"] = "skey_test_X1234" }, null);
        var command = ValidCommand() with { PspConnections = [omise, omise] };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Rejects_connection_missing_required_secret()
    {
        var (handler, _, _, _, _, _) = NewHandler();
        var command = ValidCommand() with
        {
            PspConnections = [new PspConnectionSpec("omise", ["card"], null, new Dictionary<string, string>(), null)],
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.Handle(command, default));
    }

    [Fact]
    public async Task Duplicate_code_throws_conflict()
    {
        var (handler, merchants, _, _, _, _) = NewHandler();
        merchants.Exists = true;

        await Assert.ThrowsAsync<ConflictException>(async () => await handler.Handle(ValidCommand(), default));
    }

    [Fact]
    public async Task Is_idempotent_under_transaction_retry()
    {
        var (handler, _, _, _, _, uow) = NewHandler(retries: 1);

        var result = await handler.Handle(ValidCommand(), default);

        Assert.Equal(2, uow.Runs);                 // delegate ran twice
        Assert.Equal(2, result.Connections.Count); // result not doubled — built fresh each attempt
    }

    [Fact]
    public async Task Masked_secrets_never_expose_plaintext()
    {
        var (handler, _, _, _, _, _) = NewHandler();

        var result = await handler.Handle(ValidCommand(), default);

        var masked = result.Connections.SelectMany(c => c.MaskedSecrets.Values).ToList();
        Assert.All(masked, v => Assert.StartsWith("****", v));
        Assert.DoesNotContain(masked, v => v.Contains("AAAA") || v.Contains("BBBB"));
    }
}
