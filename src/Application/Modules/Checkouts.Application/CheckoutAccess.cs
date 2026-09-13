using BuildingBlocks.Application;
using Checkouts.Domain;
using Mediator;
using Orders.Domain;
using SharedKernel;

namespace Checkouts.Application;

public sealed record ExchangeCheckoutAccessCommand(string Token)
    : ICommand<CheckoutAccessResult>;

public sealed record CheckoutAccessResult(
    Guid OrderId,
    long OrderVersion,
    string Proof,
    string CsrfToken,
    DateTime ExpiresAt);

public sealed record GetCheckoutSummaryQuery(string Proof)
    : IQuery<CheckoutSummaryView>;

public sealed record CheckoutSummaryLineView(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money LineAmount);

public sealed record CheckoutSummaryView(
    Guid OrderId,
    string OrderNo,
    string? MerchantName,
    OrderStatus OrderStatus,
    PaymentStatus PaymentStatus,
    Money TotalAmount,
    DateTime ExpiresAt,
    IReadOnlyList<CheckoutSummaryLineView> Lines);

public sealed class ExchangeCheckoutAccessHandler(
    IPaymentLinkTokenService tokens,
    ICustomerCheckoutReader reader,
    ICheckoutCapabilityService capabilities,
    IClock clock)
    : ICommandHandler<ExchangeCheckoutAccessCommand, CheckoutAccessResult>
{
    public async ValueTask<CheckoutAccessResult> Handle(
        ExchangeCheckoutAccessCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > 512)
            throw new NotFoundException("Payment link was not found.");
        var snapshot = await reader.FindByHashAsync(
            tokens.Hash(command.Token), cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.LinkStatus != PaymentLinkStatus.Active)
            throw new NotFoundException("Payment link was not found.");
        if (clock.UtcNow >= snapshot.LinkExpiresAt)
            throw new GoneException("Payment link has expired.");
        if (snapshot.OrderStatus == OrderStatus.Cancelled)
            throw new NotFoundException("Payment link was not found.");
        var capability = capabilities.Issue(
            snapshot.OrderId,
            snapshot.LinkId,
            snapshot.OrderVersion,
            snapshot.LinkExpiresAt,
            clock.UtcNow);
        return new CheckoutAccessResult(
            capability.OrderId,
            capability.OrderVersion,
            capability.Proof,
            capability.CsrfToken,
            capability.ExpiresAt);
    }
}

public sealed class GetCheckoutSummaryHandler(
    ICustomerCheckoutReader reader,
    ICheckoutCapabilityService capabilities,
    IClock clock)
    : IQueryHandler<GetCheckoutSummaryQuery, CheckoutSummaryView>
{
    public async ValueTask<CheckoutSummaryView> Handle(
        GetCheckoutSummaryQuery query,
        CancellationToken cancellationToken)
    {
        if (!capabilities.TryRead(query.Proof, out var capability))
            throw new AccessDeniedException("Checkout capability is invalid.", "checkout_capability_invalid");
        var snapshot = await reader.GetSummaryAsync(
            capability.OrderId, capability.LinkId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Checkout order was not found.");
        if (snapshot.LinkStatus != PaymentLinkStatus.Active)
            throw new AccessDeniedException("Checkout capability is no longer active.", "checkout_link_revoked");
        if (clock.UtcNow >= snapshot.LinkExpiresAt)
            throw new GoneException("Payment link has expired.");
        if (snapshot.OrderVersion != capability.OrderVersion)
            throw new ConflictException("Checkout context is stale.", "checkout_context_stale");
        return new CheckoutSummaryView(
            snapshot.OrderId,
            snapshot.OrderNo,
            snapshot.MerchantName,
            snapshot.OrderStatus,
            snapshot.PaymentStatus,
            snapshot.TotalAmount,
            snapshot.LinkExpiresAt,
            snapshot.Lines.Select(line => new CheckoutSummaryLineView(
                line.ProductCode,
                line.VariantCode,
                line.VariantName,
                line.Quantity,
                line.LineAmount)).ToList());
    }
}
