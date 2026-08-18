using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Merchants.Application.AdminControlPlane;
using Payments.Application.Capabilities;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Merchants;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Payments.Capabilities;
using SharedKernel;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PaymentCapabilityMigrationIntegrationTests
{
    private static readonly Guid ActorId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task PaymentCapabilityMigration_backfills_deterministically_and_records_ambiguity()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var uniqueUser = Guid.NewGuid();
            var ambiguousA = Guid.NewGuid();
            var ambiguousB = Guid.NewGuid();
            await using (var sql = await fixture.OpenAsync())
            {
                await SeedMerchantAsync(sql, fixture.MerchantId, "card,promptpay");
                await SeedConnectionAsync(sql, fixture.MerchantId, fixture.ConnectionId,
                    " CARD ,promptpay,card ");
                await SeedUserAsync(sql, uniqueUser, fixture.MerchantId, 2, "SALE-UNIQUE");
                await SeedUserAsync(sql, ambiguousA, fixture.MerchantId, 2, "SALE-AMBIG");
                await SeedUserAsync(sql, ambiguousB, fixture.MerchantId, 4, "SALE-AMBIG");
                await SeedOrderAsync(sql, fixture.MerchantId, "SALE-UNIQUE");
                await SeedOrderAsync(sql, fixture.MerchantId, "SALE-AMBIG");
            }

            await using var db = fixture.Context();
            var migration = Service(db);
            var first = await migration.BackfillAsync(ActorId, default);
            var second = await migration.BackfillAsync(ActorId, default);

            Assert.Equal(PaymentAuthorizationMode.LegacyRead, first.Mode);
            Assert.Equal(1, first.UnresolvedConflicts);
            Assert.Equal(first with { }, second);

            await using var verify = await fixture.OpenAsync();
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM txn.MerchantProviderAccountMethods
                WHERE MerchantId = '{fixture.MerchantId}';
                """)));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM txn.MerchantPaymentMethods
                WHERE MerchantId = '{fixture.MerchantId}';
                """)));
            Assert.Equal(4, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM txn.MerchantUserPaymentMethods
                WHERE MerchantId = '{fixture.MerchantId}';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM shop.Orders
                WHERE InitiatingAudience = 1 AND InitiatingMerchantUserId = '{uniqueUser}';
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM txn.MerchantProviderAccountMethodOptions;")));
            Assert.Equal(" CARD ,promptpay,card ", Convert.ToString(await IntegrationDb.ScalarAsync(
                verify, $"SELECT EnabledMethods FROM txn.PspConnections WHERE Id = '{fixture.ConnectionId}';")));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PaymentAuthorizationCutover_rolls_back_on_delta_conflict_then_flips_atomically()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await using (var sql = await fixture.OpenAsync())
            {
                await SeedMerchantAsync(sql, fixture.MerchantId, "card");
                await SeedConnectionAsync(sql, fixture.MerchantId, fixture.ConnectionId, "card");
                await SeedOrderAsync(sql, fixture.MerchantId, "LATE-CREATOR");
            }

            await using var db = fixture.Context();
            var migration = Service(db);
            await Assert.ThrowsAsync<PaymentAuthorizationCutoverBlockedException>(() =>
                migration.CutoverAsync(ActorId, oldInstancesDrained: false, default));
            await Assert.ThrowsAsync<PaymentAuthorizationCutoverBlockedException>(() =>
                migration.CutoverAsync(ActorId, oldInstancesDrained: true, default));

            await using (var blocked = await fixture.OpenAsync())
            {
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(blocked,
                    "SELECT Mode FROM cfg.PaymentAuthorizationStates;")));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(blocked,
                    "SELECT COUNT(*) FROM cfg.PaymentCapabilityMigrationConflicts WHERE ResolvedAt IS NULL;")));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(blocked, """
                    SELECT is_nullable FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'txn.PspConnections') AND name = N'PaymentProviderId';
                    """)));
                await SeedUserAsync(blocked, Guid.NewGuid(), fixture.MerchantId, 2, "LATE-CREATOR");
            }

            var completed = await migration.CutoverAsync(ActorId, oldInstancesDrained: true, default);

            Assert.Equal(PaymentAuthorizationMode.NormalizedRead, completed.Mode);
            Assert.NotNull(completed.CutoffAt);
            Assert.Equal(0, completed.UnresolvedConflicts);
            await using var verify = await fixture.OpenAsync();
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM sys.check_constraints
                WHERE name IN (N'CK_PspConnections_PaymentProviderRequired',
                               N'CK_Orders_InitiatingAudienceRequired')
                  AND is_disabled = 0 AND is_not_trusted = 0;
                """)));
            Assert.Equal("card", Convert.ToString(await IntegrationDb.ScalarAsync(
                verify, $"SELECT EnabledMethods FROM txn.PspConnections WHERE Id = '{fixture.ConnectionId}';")));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PaymentCapabilityRollback_never_returns_post_cutover_to_legacy_authorization()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await using (var sql = await fixture.OpenAsync())
            {
                await SeedMerchantAsync(sql, fixture.MerchantId, "card");
                await SeedConnectionAsync(sql, fixture.MerchantId, fixture.ConnectionId, "card");
            }

            await using var db = fixture.Context();
            var migration = Service(db);
            Assert.Equal(PaymentAuthorizationMode.LegacyRead,
                await migration.PrepareRollbackAsync(ActorId, normalizedAwareBinaryAvailable: false, default));
            await migration.CutoverAsync(ActorId, oldInstancesDrained: true, default);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migration.BackfillAsync(ActorId, default));

            Assert.Equal(PaymentAuthorizationMode.NormalizedRead,
                await migration.PrepareRollbackAsync(ActorId, normalizedAwareBinaryAvailable: true, default));
            Assert.Equal(PaymentAuthorizationMode.FailClosed,
                await migration.PrepareRollbackAsync(ActorId, normalizedAwareBinaryAvailable: false, default));
            Assert.Equal(PaymentAuthorizationMode.NormalizedRead,
                await migration.PrepareRollbackAsync(ActorId, normalizedAwareBinaryAvailable: true, default));

            await using var verify = await fixture.OpenAsync();
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT Mode FROM cfg.PaymentAuthorizationStates;")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, $"SELECT COUNT(*) FROM txn.MerchantProviderAccountMethods WHERE MerchantId = '{fixture.MerchantId}';")));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PaymentCapabilityMigration_compatibility_facades_dual_write_canonical_projections()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await using (var sql = await fixture.OpenAsync())
            {
                await SeedMerchantAsync(sql, fixture.MerchantId, "promptpay,card");
                await SeedConnectionAsync(sql, fixture.MerchantId, fixture.ConnectionId, "promptpay,card");
            }

            await using var db = fixture.Context();
            var unitOfWork = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
            var locks = new PaymentAuthorizationSqlLockManager(db);
            var adapters = new AdapterFactory();
            await new PaymentCapabilityMigrationService(db, unitOfWork, locks, adapters)
                .BackfillAsync(ActorId, default);

            var paymentsAccess = new AdminPaymentsAccess(
                ActorId, false, new HashSet<Guid> { fixture.MerchantId });
            var payments = new AdminPaymentsControlStore(
                db, new FixedClock(), unitOfWork, null!, null!, adapters, locks);
            var promptPay = await payments.GetMerchantMethodAsync(
                fixture.MerchantId, PaymentMethods.PromptPay, paymentsAccess, default);
            Assert.NotNull(promptPay);
            await payments.SetMerchantMethodAsync(new SetMerchantPaymentCapabilityIntent(
                fixture.MerchantId, PaymentMethods.PromptPay, false, promptPay.Version,
                "normalized-to-legacy", paymentsAccess), default);

            Assert.Equal("card", await ScalarStringAsync(
                fixture, $"SELECT EnabledChannels FROM merch.Merchants WHERE Id = '{fixture.MerchantId}'"));

            var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.MerchantId);
            var merchants = new AdminMerchantControlStore(db, new FixedClock(), unitOfWork, adapters, locks);
            await merchants.UpdateMerchantAsync(new AdminMerchantMutation(
                fixture.MerchantId, merchant.Name, merchant.Note, [PaymentMethods.PromptPay], null,
                merchant.Version, "legacy-to-normalized",
                new AdminMerchantAccess(ActorId, false, new HashSet<Guid> { fixture.MerchantId })), default);

            await using var verify = await fixture.OpenAsync();
            Assert.Equal("promptpay", Convert.ToString(await IntegrationDb.ScalarAsync(
                verify, $"SELECT EnabledChannels FROM merch.Merchants WHERE Id = '{fixture.MerchantId}'")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM txn.MerchantPaymentMethods
                WHERE MerchantId = '{fixture.MerchantId}' AND IsEnabled = 1
                  AND PaymentMethodId = '{PaymentCapabilityIds.PromptPay}';
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, $"""
                SELECT COUNT(*) FROM txn.MerchantPaymentMethods
                WHERE MerchantId = '{fixture.MerchantId}' AND IsEnabled = 1
                  AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                """)));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<string?> ScalarStringAsync(Fixture fixture, string sql)
    {
        await using var connection = await fixture.OpenAsync();
        return Convert.ToString(await IntegrationDb.ScalarAsync(connection, sql));
    }

    private static PaymentCapabilityMigrationService Service(MerchantRuntimeDbContext db)
    {
        var unitOfWork = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        return new PaymentCapabilityMigrationService(
            db, unitOfWork, new PaymentAuthorizationSqlLockManager(db), new AdapterFactory());
    }

    private static Task SeedMerchantAsync(SqlConnection sql, Guid merchantId, string channels) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT merch.Merchants
                (Id, Code, Name, Status, Country, Currency, EnabledChannels, CreatedAt, Version, Metadata)
            VALUES (@id, @code, N'Migration Merchant', 1, N'TH', N'THB', @channels,
                    SYSUTCDATETIME(), 1, N'{}');
            """, ("@id", merchantId), ("@code", $"mig-{merchantId:N}"[..24]), ("@channels", channels));

    private static Task SeedConnectionAsync(
        SqlConnection sql, Guid merchantId, Guid connectionId, string methods) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT txn.PspConnections
                (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                 IsEnabled, CreatedAt, Health, Version, Metadata)
            VALUES (@id, @merchant, 1, NULL, @methods, N'migration-test', 1,
                    SYSUTCDATETIME(), 1, 1, N'{"bank":"must-not-be-parsed"}');
            """, ("@id", connectionId), ("@merchant", merchantId), ("@methods", methods));

    private static Task SeedUserAsync(
        SqlConnection sql, Guid userId, Guid merchantId, int status, string saleCode) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT merch.Users
                (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                 DisplayName, FirstName, LastName, IdentityType, SaleCode)
            VALUES (@id, N'google', @subject, N'user@example.com', @status, @merchant, 1,
                    SYSUTCDATETIME(), N'Migration User', N'Migration', N'User', 1, @saleCode);
            """, ("@id", userId), ("@subject", $"sub-{userId:N}"), ("@status", status),
            ("@merchant", merchantId), ("@saleCode", saleCode));

    private static Task SeedOrderAsync(SqlConnection sql, Guid merchantId, string saleCode) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, SaleCode, AmountAmount, AmountCurrency, PaymentChannel,
                 Status, CreatedAt, SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @merchant, @orderNo, @saleCode, 100, N'THB', N'card', 1,
                    SYSUTCDATETIME(), @token, DATEADD(hour, 72, SYSUTCDATETIME()),
                    N'Migration Buyer', N'0800000000');
            """, ("@id", Guid.NewGuid()), ("@merchant", merchantId),
            ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"),
            ("@saleCode", saleCode), ("@token", Guid.NewGuid().ToString("N")));

    private sealed class Fixture(string database, Guid merchantId, Guid connectionId) : IAsyncDisposable
    {
        public Guid MerchantId { get; } = merchantId;
        public Guid ConnectionId { get; } = connectionId;

        public static async Task<Fixture> CreateAsync()
        {
            var database = $"pol_cap_migration_{Guid.NewGuid():N}";
            await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
            await using var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database);
            await migration.GetService<IMigrator>().MigrateAsync();
            return new Fixture(database, Guid.NewGuid(), Guid.NewGuid());
        }

        public MerchantRuntimeDbContext Context() => new(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
            new Actor(MerchantId), AllowWrites.Instance, NoOpSecurityTelemetry.Instance);

        public Task<SqlConnection> OpenAsync() => IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

        public ValueTask DisposeAsync() =>
            new(PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database));
    }

    private sealed class Actor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class AllowWrites : IWriteAuthorizer
    {
        public static readonly AllowWrites Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class AdapterFactory : IPspAdapterFactory
    {
        private static readonly IPspAdapter TwoCTwoP = new Adapter(
            Code.TwoCTwoP, PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment);
        private static readonly IPspAdapter Omise = new Adapter(Code.Omise, PaymentMethods.Card);

        public IPspAdapter For(Code psp) => psp switch
        {
            Code.TwoCTwoP => TwoCTwoP,
            Code.Omise => Omise,
            _ => throw new ArgumentOutOfRangeException(nameof(psp)),
        };
    }

    private sealed class Adapter(Code psp, params string[] methods) : IPspAdapter
    {
        public Code Psp { get; } = psp;
        public IReadOnlySet<string> SupportedMethods { get; } = methods.ToHashSet(StringComparer.Ordinal);
        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) =>
            throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }
}
