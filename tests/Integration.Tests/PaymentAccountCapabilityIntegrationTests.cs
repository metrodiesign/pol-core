using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PaymentAccountCapabilityIntegrationTests
{
    private static readonly Guid ActorId = Guid.Parse("d2000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 8, 18, 2, 3, 4, DateTimeKind.Utc);

    [Fact]
    public async Task PaymentPolicyAdministration_rechecks_parents_is_idempotent_and_keeps_disabled_children()
    {
        var database = $"pol_policy_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<IMigrator>().MigrateAsync();

            var merchantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var accountMethodId = Guid.NewGuid();
            await using (var sql = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(sql, merchantId, $"policy-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.ExecAsync(sql, """
                    INSERT merch.Users
                        (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                         DisplayName, FirstName, LastName, IdentityType)
                    VALUES
                        (@user, N'google', @subject, N'policy@example.com', 2, @merchant, 1,
                         SYSUTCDATETIME(), N'Policy User', N'Policy', N'User', 1);
                    """, ("@user", userId), ("@subject", $"policy-{userId:N}"), ("@merchant", merchantId));
                await IntegrationDb.ExecAsync(sql, $"""
                    INSERT txn.PspConnections
                        (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                         IsEnabled, CreatedAt, Health, Version)
                    VALUES ('{connectionId}', '{merchantId}', 1, '{PaymentCapabilityIds.TwoCTwoP}',
                            N'card', N'policy-test', 1, SYSUTCDATETIME(), 1, 1);
                    INSERT txn.MerchantProviderAccountMethods
                        (Id, MerchantId, PspConnectionId, PaymentProviderId, PaymentProviderMethodId,
                         PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                    VALUES ('{accountMethodId}', '{merchantId}', '{connectionId}',
                            '{PaymentCapabilityIds.TwoCTwoP}', '{PaymentCapabilityIds.TwoCTwoPCard}',
                            '{PaymentCapabilityIds.Card}', 1, '{ActorId}', SYSUTCDATETIME(), 1);
                    """);
            }

            var access = new AdminPaymentsAccess(ActorId, false, new HashSet<Guid> { merchantId });
            await using var db = NewContext(database, merchantId);
            var store = Store(db, supportsAll: true);
            var merchantIntent = new SetMerchantPaymentCapabilityIntent(
                merchantId, " CARD ", true, 0, "merchant-card", access);
            var merchant = await store.SetMerchantMethodAsync(merchantIntent, default);
            var replay = await store.SetMerchantMethodAsync(merchantIntent, default);
            Assert.True(merchant.Value.Effective);
            Assert.True(replay.Replayed);
            Assert.Equal(ActorId, merchant.Value.UpdatedBy);

            var user = await store.SetMerchantUserMethodAsync(new SetMerchantUserPaymentCapabilityIntent(
                merchantId, userId, "card", true, 0, "user-card", access), default);
            Assert.True(user.Value.Effective);
            Assert.Single((await store.ListMerchantMethodsAsync(merchantId, access, default))!);
            Assert.Single((await store.ListMerchantUserMethodsAsync(merchantId, userId, access, default))!);

            var disabled = await store.SetMerchantMethodAsync(new SetMerchantPaymentCapabilityIntent(
                merchantId, "card", false, merchant.Value.Version, "merchant-card-off", access), default);
            var child = await store.GetMerchantUserMethodAsync(merchantId, userId, "card", access, default);
            Assert.False(disabled.Value.Effective);
            Assert.NotNull(child);
            Assert.True(child.Enabled);
            Assert.False(child.Effective);

            await Assert.ThrowsAsync<PaymentCapabilityUnavailableException>(() =>
                store.SetMerchantMethodAsync(new SetMerchantPaymentCapabilityIntent(
                    merchantId, "promptpay", true, 0, "merchant-promptpay", access), default));
            Assert.Null(await store.ListMerchantMethodsAsync(Guid.NewGuid(), access, default));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Account_method_and_option_writer_is_scoped_idempotent_audited_and_projects_csv()
    {
        var database = $"pol_account_cap_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<IMigrator>().MigrateAsync();

            var merchantId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            await using (var sql = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(sql, merchantId, $"acct-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.ExecAsync(sql, $"""
                    INSERT txn.PspConnections
                        (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                         IsEnabled, CreatedAt, Health, Version)
                    VALUES ('{connectionId}', '{merchantId}', 1, '{PaymentCapabilityIds.TwoCTwoP}',
                            N'', N'account-capability-test', 1, SYSUTCDATETIME(), 1, 1);
                    """);
            }

            var access = new AdminPaymentsAccess(ActorId, false, new HashSet<Guid> { merchantId });
            await using (var db = NewContext(database, merchantId))
            {
                var store = Store(db, supportsAll: true);
                var cardIntent = new SetAccountPaymentCapabilityIntent(
                    connectionId, " CARD ", null, true, 0, "account-card", access);
                var first = await store.SetAccountMethodAsync(cardIntent, default);
                var replay = await store.SetAccountMethodAsync(cardIntent, default);

                Assert.False(first.Replayed);
                Assert.True(replay.Replayed);
                Assert.Equal(PaymentMethods.Card, first.Value.Method);
                Assert.Equal(ActorId, first.Value.UpdatedBy);
                Assert.Equal(Now, first.Value.UpdatedAt);

                var installment = await store.SetAccountMethodAsync(
                    new SetAccountPaymentCapabilityIntent(
                        connectionId, "installment", null, true, 0, "account-installment", access), default);
                Assert.True(installment.Value.Enabled);
            }

            var providerOptionId = Guid.NewGuid();
            await using (var sql = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.ExecAsync(sql, $"""
                    INSERT cfg.PaymentProviderMethodOptions
                        (Id, PaymentProviderMethodId, PaymentMethodId, PaymentMethodOptionId,
                         IsActive, CreatedBy, CreatedAt, Version)
                    VALUES ('{providerOptionId}', '{PaymentCapabilityIds.TwoCTwoPInstallment}',
                            '{PaymentCapabilityIds.Installment}', '{PaymentCapabilityIds.Kbank}',
                            1, '{ActorId}', SYSUTCDATETIME(), 1);
                    """);
            }

            await using (var db = NewContext(database, merchantId))
            {
                var option = await Store(db, supportsAll: true).SetAccountMethodOptionAsync(
                    new SetAccountPaymentCapabilityIntent(
                        connectionId, "installment", " kbank ", true, 0,
                        "account-installment-kbank", access), default);
                Assert.Equal("KBANK", option.Value.Option);
                Assert.True(option.Value.Enabled);
            }

            await using (var db = NewContext(database, merchantId))
            {
                await Assert.ThrowsAsync<PaymentCapabilityUnavailableException>(() =>
                    Store(db, supportsAll: false).SetAccountMethodAsync(
                        new SetAccountPaymentCapabilityIntent(
                            connectionId, "promptpay", null, true, 0,
                            "adapter-drift-promptpay", access), default));
            }

            await using (var sql = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal("card,installment", Convert.ToString(await IntegrationDb.ScalarAsync(sql,
                    "SELECT EnabledMethods FROM txn.PspConnections WHERE Id = @id;", ("@id", connectionId))));
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(sql,
                    "SELECT COUNT(*) FROM txn.MerchantProviderAccountMethods WHERE PspConnectionId = @id;",
                    ("@id", connectionId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(sql,
                    "SELECT COUNT(*) FROM txn.MerchantProviderAccountMethodOptions WHERE PspConnectionId = @id;",
                    ("@id", connectionId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(sql, $"""
                    SELECT COUNT(*) FROM txn.MerchantProviderAccountMethods
                    WHERE PspConnectionId = '{connectionId}'
                      AND PaymentMethodId = '{PaymentCapabilityIds.PromptPay}';
                    """)));
            }
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static MerchantRuntimeDbContext NewContext(string database, Guid merchantId) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        new Actor(merchantId), AllowAll.Instance, NoOpSecurityTelemetry.Instance);

    private static AdminPaymentsControlStore Store(MerchantRuntimeDbContext db, bool supportsAll) => new(
        db, new FixedClock(), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        null!, null!, new AdapterFactory(supportsAll), new PaymentAuthorizationSqlLockManager(db));

    private sealed class Actor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => ActorId;
        public bool HasActor => true;
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class AdapterFactory(bool supportsAll) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => new Adapter(psp, supportsAll);
    }

    private sealed class Adapter(Code psp, bool supportsAll) : IPspAdapter
    {
        public Code Psp => psp;
        public IReadOnlySet<string> SupportedMethods { get; } = supportsAll
            ? new HashSet<string>([PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment],
                StringComparer.Ordinal)
            : new HashSet<string>([PaymentMethods.Card], StringComparer.Ordinal);
        public Task<PspCharge> CreateRedirectChargeAsync(
            Payments.Domain.Session session, Guid pspConnectionId, string secret, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) =>
            throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, CancellationToken ct) => throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }
}
