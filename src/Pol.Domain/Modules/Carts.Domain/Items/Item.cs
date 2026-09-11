using SharedKernel;

namespace Carts.Domain.Items;

/// <summary>
/// A line in a <see cref="Domain.Cart"/>. Owned by its cart aggregate — never loaded or mutated on its own.
/// The unit price is mapped as an EF complex type (rf1 — decimal(19,4) + char(3) columns), per the
/// EF money mapping rule.
/// <para><see cref="Entity{T}.Id"/> is minted by <see cref="Domain.Cart.AddItem"/> and is the line's public
/// handle: the remove/quantity routes address it, because a real <see cref="ProductCode"/> contains <c>/</c>
/// and Thai characters and cannot be a path segment (products-external-source-of-truth REQ-9.1).</para>
/// </summary>
public sealed class Item : Entity<Guid>
{
    public Guid CartId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Domain.Cart"/> at construction (rls-to-query-filter
    /// REQ-6) — Item has no navigation to Cart, so this is its own tenant key for the read floor. Enforced
    /// against drift by a composite FK <c>(CartId, MerchantId) → Cart(Id, MerchantId)</c>; only
    /// <see cref="Domain.Cart.AddItem"/> stamps it, so it can never diverge from the parent in practice.</summary>
    public Guid MerchantId { get; private set; }

    /// <summary>The insurance document this line buys — the identifier itself, no surrogate key any more
    /// (products-external-source-of-truth REQ-2.1/2.2). Stored as the upstream spelled it, trimmed (REQ-2.6).</summary>
    public string ProductCode { get; private set; } = default!;

    /// <summary>The sale code the upstream returned WITH the document, never the one a client sent
    /// (REQ-4.7) — checkout re-reads the document under this same code.</summary>
    public string SaleCode { get; private set; } = default!;

    /// <summary>Wire value of the document's <c>ProductGroup</c> as the upstream returned it (REQ-4.7);
    /// a plain string because Carts holds no reference to <c>Products.Domain</c>.</summary>
    public string VariantCode { get; private set; } = default!;

    public string? VariantName { get; private set; }

    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    /// <summary>Canonical server-owned business facts. Never accepts arbitrary client JSON.</summary>
    public string? Metadata { get; private set; }

    /// <summary>The line total: unit price times quantity. Not mapped (EF ignores it).</summary>
    public Money LineTotal => Money.Of(UnitPrice.Amount * Quantity, UnitPrice.Currency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(
        Guid id, Guid cartId, Guid merchantId,
        string productCode, string saleCode, string variantCode, string? variantName, int quantity, Money unitPrice,
        CommerceItemMetadata metadata)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode, nameof(productCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(saleCode, nameof(saleCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(variantCode, nameof(variantCode));
        ArgumentNullException.ThrowIfNull(metadata);
        if (productCode.Trim().Length > 150)
            throw new ArgumentException("Product code must be at most 150 characters.", nameof(productCode));
        if (saleCode.Trim().Length > 20)
            throw new ArgumentException("Sale code must be at most 20 characters.", nameof(saleCode));
        if (variantCode.Trim().Length > 64)
            throw new ArgumentException("Variant code must be at most 64 characters.", nameof(variantCode));
        if (variantName?.Trim().Length > 128)
            throw new ArgumentException("Variant name must be at most 128 characters.", nameof(variantName));

        CartId = cartId;
        MerchantId = merchantId;
        ProductCode = productCode.Trim();
        SaleCode = saleCode.Trim();
        VariantCode = variantCode.Trim();
        VariantName = string.IsNullOrWhiteSpace(variantName) ? null : variantName.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        Metadata = CommerceItemMetadataCodec.Serialize(metadata);
    }

    internal void SetQuantity(int quantity) => Quantity = quantity;
}
