using SharedKernel;
using Orders.Domain;

namespace Orders.Domain.Items;

/// <summary>
/// Generic, purchase-time product/variant snapshot owned by one <see cref="Order"/>. Price and metadata are
/// server-owned. No insured/customer PII is accepted or persisted on a line.
/// </summary>
public sealed class Item : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Order"/> at construction (mirrors
    /// <c>Carts.Domain.Items.Item.MerchantId</c>) — enforced against drift by a composite FK.</summary>
    public Guid MerchantId { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Premium per unit, from <c>Cart.Item</c> at checkout-start (server, never client).</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>Discount on this line, in the line's own currency, carried from the checkout snapshot
    /// (purchase-flow-completion REQ-7.2). Zero when none was given — never null.</summary>
    public Money Discount { get; private set; }

    /// <summary>Trusted tax component captured with the line. It is included in LineAmount once.</summary>
    public Money TaxAmount { get; private set; }

    /// <summary>Server-derived line amount frozen on the Order.</summary>
    public Money LineAmount { get; private set; }

    public string ProductCode { get; private set; } = default!;
    public string VariantCode { get; private set; } = default!;
    public string? VariantName { get; private set; }

    /// <summary>Canonical server-owned business facts. Never accepts arbitrary client JSON.</summary>
    public string? Metadata { get; private set; }

    /// <summary>Canonical client metadata envelope. Kept separate from trusted product facts in
    /// <see cref="Metadata"/>.</summary>
    public string? RequestMetadata { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(
        Guid id, Guid orderId, Guid merchantId, int quantity, Money unitPrice, Money discount,
        string productCode, string variantCode, string? variantName, CommerceItemMetadata? metadata,
        VersionedMetadata? requestMetadata = null)
        : this(id, orderId, merchantId, quantity, unitPrice, discount,
            Money.Zero(unitPrice.Currency),
            LineAmounts.Net(LineAmounts.Gross(unitPrice, quantity), discount),
            productCode, variantCode, variantName, metadata, requestMetadata)
    {
    }

    internal Item(
        Guid id, Guid orderId, Guid merchantId, int quantity, Money unitPrice, Money discount,
        Money taxAmount, Money lineAmount, string productCode, string variantCode, string? variantName,
        CommerceItemMetadata? metadata, VersionedMetadata? requestMetadata = null)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode, nameof(productCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(variantCode, nameof(variantCode));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        if (productCode.Trim().Length > 150)
            throw new ArgumentException("Product code must be at most 150 characters.", nameof(productCode));
        if (variantCode.Trim().Length > 64)
            throw new ArgumentException("Variant code must be at most 64 characters.", nameof(variantCode));
        if (variantName?.Trim().Length > 128)
            throw new ArgumentException("Variant name must be at most 128 characters.", nameof(variantName));
        if (!unitPrice.SameCurrencyAs(discount) || !unitPrice.SameCurrencyAs(taxAmount)
            || !unitPrice.SameCurrencyAs(lineAmount))
            throw new ArgumentException("Item money values must use one currency.");

        OrderId = orderId;
        MerchantId = merchantId;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Discount = discount;
        TaxAmount = taxAmount;
        LineAmount = lineAmount;
        ProductCode = productCode.Trim();
        VariantCode = variantCode.Trim();
        VariantName = string.IsNullOrWhiteSpace(variantName) ? null : variantName.Trim();
        Metadata = metadata is null ? null : CommerceItemMetadataCodec.Serialize(metadata);
        RequestMetadata = requestMetadata?.ToCanonicalJson();
    }

    internal void Reparent(Guid orderId)
    {
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        OrderId = orderId;
    }

    /// <summary>Updates trusted price facts in place while preserving this item's identity.</summary>
    internal void ApplyTrustedSnapshot(Guid orderId, Guid merchantId, TrustedOrderLineInput line)
    {
        if (OrderId != orderId || MerchantId != merchantId)
            throw new InvalidOperationException("Order item ownership is immutable.");
        if (!string.Equals(ProductCode, line.ProductCode, StringComparison.Ordinal)
            || !string.Equals(VariantCode, line.VariantCode, StringComparison.Ordinal))
            throw new InvalidOperationException("An omitted order item cannot change product identity.");
        if (line.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(line), "Quantity must be positive.");
        if (!line.UnitPrice.SameCurrencyAs(line.DiscountAmount)
            || !line.UnitPrice.SameCurrencyAs(line.TaxAmount)
            || !line.UnitPrice.SameCurrencyAs(line.LineAmount))
            throw new ArgumentException("Item money values must use one currency.", nameof(line));

        Quantity = line.Quantity;
        UnitPrice = line.UnitPrice;
        Discount = line.DiscountAmount;
        TaxAmount = line.TaxAmount;
        LineAmount = line.LineAmount;
        Metadata = line.Metadata is null ? null : CommerceItemMetadataCodec.Serialize(line.Metadata);
        RequestMetadata = line.RequestMetadata?.ToCanonicalJson();
    }
}
