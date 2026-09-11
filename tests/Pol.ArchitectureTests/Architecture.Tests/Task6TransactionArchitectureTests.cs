using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Architecture.Tests;

[Trait("Capability", "CheckoutTransactions")]
public sealed class Task6TransactionArchitectureTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly PolDbContext _db;

    public Task6TransactionArchitectureTests()
    {
        _connection.Open();
        _db = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlite(_connection)
                .EnableServiceProviderCaching(false)
                .Options,
            new ModuleAssemblies([
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
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    [Trait("Requirement", "REQ-8.9")]
    public void Transaction_model_is_pinned_to_merchant_order_and_keeps_success_rows_distinct()
    {
        var transaction = _db.Model.FindEntityType(typeof(Transaction))!;
        var transactionEvent = _db.Model.FindEntityType(typeof(TransactionEvent))!;
        var order = _db.Model.FindEntityType(typeof(Orders.Domain.Order))!;

        Assert.Equal(SchemaNames.Txn, transaction.GetSchema());
        Assert.Equal("Transactions", transaction.GetTableName());
        Assert.Equal(SchemaNames.Txn, transactionEvent.GetSchema());
        Assert.Equal("TransactionEvents", transactionEvent.GetTableName());
        var amount = transaction.FindComplexProperty(nameof(Transaction.Amount))!;
        Assert.NotNull(amount.ComplexType.FindProperty(nameof(Money.Amount)));
        Assert.NotNull(amount.ComplexType.FindProperty(nameof(Money.Currency)));
        Assert.NotNull(transaction.FindProperty(nameof(Transaction.OrderSnapshot)));
        Assert.True(transaction.FindProperty(nameof(Transaction.OrderSnapshot))!.IsUnicode());
        Assert.NotNull(transaction.FindProperty(nameof(Transaction.ProviderRequestReference)));
        Assert.NotNull(transaction.FindProperty(nameof(Transaction.ProviderAccountId)));
        Assert.NotNull(transaction.FindProperty(nameof(Transaction.CredentialVersionId)));
        Assert.NotNull(transaction.FindProperty(nameof(Transaction.Environment)));
        Assert.Contains(transaction.GetIndexes(), x => x.IsUnique
            && x.GetFilter() == "[Status] IN (1, 2)"
            && x.Properties.Select(p => p.Name).SequenceEqual([nameof(Transaction.OrderId)]));
        Assert.Contains(transaction.GetIndexes(), x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual([
                nameof(Transaction.OrderId), nameof(Transaction.AttemptNo)]));
        Assert.Contains(transaction.GetIndexes(), x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual([
                nameof(Transaction.ProviderAccountId), nameof(Transaction.Environment),
                nameof(Transaction.ProviderRequestReference)]));
        Assert.DoesNotContain(transaction.GetIndexes(), x => x.IsUnique
            && x.Properties.Any(p => p.Name == nameof(Transaction.Status))
            && x.GetFilter() is null);
        Assert.Contains(transaction.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(Orders.Domain.Order)
            && fk.Properties.Select(p => p.Name).SequenceEqual([
                nameof(Transaction.OrderId), nameof(Transaction.MerchantId)]));
        Assert.Contains(transactionEvent.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(Transaction)
            && fk.Properties.Select(p => p.Name).SequenceEqual([
                nameof(TransactionEvent.TransactionId), nameof(TransactionEvent.MerchantId)]));
        Assert.Contains(transactionEvent.GetIndexes(), x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual([
                nameof(TransactionEvent.TransactionId), nameof(TransactionEvent.EventReference),
                nameof(TransactionEvent.Source)]));
        Assert.Equal(typeof(Guid?), order.FindProperty(nameof(Orders.Domain.Order.SuccessfulTransactionId))!.ClrType);
        Assert.Contains(order.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(Transaction)
            && fk.Properties.Select(p => p.Name).SequenceEqual([
                nameof(Orders.Domain.Order.SuccessfulTransactionId), nameof(Orders.Domain.Order.MerchantId)]));
        Assert.DoesNotContain(_db.Model.GetEntityTypes(), entity =>
            string.Equals(entity.ClrType.Name, "Payment", StringComparison.Ordinal));
        Assert.Equal(true, transactionEvent.FindAnnotation("Pol:AppendOnly")?.Value);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.1")]
    public void Transaction_required_fields_are_not_optional_in_the_model()
    {
        var entity = _db.Model.FindEntityType(typeof(Transaction))!;
        Assert.False(entity.FindProperty(nameof(Transaction.MerchantId))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Transaction.OrderId))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Transaction.OrderSnapshot))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Transaction.ProviderRequestReference))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Transaction.CredentialVersionId))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Transaction.Version))!.IsNullable);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
