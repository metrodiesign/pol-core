using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

public sealed class OrderItemsTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

    private static OrderItemInput Item(
        decimal unitPrice,
        int quantity = 1,
        string currency = "THB",
        string productCode = "DOC-1",
        string variantCode = "VMI",
        string? variantName = "ประกันรถยนต์",
        Money? discount = null,
        CommerceItemMetadata? metadata = null) =>
        new(quantity, Money.Of(unitPrice, currency), productCode, variantCode, variantName, discount, metadata);

    [Fact]
    public void Create_snapshots_generic_line_and_pending_status()
    {
        var metadata = new CommerceItemMetadata(
            CommerceItemMetadataCodec.InsuranceDocumentSource, "POLICY", "POL-1",
            new DateOnly(2026, 7, 1), new DateOnly(2027, 7, 1));

        var order = Order.Create(
            MerchantId, Money.Of(30000m, "THB"), At,
            [Item(15000m, quantity: 2, metadata: metadata)], "ORD6900000001", saleCode: "SALE-1");

        var item = Assert.Single(order.Items);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal("SALE-1", order.SaleCode);
        Assert.Equal("DOC-1", item.ProductCode);
        Assert.Equal("VMI", item.VariantCode);
        Assert.Equal("ประกันรถยนต์", item.VariantName);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(Money.Zero("THB"), item.Discount);
        Assert.Equal(metadata, CommerceItemMetadataCodec.Parse(item.Metadata!));
    }

    [Fact]
    public void Create_supports_multiple_lines_and_exact_total()
    {
        var order = Order.Create(
            MerchantId, Money.Of(25000m, "THB"), At,
            [Item(15000m, productCode: "DOC-A"), Item(10000m, productCode: "DOC-B")],
            "ORD6900000002");

        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void Create_rejects_empty_lines() =>
        Assert.Throws<ArgumentException>(() =>
            Order.Create(MerchantId, Money.Of(1m, "THB"), At, [], "ORD6900000003"));

    [Fact]
    public void Create_rejects_non_positive_quantity() =>
        Assert.Throws<ArgumentException>(() =>
            Order.Create(MerchantId, Money.Of(1m, "THB"), At, [Item(1m, quantity: 0)], "ORD6900000004"));

    [Fact]
    public void Create_rejects_total_mismatch() =>
        Assert.Throws<ArgumentException>(() =>
            Order.Create(MerchantId, Money.Of(10m, "THB"), At, [Item(9m)], "ORD6900000005"));

    [Fact]
    public void Create_rejects_currency_mismatch() =>
        Assert.Throws<ArgumentException>(() =>
            Order.Create(MerchantId, Money.Of(10m, "THB"), At, [Item(10m, currency: "USD")], "ORD6900000006"));

    [Theory]
    [InlineData("", "VMI")]
    [InlineData("   ", "VMI")]
    [InlineData("DOC-1", "")]
    [InlineData("DOC-1", "  ")]
    public void Create_rejects_blank_generic_keys(string productCode, string variantCode) =>
        Assert.Throws<ArgumentException>(() => Order.Create(
            MerchantId, Money.Of(10m, "THB"), At,
            [Item(10m, productCode: productCode, variantCode: variantCode)], "ORD6900000007"));

    [Fact]
    public void Generic_fields_are_trimmed()
    {
        var order = Order.Create(
            MerchantId, Money.Of(10m, "THB"), At,
            [Item(10m, productCode: " DOC-1 ", variantCode: " VMI ", variantName: " Name ")],
            "ORD6900000008");

        var item = Assert.Single(order.Items);
        Assert.Equal("DOC-1", item.ProductCode);
        Assert.Equal("VMI", item.VariantCode);
        Assert.Equal("Name", item.VariantName);
    }

    [Fact]
    public void Order_item_CLR_surface_has_no_insured_or_policy_specific_properties()
    {
        var names = typeof(Item).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(names, name => name.StartsWith("Insured", StringComparison.Ordinal));
        Assert.DoesNotContain("DocumentType", names);
        Assert.DoesNotContain("PolicyNumber", names);
        Assert.DoesNotContain("StartDate", names);
        Assert.DoesNotContain("EndDate", names);
    }
}
