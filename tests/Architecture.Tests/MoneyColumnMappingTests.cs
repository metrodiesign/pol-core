using BuildingBlocks.Infrastructure.Persistence;
using Carts.Domain;
using Carts.Domain.Items;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using CheckoutSession = Checkouts.Domain.Session;
using PaymentSession = Payments.Domain.Session;

namespace Architecture.Tests;

/// <summary>
/// Model assertion (rf1-schema-reset task 2, REQ-6.3): every entity's <c>Money</c> complex property
/// must map to a <c>{Prop}Amount decimal(19,4)</c> + <c>{Prop}Currency</c> (max length 3, fixed,
/// non-Unicode -> char(3)) column pair, per the EF Money mapping rule. Offline (Sqlite, no SQL Server)
/// — this asserts the CONFIGURED relational metadata, which the SqlServer provider then materialises
/// as decimal(19,4)/char(3) (proven by the generated InterimMoneyDecimal migration).
/// </summary>
public sealed class MoneyColumnMappingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PolDbContext _db;

    public MoneyColumnMappingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // EnableServiceProviderCaching(false): see EntitySchemaMappingTests — EF's model cache keys on the
        // context type, so two PolDbContext test classes with different ModuleAssemblies would otherwise
        // share whichever model got built first.
        var options = new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_connection)
            .EnableServiceProviderCaching(false).Options;
        var modules = new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
            typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
            typeof(Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly,
            typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
            typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        ]);
        _db = new PolDbContext(options, modules);
    }

    private void AssertMoneyColumns(Type entityType, string moneyPropertyName, string amountColumn, string currencyColumn)
    {
        var entity = _db.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"{entityType.Name} is not in the model.");
        var complex = entity.GetComplexProperties().SingleOrDefault(p => p.Name == moneyPropertyName)
            ?? throw new InvalidOperationException($"{entityType.Name}.{moneyPropertyName} is not mapped as a complex property.");

        var amount = complex.ComplexType.FindProperty(nameof(SharedKernel.Money.Amount))
            ?? throw new InvalidOperationException($"{entityType.Name}.{moneyPropertyName}.Amount not found.");
        Assert.Equal(amountColumn, amount.GetColumnName());
        Assert.Equal(19, amount.GetPrecision());
        Assert.Equal(4, amount.GetScale());

        var currency = complex.ComplexType.FindProperty(nameof(SharedKernel.Money.Currency))
            ?? throw new InvalidOperationException($"{entityType.Name}.{moneyPropertyName}.Currency not found.");
        Assert.Equal(currencyColumn, currency.GetColumnName());
        Assert.Equal(3, currency.GetMaxLength());
        Assert.True(currency.IsFixedLength());
        Assert.False(currency.IsUnicode()); // char(3), not nchar(3) — ISO 4217 is ASCII
    }

    // The Product aggregate and its shop.Products table were retired (products-external-source-of-truth REQ-6.1),
    // so the former Product_owns_no_Money_complex_property test was removed with them: the catalogue is read live
    // from the upstream now and no longer has an EF entity to assert against.

    [Fact]
    public void CartItem_UnitPrice_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(typeof(Item), nameof(Item.UnitPrice), "UnitPriceAmount", "UnitPriceCurrency");

    [Fact]
    public void CheckoutSession_Amount_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(typeof(CheckoutSession), nameof(CheckoutSession.Amount), "AmountAmount", "AmountCurrency");

    [Fact]
    public void Order_Amount_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(typeof(Order), nameof(Order.Amount), "AmountAmount", "AmountCurrency");

    [Fact]
    public void PaymentSession_Amount_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(typeof(PaymentSession), nameof(PaymentSession.Amount), "AmountAmount", "AmountCurrency");

    // purchase-flow-completion REQ-6.3/7.2 — the line discount is a Money everywhere, never a bare decimal
    // at a seam, so it gets the same column pair as UnitPrice on both item types.
    [Fact]
    public void CheckoutItem_Discount_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(
            typeof(Checkouts.Domain.Items.Item), nameof(Checkouts.Domain.Items.Item.Discount),
            "DiscountAmount", "DiscountCurrency");

    [Fact]
    public void OrderItem_Discount_maps_to_decimal_19_4_and_char3() =>
        AssertMoneyColumns(
            typeof(Orders.Domain.Items.Item), nameof(Orders.Domain.Items.Item.Discount),
            "DiscountAmount", "DiscountCurrency");

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
