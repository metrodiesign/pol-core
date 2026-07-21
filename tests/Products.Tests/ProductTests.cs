using Products.Domain;
using SharedKernel;

namespace Products.Tests;

/// <summary>
/// Pure domain tests for <see cref="Product.Create"/>'s insurance-plan validation (insurance-pivot REQ-1.3,
/// REQ-1.5) — no DB. Mirrors the strictness `Name`/`Price` already had before this pivot.
/// </summary>
public sealed class ProductTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime At = new(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct(
        decimal price = 500m, decimal sumInsured = 1_000_000m, string currency = "THB",
        string sumInsuredCurrency = "THB", int coverageDurationDays = 365, string insurer = "Muang Thai Insurance") =>
        Product.Create(
            MerchantId, "Travel Plan A", Money.Of(price, currency), Money.Of(sumInsured, sumInsuredCurrency),
            coverageDurationDays, insurer, At);

    [Fact]
    public void Create_with_valid_insurance_fields_succeeds()
    {
        var product = NewProduct();

        Assert.Equal(Money.Of(1_000_000m, "THB"), product.SumInsured);
        Assert.Equal(365, product.CoverageDurationDays);
        Assert.Equal("Muang Thai Insurance", product.Insurer);
    }

    [Fact]
    public void Create_rejects_zero_SumInsured() =>
        Assert.Throws<ArgumentException>(() => NewProduct(sumInsured: 0m));

    [Fact]
    public void Create_rejects_a_SumInsured_currency_that_does_not_match_Price() =>
        Assert.Throws<ArgumentException>(() => NewProduct(currency: "THB", sumInsuredCurrency: "USD"));

    [Fact]
    public void Create_rejects_zero_CoverageDurationDays() =>
        Assert.Throws<ArgumentException>(() => NewProduct(coverageDurationDays: 0));

    [Fact]
    public void Create_rejects_a_negative_CoverageDurationDays() =>
        Assert.Throws<ArgumentException>(() => NewProduct(coverageDurationDays: -1));

    [Fact]
    public void Create_rejects_a_blank_Insurer() =>
        Assert.Throws<ArgumentException>(() => NewProduct(insurer: "   "));

    [Fact]
    public void Create_trims_the_Insurer_name() =>
        Assert.Equal("Muang Thai Insurance", NewProduct(insurer: "  Muang Thai Insurance  ").Insurer);
}
