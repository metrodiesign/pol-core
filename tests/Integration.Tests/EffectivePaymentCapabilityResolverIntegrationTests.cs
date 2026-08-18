using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Payments.Capabilities;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class EffectivePaymentCapabilityResolverIntegrationTests
{
    [Fact]
    public async Task EffectivePaymentCapabilityResolver_re_reads_and_intersects_every_required_layer()
    {
        await WithFixtureAsync(async fixture =>
        {
            await using var db = NewContext(fixture);
            var resolver = Resolver(db, supportsTwoCTwoP: true);
            var user = new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserId);

            var anyProvider = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, " CARD ", null), default);
            var selected = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", "2C2P"), default);
            var wrongProvider = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", "omise"), default);
            var admin = await resolver.ResolveMethodAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(fixture.MerchantId, PaymentAudience.PlatformAdmin, null),
                "card", null), default);

            Assert.True(anyProvider.Allowed);
            Assert.True(selected.Allowed);
            Assert.Equal(fixture.ConnectionId, selected.QualifyingAccountId);
            Assert.False(wrongProvider.Allowed);
            Assert.Equal(PaymentCapabilityDenial.AccountUnavailable, wrongProvider.Denial);
            Assert.True(admin.Allowed);

            await UpdateAsync(fixture, $"""
                UPDATE txn.MerchantUserPaymentMethods SET IsEnabled = 0
                WHERE MerchantUserId = '{fixture.UserId}' AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                """);
            var revoked = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", null), default);
            Assert.Equal(PaymentCapabilityDenial.UserPolicyDenied, revoked.Denial);

            await UpdateAsync(fixture, $"""
                UPDATE txn.MerchantUserPaymentMethods SET IsEnabled = 1
                WHERE MerchantUserId = '{fixture.UserId}' AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                UPDATE merch.Users SET Status = 4 WHERE Id = '{fixture.UserId}';
                """);
            var suspended = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", null), default);
            Assert.Equal(PaymentCapabilityDenial.UserNotActive, suspended.Denial);

            await UpdateAsync(fixture, $"""
                UPDATE merch.Users SET Status = 2 WHERE Id = '{fixture.UserId}';
                UPDATE txn.MerchantProviderAccountMethods SET IsEnabled = 0
                WHERE PspConnectionId = '{fixture.ConnectionId}'
                  AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                """);
            var noAccount = await resolver.ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", null), default);
            Assert.Equal(PaymentCapabilityDenial.AccountUnavailable, noAccount.Denial);

            await UpdateAsync(fixture, $"""
                UPDATE txn.MerchantProviderAccountMethods SET IsEnabled = 1
                WHERE PspConnectionId = '{fixture.ConnectionId}'
                  AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                """);
            var drift = await Resolver(db, supportsTwoCTwoP: false).ResolveMethodAsync(
                new ResolvePaymentMethod(user, "card", null), default);
            Assert.Equal(PaymentCapabilityDenial.AdapterUnsupported, drift.Denial);
        });
    }

    [Fact]
    public async Task EffectivePaymentOptions_returns_exact_selected_provider_intersection_without_fallback()
    {
        await WithFixtureAsync(async fixture =>
        {
            await using var db = NewContext(fixture);
            var resolver = Resolver(db, supportsTwoCTwoP: true);
            var user = new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserBId);

            var options = await resolver.ResolveOptionsAsync(
                new ResolvePaymentMethod(user, "installment", "2c2p"), default);
            var otherProvider = await resolver.ResolveOptionsAsync(
                new ResolvePaymentMethod(user, "installment", "omise"), default);

            Assert.Equal(["KBANK", "SCB"], options.Select(x => x.Code).ToArray());
            Assert.Empty(otherProvider);
            Assert.DoesNotContain(options, x => x.Code is "KTC" or "BAY");

            await UpdateAsync(fixture, $"""
                UPDATE txn.MerchantUserPaymentMethods SET IsEnabled = 0
                WHERE MerchantUserId = '{fixture.UserBId}'
                  AND PaymentMethodId = '{PaymentCapabilityIds.Installment}';
                """);
            Assert.Empty(await resolver.ResolveOptionsAsync(
                new ResolvePaymentMethod(user, "installment", "2c2p"), default));
        });
    }

    [Fact]
    public async Task MerchantPaymentSelfRead_lists_only_current_user_effective_lowercase_methods()
    {
        await WithFixtureAsync(async fixture =>
        {
            await using var db = NewContext(fixture);
            var resolver = Resolver(db, supportsTwoCTwoP: true);
            var methods = await resolver.ListMethodsAsync(new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserId), default);

            Assert.Equal(["card", "promptpay"], methods.Select(x => x.Method).ToArray());
            Assert.All(methods, x => Assert.Equal(x.Method, x.Method.ToLowerInvariant()));
        });
    }

    [Fact]
    public async Task MerchantUserPaymentAcceptance_User_A_User_B_and_bank_options_match_policy_intersection()
    {
        await WithFixtureAsync(async fixture =>
        {
            await using var db = NewContext(fixture);
            var resolver = Resolver(db, supportsTwoCTwoP: true);
            var userA = new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserId);
            var userB = new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserBId);

            Assert.Equal(["card", "promptpay"], (await resolver.ListMethodsAsync(userA, default))
                .Select(x => x.Method).ToArray());
            Assert.Equal(PaymentCapabilityDenial.UserPolicyDenied,
                (await resolver.ResolveMethodAsync(
                    new ResolvePaymentMethod(userA, "installment", null), default)).Denial);

            Assert.Equal(["installment"], (await resolver.ListMethodsAsync(userB, default))
                .Select(x => x.Method).ToArray());
            Assert.Equal(PaymentCapabilityDenial.UserPolicyDenied,
                (await resolver.ResolveMethodAsync(new ResolvePaymentMethod(userB, "card", null), default)).Denial);
            Assert.Equal(PaymentCapabilityDenial.UserPolicyDenied,
                (await resolver.ResolveMethodAsync(
                    new ResolvePaymentMethod(userB, "promptpay", null), default)).Denial);

            var options = await resolver.ResolveOptionsAsync(
                new ResolvePaymentMethod(userB, "installment", "2c2p"), default);
            Assert.Equal(["KBANK", "SCB"], options.Select(x => x.Code).ToArray());
            Assert.DoesNotContain(options, x => x.Code is "KTC" or "BAY");
        });
    }

    [Fact]
    public async Task PaymentAuthorizationCutover_mode_switch_is_fresh_and_fail_closed()
    {
        await WithFixtureAsync(async fixture =>
        {
            await using var db = NewContext(fixture);
            var resolver = Resolver(db, supportsTwoCTwoP: true);
            var request = new ResolvePaymentMethod(new PaymentCapabilitySubject(
                fixture.MerchantId, PaymentAudience.User, fixture.UserId), "card", null);

            await UpdateAsync(fixture, $"""
                UPDATE txn.MerchantUserPaymentMethods SET IsEnabled = 0
                WHERE MerchantUserId = '{fixture.UserId}'
                  AND PaymentMethodId = '{PaymentCapabilityIds.Card}';
                UPDATE cfg.PaymentAuthorizationStates SET Mode = 1, CutoffAt = NULL, Version = Version + 1;
                """);
            Assert.True((await resolver.ResolveMethodAsync(request, default)).Allowed);

            await UpdateAsync(fixture,
                "UPDATE cfg.PaymentAuthorizationStates SET Mode = 2, Version = Version + 1;");
            Assert.Equal(PaymentCapabilityDenial.UserPolicyDenied,
                (await resolver.ResolveMethodAsync(request, default)).Denial);

            await UpdateAsync(fixture,
                "UPDATE cfg.PaymentAuthorizationStates SET Mode = 3, Version = Version + 1;");
            Assert.Equal(PaymentCapabilityDenial.MethodUnavailable,
                (await resolver.ResolveMethodAsync(request, default)).Denial);
        });
    }

    private static async Task WithFixtureAsync(Func<Fixture, Task> test)
    {
        var fixture = new Fixture($"pol_resolver_{Guid.NewGuid():N}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(fixture.Database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(fixture.Database))
                await migration.GetService<IMigrator>().MigrateAsync();
            await SeedAsync(fixture);
            await test(fixture);
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(fixture.Database);
        }
    }

    private static async Task SeedAsync(Fixture fixture)
    {
        await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(fixture.Database));
        var actor = Guid.NewGuid();
        await IntegrationDb.InsertMerchantAsync(db, fixture.MerchantId, $"resolve-{Guid.NewGuid():N}"[..24]);
        await IntegrationDb.ExecAsync(db, """
            UPDATE cfg.PaymentAuthorizationStates
            SET Mode = 2, CutoffAt = SYSUTCDATETIME(), Version = Version + 1;
            """);
        await IntegrationDb.ExecAsync(db, """
            INSERT merch.Users
                (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                 DisplayName, FirstName, LastName, IdentityType)
            VALUES
                (@user, N'google', @subject, N'user@example.com', 2, @merchant, 1,
                 SYSUTCDATETIME(), N'User A', N'User', N'A', 1),
                (@userB, N'google', @subjectB, N'user-b@example.com', 2, @merchant, 1,
                 SYSUTCDATETIME(), N'User B', N'User', N'B', 1);
            """, ("@user", fixture.UserId), ("@subject", $"subject-{fixture.UserId:N}"),
            ("@userB", fixture.UserBId), ("@subjectB", $"subject-{fixture.UserBId:N}"),
            ("@merchant", fixture.MerchantId));
        await IntegrationDb.ExecAsync(db, $"""
            INSERT txn.PspConnections
                (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                 IsEnabled, CreatedAt, Health, Version)
            VALUES ('{fixture.ConnectionId}', '{fixture.MerchantId}', 1,
                    '{PaymentCapabilityIds.TwoCTwoP}', N'card,installment,promptpay', N'resolver-test',
                    1, SYSUTCDATETIME(), 1, 1);

            INSERT txn.MerchantProviderAccountMethods
                (Id, MerchantId, PspConnectionId, PaymentProviderId, PaymentProviderMethodId,
                 PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES
                ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{fixture.ConnectionId}',
                 '{PaymentCapabilityIds.TwoCTwoP}', '{PaymentCapabilityIds.TwoCTwoPCard}',
                 '{PaymentCapabilityIds.Card}', 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{fixture.ConnectionId}',
                 '{PaymentCapabilityIds.TwoCTwoP}', '{PaymentCapabilityIds.TwoCTwoPPromptPay}',
                 '{PaymentCapabilityIds.PromptPay}', 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{fixture.InstallmentAccountMethodId}', '{fixture.MerchantId}', '{fixture.ConnectionId}',
                 '{PaymentCapabilityIds.TwoCTwoP}', '{PaymentCapabilityIds.TwoCTwoPInstallment}',
                 '{PaymentCapabilityIds.Installment}', 1, '{actor}', SYSUTCDATETIME(), 1);

            INSERT txn.MerchantPaymentMethods
                (Id, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES
                ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{PaymentCapabilityIds.Card}',
                 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{PaymentCapabilityIds.PromptPay}',
                 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{PaymentCapabilityIds.Installment}',
                 1, '{actor}', SYSUTCDATETIME(), 1);

            INSERT txn.MerchantUserPaymentMethods
                (Id, MerchantUserId, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES
                ('{Guid.NewGuid()}', '{fixture.UserId}', '{fixture.MerchantId}',
                 '{PaymentCapabilityIds.Card}', 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{Guid.NewGuid()}', '{fixture.UserId}', '{fixture.MerchantId}',
                 '{PaymentCapabilityIds.PromptPay}', 1, '{actor}', SYSUTCDATETIME(), 1),
                ('{Guid.NewGuid()}', '{fixture.UserBId}', '{fixture.MerchantId}',
                 '{PaymentCapabilityIds.Installment}', 1, '{actor}', SYSUTCDATETIME(), 1);
            """);

        var providerOptions = new Dictionary<Guid, Guid>
        {
            [PaymentCapabilityIds.Kbank] = Guid.NewGuid(),
            [PaymentCapabilityIds.Scb] = Guid.NewGuid(),
            [PaymentCapabilityIds.Ktc] = Guid.NewGuid(),
            [PaymentCapabilityIds.Bay] = Guid.NewGuid(),
        };
        foreach (var option in providerOptions)
        {
            await IntegrationDb.ExecAsync(db, $"""
                INSERT cfg.PaymentProviderMethodOptions
                    (Id, PaymentProviderMethodId, PaymentMethodId, PaymentMethodOptionId,
                     IsActive, CreatedBy, CreatedAt, Version)
                VALUES ('{option.Value}', '{PaymentCapabilityIds.TwoCTwoPInstallment}',
                        '{PaymentCapabilityIds.Installment}', '{option.Key}',
                        1, '{actor}', SYSUTCDATETIME(), 1);
                """);
        }
        await InsertAccountOptionAsync(db, fixture, providerOptions[PaymentCapabilityIds.Kbank],
            PaymentCapabilityIds.Kbank, enabled: true, actor);
        await InsertAccountOptionAsync(db, fixture, providerOptions[PaymentCapabilityIds.Scb],
            PaymentCapabilityIds.Scb, enabled: true, actor);
        await InsertAccountOptionAsync(db, fixture, providerOptions[PaymentCapabilityIds.Ktc],
            PaymentCapabilityIds.Ktc, enabled: false, actor);
    }

    private static Task InsertAccountOptionAsync(
        SqlConnection db, Fixture fixture, Guid providerOptionId, Guid optionId, bool enabled, Guid actor) =>
        IntegrationDb.ExecAsync(db, $"""
            INSERT txn.MerchantProviderAccountMethodOptions
                (Id, MerchantId, MerchantProviderAccountMethodId, PspConnectionId,
                 PaymentProviderId, PaymentProviderMethodId, PaymentMethodId,
                 PaymentProviderMethodOptionId, PaymentMethodOptionId,
                 IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES ('{Guid.NewGuid()}', '{fixture.MerchantId}', '{fixture.InstallmentAccountMethodId}',
                    '{fixture.ConnectionId}', '{PaymentCapabilityIds.TwoCTwoP}',
                    '{PaymentCapabilityIds.TwoCTwoPInstallment}', '{PaymentCapabilityIds.Installment}',
                    '{providerOptionId}', '{optionId}', {(enabled ? 1 : 0)},
                    '{actor}', SYSUTCDATETIME(), 1);
            """);

    private static Task UpdateAsync(Fixture fixture, string sql) => UpdateCoreAsync(fixture.Database, sql);

    private static async Task UpdateCoreAsync(string database, string sql)
    {
        await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(db, sql);
    }

    private static MerchantRuntimeDbContext NewContext(Fixture fixture) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(fixture.Database), sql => sql.UseCompatibilityLevel(170)).Options,
        new Actor(fixture.MerchantId, fixture.UserId), AllowAll.Instance, NoOpSecurityTelemetry.Instance);

    private static EffectivePaymentCapabilityResolver Resolver(
        MerchantRuntimeDbContext db, bool supportsTwoCTwoP) => new(
        db, new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new PaymentAuthorizationSqlLockManager(db), new AdapterFactory(supportsTwoCTwoP));

    private sealed record Fixture(string Database, Guid MerchantId, Guid UserId, Guid ConnectionId)
    {
        public Guid UserBId { get; } = Guid.NewGuid();
        public Guid InstallmentAccountMethodId { get; } = Guid.NewGuid();
    }

    private sealed class Actor(Guid merchantId, Guid userId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => userId;
        public bool HasActor => true;
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class AdapterFactory(bool supportsTwoCTwoP) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => new Adapter(psp, psp == Code.TwoCTwoP && supportsTwoCTwoP
            ? [PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment]
            : psp == Code.Omise ? [PaymentMethods.Card] : []);
    }

    private sealed class Adapter(Code psp, string[] supported) : IPspAdapter
    {
        public Code Psp => psp;
        public IReadOnlySet<string> SupportedMethods { get; } = supported.ToHashSet(StringComparer.Ordinal);
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
