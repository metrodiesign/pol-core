using BuildingBlocks.Infrastructure.Persistence;
using Checkouts.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Persistence.MerchantRuntime;

namespace Architecture.Tests;

[Trait("Capability", "OrdersLinks")]
public sealed class Task5OrderLinksArchitectureTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly PolDbContext _db;

    public Task5OrderLinksArchitectureTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;
        _db = new PolDbContext(options, new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(global::Admins.Infrastructure.AdminModuleRegistration),
            typeof(global::Iam.Infrastructure.IamModuleRegistration),
            typeof(global::Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(global::Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(global::Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(global::Access.Infrastructure.AccessModuleRegistration),
        ]));
    }

    [Fact]
    [Trait("Requirement", "REQ-6.12")]
    [Trait("Requirement", "REQ-7.1")]
    public void PaymentLink_and_replay_are_checkout_owned_and_directly_merchant_bound_to_Order()
    {
        var link = _db.Model.FindEntityType(typeof(PaymentLink))!;
        var replay = _db.Model.FindEntityType(typeof(PaymentLinkReplay))!;

        Assert.Equal(SchemaNames.Checkout, link.GetSchema());
        Assert.Equal("PaymentLinks", link.GetTableName());
        Assert.Equal(SchemaNames.Checkout, replay.GetSchema());
        Assert.Equal("PaymentLinkReplays", replay.GetTableName());
        Assert.Contains(link.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(Order)
            && fk.Properties.Select(p => p.Name).SequenceEqual(["OrderId", "MerchantId"]));
        Assert.Contains(replay.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(Order)
            && fk.Properties.Select(p => p.Name).SequenceEqual(["OrderId", "MerchantId"]));
    }

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.2")]
    [Trait("Requirement", "REQ-7.3")]
    public void Hash_and_unique_constraints_enforce_link_only_capabilities_and_bounded_replay()
    {
        var link = _db.Model.FindEntityType(typeof(PaymentLink))!;
        var replay = _db.Model.FindEntityType(typeof(PaymentLinkReplay))!;

        Assert.Equal("binary(32)", link.FindProperty(nameof(PaymentLink.TokenHash))!.GetColumnType());
        Assert.Contains(link.GetIndexes(), index =>
            index.IsUnique && index.GetFilter() == "[Status] = 1"
            && index.Properties.Select(p => p.Name).SequenceEqual(["OrderId", "Status"]));
        Assert.Contains(link.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(["TokenHash"]));
        Assert.Contains(replay.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(p => p.Name)
                .SequenceEqual(["MerchantId", "Operation", "IdempotencyKey"]));
        Assert.DoesNotContain(link.GetProperties(), p =>
            p.Name.Contains("RawToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replay.GetProperties(), p =>
            p.Name.Equals("RawToken", StringComparison.OrdinalIgnoreCase));
        Assert.True(replay.FindProperty(nameof(PaymentLinkReplay.ProtectedRawToken))!.IsUnicode());
    }

    [Fact]
    [Trait("Requirement", "REQ-6.12")]
    public void Order_keeps_payment_state_separate_and_legacy_summary_columns_nullable()
    {
        var order = _db.Model.FindEntityType(typeof(Order))!;

        Assert.NotNull(order.FindProperty(nameof(Order.PaymentStatus)));
        Assert.NotNull(order.FindProperty(nameof(Order.CreatedByAccountId)));
        Assert.NotNull(order.FindProperty(nameof(Order.OwnerSaleId)));
        Assert.NotNull(order.FindProperty(nameof(Order.OwnerBranchIdAtCreation)));
        Assert.NotNull(order.FindComplexProperty(nameof(Order.OrderDiscountAmount)));
        Assert.NotNull(order.FindComplexProperty(nameof(Order.OrderChargeAmount)));
        Assert.True(order.FindProperty(nameof(Order.SummaryToken))!.IsNullable);
        Assert.True(order.FindProperty(nameof(Order.SummaryTokenExpiresAt))!.IsNullable);
        Assert.DoesNotContain(_db.Model.GetEntityTypes(), entity =>
            string.Equals(entity.ClrType.Name, "Payment", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
