using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Merchants.Domain;
using Orders.Application;
using Orders.Domain;
using Persistence.ControlPlane;
using Products.Application;
using Products.Application.Ports;
using Products.Domain;
using SharedKernel;
using ProductPaymentStatus = Products.Domain.PaymentStatus;

namespace Products.Infrastructure.Orders;

/// <summary>
/// Production Order source policy. It deliberately uses the same upstream document boundary as the Cart path,
/// but resolves the Sale, Merchant currency and document ownership from server state before producing money.
/// Client product names, codes, prices and currency are never used as source data.
/// </summary>
internal sealed class TrustedOrderPricingSource(
    ControlPlaneDbContext controlPlane,
    ISpDocumentGateway documents,
    IDocumentSaleProbe documentSales)
    : ITrustedOrderPricingSource, IOrderSourcePolicy
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId,
        string businessType,
        IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException(
            "Trusted Order pricing requires a resolved source owner.",
            new InvalidOperationException("The contextual order source policy was not invoked."));

    public async Task<TrustedOrderPricing> PriceAsync(
        TrustedOrderSourceContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.BusinessType.Trim(), "insurance", StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                "The requested business type has no trusted Order source policy.", "unsupported_business_type");
        if (context.Owner.OwnerSaleId is not { } saleId)
            throw new ConflictException(
                "An active Sale owner is required for the insurance Order source.", "owner_required");

        var merchant = await PlatformReadGuard.ReadAsync(ct => controlPlane.Merchants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == context.MerchantId && x.Status == MerchantStatus.Active, ct),
            cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException("The Merchant is not active.", "merchant_unavailable");
        if (!string.Equals(merchant.Currency, "THB", StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                "The insurance source does not support the Merchant currency.", "source_currency_unsupported");

        var sale = await PlatformReadGuard.ReadAsync(ct => controlPlane.Sales.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == saleId
                && x.MerchantId == context.MerchantId
                && x.Status == SaleStatus.Active, ct), cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException("The Sale owner is not active for this Merchant.", "owner_required");

        if (context.RequestedItems.Count == 0)
            throw new InvalidRequestException("At least one order item is required.", "items_required");

        var lines = new List<TrustedOrderLineInput>(context.RequestedItems.Count);
        foreach (var requested in context.RequestedItems)
        {
            if (requested.Quantity != 1)
                throw new ConflictException(
                    "Insurance documents can only be purchased with quantity one.", "source_quantity_invalid");
            var documentNo = requested.ProductReference.Trim();
            if (documentNo.Length == 0)
                throw new ConflictException("A trusted insurance document is required.", "product_unavailable");

            var document = await ResolveDocumentAsync(documentNo, sale.Code, context.MerchantId, cancellationToken)
                .ConfigureAwait(false);
            var metadata = new CommerceItemMetadata(
                CommerceItemMetadataCodec.InsuranceDocumentSource,
                document.DocumentType.ToString(),
                document.PolicyNumber,
                document.StartDate is { } start ? DateOnly.FromDateTime(start) : null,
                document.EndDate is { } end ? DateOnly.FromDateTime(end) : null);
            var unitPrice = Money.Of(document.TotalPremium, merchant.Currency);
            lines.Add(new TrustedOrderLineInput(
                document.DocumentNo,
                document.ProductGroup.ToString(),
                string.IsNullOrWhiteSpace(document.ShowName)
                    ? document.ProductGroup.ToString()
                    : document.ShowName,
                1,
                unitPrice,
                Money.Zero(merchant.Currency),
                Money.Zero(merchant.Currency),
                unitPrice,
                $"sp:{document.InsuranceType}",
                metadata));
        }

        return new TrustedOrderPricing(
            merchant.Currency,
            lines,
            Money.Zero(merchant.Currency),
            Money.Zero(merchant.Currency));
    }

    private async Task<DocumentView> ResolveDocumentAsync(
        string documentNo,
        string saleCode,
        Guid merchantId,
        CancellationToken cancellationToken)
    {
        var matches = new List<DocumentView>(2);
        var paid = false;
        foreach (var group in new[] { ProductGroup.CMI, ProductGroup.FIRE })
        {
            var raw = await documents.LookupAsync(
                new SpDocumentLookupRequest(documentNo, group, saleCode), cancellationToken).ConfigureAwait(false);
            if (raw is null)
                continue;
            if (raw.PaymentStatus is not null
                && string.Equals(raw.PaymentStatus.Trim(), nameof(ProductPaymentStatus.PAID), StringComparison.Ordinal))
                paid = true;
            var view = SpDocumentItemMapper.ToView(raw);
            if (view is null)
                continue;
            if (!string.Equals(view.SaleCode, saleCode, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException("The source document is bound to another Sale.", "source_owner_mismatch");
            matches.Add(view);
        }

        if (paid)
            throw new ConflictException("The source document is already paid.", "product_unpayable");
        if (matches.Count > 1)
            throw new ConflictException("The source document matched more than one catalogue route.", "source_ambiguous");
        if (matches.Count == 0)
            throw new ConflictException("The source document is not available for sale.", "product_unavailable");

        var document = matches[0];
        var held = await documentSales.ProbeAsync(
            [new DocumentKey(document.DocumentNo, document.ProductGroup.ToString())], cancellationToken)
            .ConfigureAwait(false);
        if (held.Count > 0)
            throw new ConflictException("The source document is already held by an Order.", "product_unpayable");
        return document;
    }
}
