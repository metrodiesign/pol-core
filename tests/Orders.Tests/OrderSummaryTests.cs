using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Pure domain tests for the order summary link (REQ-2): a fresh order issues a token with a
/// future expiry; expiry is decided against that timestamp; a resend rotates the token and extends the TTL,
/// and is rejected once the order leaves AwaitingPayment.</summary>
public sealed class OrderSummaryTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    private static Order NewOrder() =>
        Order.Create(MerchantId, Money.Of(15000, "THB"), At,
            OrderLineInputs.OneLine(Money.Of(15000, "THB")), orderNo: "ORD6900000001",
            paymentChannel: "card");

    [Fact]
    public void Create_issues_a_token_expiring_after_the_ttl()
    {
        var order = NewOrder();

        Assert.False(string.IsNullOrWhiteSpace(order.SummaryToken));
        Assert.Equal(At + Order.SummaryTokenTtl, order.SummaryTokenExpiresAt);
        Assert.False(order.IsSummaryExpired(At));
        Assert.True(order.IsSummaryExpired(order.SummaryTokenExpiresAt));
    }

    [Fact]
    public void ReissueSummary_rotates_the_token_and_extends_the_expiry()
    {
        var order = NewOrder();
        var oldToken = order.SummaryToken;
        var later = At.AddHours(1);

        order.ReissueSummary(later);

        Assert.NotEqual(oldToken, order.SummaryToken);
        Assert.Equal(later + Order.SummaryTokenTtl, order.SummaryTokenExpiresAt);
    }

    [Fact]
    public void ReissueSummary_is_rejected_once_the_order_is_no_longer_awaiting_payment()
    {
        var order = NewOrder();
        order.Cancel();

        Assert.Throws<InvalidOperationException>(() => order.ReissueSummary(At));
    }

    [Theory]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Expired)]
    public void ReissueSummary_is_allowed_for_retryable_payment_status(OrderStatus status)
    {
        var order = NewOrder();
        var sessionId = Guid.NewGuid();
        order.AttachPaymentAttempt(sessionId);
        if (status == OrderStatus.Failed)
            order.MarkPaymentFailed(sessionId);
        else
            order.MarkPaymentExpired(sessionId);
        var previous = order.SummaryToken;

        order.ReissueSummary(At.AddHours(1));

        Assert.NotEqual(previous, order.SummaryToken);
    }
}
