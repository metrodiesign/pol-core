using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

[Trait("Capability", "CheckoutTransactions")]
public sealed class TransactionDomainTests
{
    private static readonly Guid MerchantId = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrderId = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");
    private static readonly DateTime Now = new(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-8.1")]
    public void Creation_pins_order_money_provider_and_immutable_snapshot_before_provider_io()
    {
        var providerAccount = Guid.NewGuid();
        var credential = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var snapshot = "{\"schemaVersion\":1,\"provenance\":\"CAPTURED_AT_CONFIRM\"}";

        var transaction = Transaction.Create(
            transactionId, MerchantId, OrderId, "TXN-1", 1, Money.Of(125.50m, "THB"), "card",
            Code.TwoCTwoP, providerAccount, PspEnvironment.Sandbox, credential, 17,
            transactionId.ToString("N"), snapshot, Now);

        Assert.Equal(transactionId, transaction.Id);
        Assert.Equal(OrderId, transaction.OrderId);
        Assert.Equal(Money.Of(125.50m, "THB"), transaction.Amount);
        Assert.Equal(providerAccount, transaction.ProviderAccountId);
        Assert.Equal(credential, transaction.CredentialVersionId);
        Assert.Equal(17, transaction.ConfigurationVersion);
        Assert.Equal(transactionId.ToString("N"), transaction.ProviderRequestReference);
        Assert.Equal(snapshot, transaction.OrderSnapshot);
        Assert.Equal(TransactionStatus.Created, transaction.Status);
        Assert.True(transaction.IsPotentiallyChargeable);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.2")]
    [Trait("Requirement", "REQ-8.7")]
    [Trait("Requirement", "REQ-8.8")]
    public void Success_is_absorbing_and_late_review_does_not_downgrade_financial_state()
    {
        var transaction = NewTransaction();
        Assert.True(transaction.MarkSucceeded("charge-1", "paid", Now));

        transaction.MarkPendingConfirmation(Now.AddMinutes(1), Now.AddMinutes(2), "late-pending");
        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.Equal("charge-1", transaction.ProviderReference);

        Assert.False(transaction.MarkSucceeded(
            "charge-1", "duplicate-paid", Now.AddMinutes(3), needsReview: true, reviewCode: "duplicate"));
        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.True(transaction.NeedsReview);
        Assert.Equal("duplicate", transaction.ReviewCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.9")]
    public void Distinct_attempts_can_both_be_successful_without_a_unique_success_constraint()
    {
        var first = NewTransaction(attempt: 1);
        var second = NewTransaction(attempt: 2);

        Assert.True(first.MarkSucceeded("charge-1", "paid", Now));
        Assert.True(second.MarkSucceeded("charge-2", "paid", Now.AddMinutes(1), needsReview: true,
            reviewCode: "duplicate_success"));
        Assert.Equal(TransactionStatus.Succeeded, first.Status);
        Assert.Equal(TransactionStatus.Succeeded, second.Status);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ProviderReference, second.ProviderReference);
        Assert.True(second.NeedsReview);
    }

    private static Transaction NewTransaction(int attempt = 1) => Transaction.Create(
        Guid.NewGuid(), MerchantId, OrderId, $"TXN-{attempt}", attempt, Money.Of(125.50m, "THB"), "card",
        Code.TwoCTwoP, Guid.NewGuid(), PspEnvironment.Sandbox, Guid.NewGuid(), 17,
        Guid.NewGuid().ToString("N"), "{\"schemaVersion\":1}", Now);
}
