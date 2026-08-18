using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Application.Ports;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Integration.Tests;

/// <summary>
/// <c>PayableOrderReader</c> against the real SQL Server (2c2p-e2e-fixes AC-2). <c>GetForMintAsync</c> used to
/// compose a projection (<c>o.Amount.Amount</c>/<c>o.Amount.Currency</c>) over an <c>UPDLOCK</c>
/// <c>FromSqlInterpolated</c> read, which made EF forget the <c>Amount</c> complex type's
/// <c>HasColumnName</c> mapping and emit <c>Invalid column name 'Amount_Amount'</c> — every <c>/pay</c> call
/// 500'd in the live sandbox. This calls the real port through the real <c>MerchantRuntimeDbContext</c> (not a
/// raw connection) so a regression here fails the same way it failed there.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PayableOrderReaderIntegrationTests
{
    [Fact]
    public async Task GetForMintAsync_locks_and_returns_the_real_order_through_EF()
    {
        var database = $"pol_payable_{Guid.NewGuid():N}";
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderNo = $"ORD69{Random.Shared.Next(60_000_000, 69_999_999)}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<IMigrator>().MigrateAsync();
            await using var c = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            await IntegrationDb.InsertMerchantAsync(c, merchantId, $"payable-{Guid.NewGuid():N}"[..24]);
            await InsertOrderAsync(c, orderId, merchantId, orderNo);

            await using var db = NewContext(database, merchantId);
            var sut = new PayableOrderReader(db);

            var payable = await sut.GetForMintAsync(orderId, CancellationToken.None);

            Assert.NotNull(payable);
            Assert.Equal(orderId, payable!.OrderId);
            Assert.Equal(Money.Of(15000m, "THB"), payable.Amount);
            Assert.Equal(PayableOrderStatus.Pending, payable.Status);
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static MerchantRuntimeDbContext NewContext(string database, Guid merchantId) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
            new FakeActor(merchantId), AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

    // Mirrors OrderNoSequenceIntegrationTests.InsertOrderAsync — same required columns, run-unique OrderNo.
    private static Task InsertOrderAsync(SqlConnection c, Guid orderId, Guid merchantId, string orderNo) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @m, @orderNo, 15000, N'THB', 1, SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()), N'Probe', '0800000000');
            """,
            ("@id", orderId), ("@m", merchantId), ("@orderNo", orderNo),
            ("@token", Guid.NewGuid().ToString("N")));

    private sealed class FakeActor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
