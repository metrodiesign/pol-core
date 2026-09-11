using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Notifications.Application;
using Notifications.Domain;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Notifications;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Notifications;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "Notifications")]
[Collection("NotificationsSql")]
public sealed class Task7NotificationSqlIntegrationTests
{
    private const string DatabaseName = "PolNotificationsTask7Test";
    private static readonly Guid MerchantId = IntegrationDb.MerchantA;
    private static readonly DateTime Now = new(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-9.1")]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.3")]
    [Trait("Requirement", "REQ-9.8")]
    [Trait("Requirement", "REQ-9.10")]
    public async Task Registration_outbox_handoff_materializes_atomic_inbox_notification_and_two_channels_once()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            await using var db = CreateRuntimeDb();
            var materializer = new NotificationMaterializer(
                db,
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                new FixedClock(Now));
            var sourceEventId = Guid.NewGuid();
            var source = new NotificationEvent(
                sourceEventId,
                MerchantId,
                "AgentRegistrationDecidedV1",
                "{\"decision\":\"approved\"}",
                Now,
                "agent@example.com",
                "+66800000000",
                Guid.NewGuid(),
                Guid.NewGuid(),
                CorrelationId: "task7-registration-correlation");

            await materializer.MaterializeAsync(source, default);
            await materializer.MaterializeAsync(source, default);

            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.NotificationInboxMessages WHERE SourceEventId = @event;
                """, ("@event", sourceEventId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.Notifications WHERE SourceEventId = @event;
                """, ("@event", sourceEventId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.Deliveries WHERE SourceEventId = @event;
                """, ("@event", sourceEventId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.Deliveries
                WHERE SourceEventId = @event AND Channel = N'email'
                  AND RecipientSnapshot = N'agent@example.com'
                  AND TemplateVersion = N'agent-registration-result.v1';
                """, ("@event", sourceEventId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.Deliveries
                WHERE SourceEventId = @event AND Channel = N'sms'
                  AND RecipientSnapshot = N'+66800000000'
                  AND Status = 1;
                """, ("@event", sourceEventId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.6")]
    [Trait("Requirement", "REQ-9.7")]
    [Trait("Requirement", "REQ-9.8")]
    [Trait("Requirement", "REQ-9.10")]
    public async Task Control_plane_endpoint_is_snapshotted_into_one_business_webhook_delivery_and_replay_is_deduped()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var endpointId = Guid.NewGuid();
            var secretVersionId = Guid.NewGuid();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                var protector = new EphemeralDataProtectionProvider()
                    .CreateProtector("pol-core/delivery-secret/v1");
                var protectedSecret = protector.Protect("task7-webhook-secret");
                await IntegrationDb.ExecAsync(seed, """
                    INSERT admin.DeliverySecretVersions
                        (Id, OwnerId, MerchantId, OwnerType, ProtectedSecret, State, CreatedAt, ActivatedAt, RetiredAt)
                    VALUES (@id, @owner, @merchant, N'webhook-endpoint', @secret, 2, @at, @at, NULL);
                    INSERT admin.WebhookEndpoints
                        (Id, MerchantId, Name, Url, EventsCsv, Enabled, ActiveSecretVersionId,
                         SecretHint, CreatedAt, UpdatedAt, Version)
                    VALUES (@endpoint, @merchant, N'task7', N'https://business.example/hook',
                            N'payments.transaction-succeeded.v1', 1, @secretId, N'••••cret', @at, @at, 1);
                    """,
                    ("@id", secretVersionId), ("@owner", endpointId), ("@merchant", MerchantId),
                    ("@secret", protectedSecret), ("@endpoint", endpointId), ("@secretId", secretVersionId),
                    ("@at", Now));
            }

            await using var control = CreateControlPlaneDb();
            await using var commerce = CreateRuntimeDb();
            var materializer = new NotificationMaterializer(
                commerce,
                new MerchantRuntimeUnitOfWork(commerce, NoOpSecurityTelemetry.Instance),
                new FixedClock(Now),
                new BusinessWebhookConfigurationReader(control));
            var source = new NotificationEvent(
                Guid.NewGuid(), MerchantId, "payments.transaction-succeeded.v1", "{\"result\":\"paid\"}", Now,
                TransactionId: Guid.NewGuid(), OrderId: Guid.NewGuid());

            await materializer.MaterializeAsync(source, default);
            await using (var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.Deliveries
                    WHERE SourceEventId = @event AND Channel = N'business_webhook';
                    """, ("@event", source.SourceEventId))));
                Assert.Equal("https://business.example/hook", Convert.ToString(await IntegrationDb.ScalarAsync(check, """
                    SELECT EndpointUrlSnapshot FROM txn.Deliveries
                    WHERE SourceEventId = @event AND Channel = N'business_webhook';
                    """, ("@event", source.SourceEventId))));
                Assert.Equal("payment-succeeded.v1", Convert.ToString(await IntegrationDb.ScalarAsync(check, """
                    SELECT TemplateVersion FROM txn.Deliveries
                    WHERE SourceEventId = @event AND Channel = N'business_webhook';
                    """, ("@event", source.SourceEventId))));
            }

            await IntegrationDb.ExecAsync(
                await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)),
                "UPDATE admin.WebhookEndpoints SET Url = N'https://new.example/hook' WHERE Id = @id;",
                ("@id", endpointId));
            await materializer.MaterializeAsync(source, default);

            await using var final = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(final, """
                SELECT COUNT(*) FROM txn.Deliveries WHERE SourceEventId = @event;
                """, ("@event", source.SourceEventId))));
            Assert.Equal("https://business.example/hook", Convert.ToString(await IntegrationDb.ScalarAsync(final, """
                SELECT EndpointUrlSnapshot FROM txn.Deliveries
                WHERE SourceEventId = @event AND Channel = N'business_webhook';
                """, ("@event", source.SourceEventId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.1")]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.8")]
    public async Task Failed_commerce_materialization_rolls_back_inbox_and_notification_together()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            await using var db = CreateRuntimeDb(DenyDeliveryInsertAuthorizer.Instance);
            var source = new NotificationEvent(
                Guid.NewGuid(), MerchantId, "AgentRegistrationDecidedV1", "{}", Now,
                "agent@example.com", "+66800000000");
            var materializer = new NotificationMaterializer(
                db,
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                new FixedClock(Now));

            await Assert.ThrowsAsync<WriteGuardException>(() => materializer.MaterializeAsync(source, default));

            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT COUNT(*) FROM txn.NotificationInboxMessages;")));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT COUNT(*) FROM txn.Notifications;")));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT COUNT(*) FROM txn.Deliveries;")));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.1")]
    [Trait("Requirement", "REQ-9.8")]
    public async Task Fresh_migration_exposes_notification_dedupe_and_attempt_constraints()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT CASE WHEN OBJECT_ID(N'txn.Notifications', N'U') IS NULL THEN 0 ELSE 1 END;")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT CASE WHEN OBJECT_ID(N'txn.Deliveries', N'U') IS NULL THEN 0 ELSE 1 END;")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT CASE WHEN OBJECT_ID(N'txn.DeliveryAttempts', N'U') IS NULL THEN 0 ELSE 1 END;")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check,
                "SELECT CASE WHEN OBJECT_ID(N'txn.NotificationInboxMessages', N'U') IS NULL THEN 0 ELSE 1 END;")));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'txn.Notifications')
                  AND i.name = N'IX_Notifications_SourceEventId';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'txn.Deliveries')
                  AND i.name = N'IX_Deliveries_NotificationId_Channel_RecipientFingerprint';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'txn.NotificationInboxMessages')
                  AND i.name = N'IX_NotificationInboxMessages_SourceEventId';
                """)));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.3")]
    [Trait("Requirement", "REQ-9.4")]
    [Trait("Requirement", "REQ-9.5")]
    public async Task Delivery_processor_records_accepted_unknown_and_blocked_sms_without_rolling_back_source_state()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            await using (var seed = CreateRuntimeDb())
            {
                var materializer = new NotificationMaterializer(
                    seed,
                    new MerchantRuntimeUnitOfWork(seed, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now));
                await materializer.MaterializeAsync(new NotificationEvent(
                    Guid.NewGuid(), MerchantId, "AgentRegistrationDecidedV1", "{}", Now,
                    "agent@example.com", "+66800000000"), default);
                await materializer.MaterializeAsync(new NotificationEvent(
                    Guid.NewGuid(), MerchantId, "AgentRegistrationDecidedV1", "{\"decision\":\"rejected\"}", Now,
                    "second@example.com", "+66800000001"), default);
            }

            Guid emailId;
            Guid unknownId;
            Guid smsId;
            await using (var lookup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                emailId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT Id FROM txn.Deliveries WHERE MerchantId = @merchant AND Channel = N'email';
                    """, ("@merchant", MerchantId)))!);
                unknownId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT TOP (1) Id FROM txn.Deliveries
                    WHERE MerchantId = @merchant AND Channel = N'email' AND Id <> @known;
                    """, ("@merchant", MerchantId), ("@known", emailId)))!);
                smsId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT Id FROM txn.Deliveries WHERE MerchantId = @merchant AND Channel = N'sms';
                    """, ("@merchant", MerchantId)))!);
            }

            var emailSender = new FakeEmailSender(new DeliveryProviderResult(
                DeliveryProviderOutcome.Accepted, "smtp-message-1"));
            await using (var emailDb = CreateRuntimeDb())
            {
                var processor = new NotificationDeliveryProcessor(
                    emailDb,
                    new MerchantRuntimeUnitOfWork(emailDb, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now),
                    emailSender,
                    new NotConfiguredSmsSender());
                await processor.ProcessAsync(emailId, default);
            }

            var unknownSender = new FakeEmailSender(
                new DeliveryProviderAmbiguousException("provider response was lost"));
            var smsSender = new FakeSmsSender(isConfigured: false);
            await using (var unknownDb = CreateRuntimeDb())
            {
                var processor = new NotificationDeliveryProcessor(
                    unknownDb,
                    new MerchantRuntimeUnitOfWork(unknownDb, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now),
                    unknownSender,
                    smsSender);
                await processor.ProcessAsync(unknownId, default);
                await processor.ProcessAsync(smsId, default);
            }

            await using (var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal(5, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT Status FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", emailId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT AttemptCount FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", emailId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.DeliveryAttempts WHERE DeliveryId = @id AND Outcome = N'ACCEPTED';
                    """, ("@id", emailId))));
                Assert.Equal(6, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT Status FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", unknownId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.DeliveryAttempts WHERE DeliveryId = @id AND Outcome = N'UNKNOWN';
                    """, ("@id", unknownId))));
                Assert.Equal(7, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT Status FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", smsId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT AttemptCount FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", smsId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.DeliveryAttempts WHERE DeliveryId = @id;
                    """, ("@id", smsId))));
            }
            Assert.Equal(1, emailSender.Calls);
            Assert.Equal(1, unknownSender.Calls);
            Assert.Equal(0, smsSender.Calls);
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.8")]
    [Trait("Requirement", "REQ-9.9")]
    public async Task Operations_search_scope_retry_and_review_note_do_not_change_order_financial_state()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var orderId = Guid.NewGuid();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
                await SeedOrderAsync(seed, orderId, paymentStatus: 2);

            await using (var seed = CreateRuntimeDb())
            {
                var materializer = new NotificationMaterializer(
                    seed,
                    new MerchantRuntimeUnitOfWork(seed, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now));
                await materializer.MaterializeAsync(new NotificationEvent(
                    Guid.NewGuid(), MerchantId, "payments.transaction-succeeded.v1", "{}", Now,
                    "buyer@example.com", "+66800000000", OrderId: orderId,
                    OrderNo: "ORD6900000999", TransactionNo: "TXN-6900000999",
                    CorrelationId: "corr-search-7"), default);
            }

            Guid emailId;
            await using (var lookup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
                emailId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT Id FROM txn.Deliveries WHERE MerchantId = @merchant AND Channel = N'email';
                    """, ("@merchant", MerchantId)))!);

            await using (var processDb = CreateRuntimeDb())
            {
                var processor = new NotificationDeliveryProcessor(
                    processDb,
                    new MerchantRuntimeUnitOfWork(processDb, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now),
                    new FakeEmailSender(new DeliveryProviderResult(
                        DeliveryProviderOutcome.Failed, FailureCode: "smtp_rejected")),
                    new NotConfiguredSmsSender());
                await processor.ProcessAsync(emailId, default);
            }

            await using (var update = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
                await IntegrationDb.ExecAsync(update, """
                    UPDATE txn.Deliveries
                    SET Status = 8, CompletedAt = @at, NextAttemptAt = @at
                    WHERE Id = @id;
                    """, ("@at", Now), ("@id", emailId));

            await using var operationsDb = CreateRuntimeDb();
            var operations = new NotificationOperations(
                operationsDb,
                new MerchantRuntimeUnitOfWork(operationsDb, NoOpSecurityTelemetry.Instance),
                new FixedClock(Now));
            var access = new DeliveryAccess(false, new HashSet<Guid> { MerchantId });

            var byOrder = await operations.SearchAsync(
                new NotificationSearchQuery(1, 10, OrderNo: "ORD6900000999"), access, default);
            var byTransaction = await operations.SearchAsync(
                new NotificationSearchQuery(1, 10, TransactionNo: "TXN-6900000999"), access, default);
            var byCorrelation = await operations.SearchAsync(
                new NotificationSearchQuery(1, 10, CorrelationId: "corr-search-7"), access, default);
            var outside = await operations.SearchAsync(
                new NotificationSearchQuery(1, 10),
                new DeliveryAccess(false, new HashSet<Guid> { IntegrationDb.MerchantB }), default);

            Assert.Single(byOrder.Items);
            Assert.Single(byTransaction.Items);
            Assert.Single(byCorrelation.Items);
            Assert.Empty(outside.Items);
            Assert.Single(await operations.ListAttemptsAsync(emailId, access, default));

            var retried = await operations.RetryAsync(emailId, Guid.NewGuid(), access, default);
            Assert.NotNull(retried);
            Assert.Equal("PENDING", retried!.Status);
            Assert.Equal(1, retried.AttemptCount);
            Assert.Single(await operations.ListAttemptsAsync(emailId, access, default));

            var note = await operations.AddReviewNoteAsync(
                MerchantId,
                byOrder.Items[0].Id,
                emailId,
                Guid.NewGuid(),
                "ตรวจสอบการตอบกลับผู้ให้บริการ",
                access,
                default);
            Assert.Equal(emailId, note.DeliveryId);

            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT PaymentStatus FROM shop.Orders WHERE Id = @order;
                """, ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.NotificationReviewNotes
                WHERE MerchantId = @merchant AND DeliveryId = @delivery;
                """, ("@merchant", MerchantId), ("@delivery", emailId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.3")]
    [Trait("Requirement", "REQ-9.4")]
    [Trait("Requirement", "REQ-9.5")]
    public async Task Commerce_dispatcher_records_delivered_and_appends_five_failures_before_manual_queue()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            Guid deliveredId;
            Guid failedId;
            await using (var seed = CreateRuntimeDb())
            {
                var materializer = new NotificationMaterializer(
                    seed,
                    new MerchantRuntimeUnitOfWork(seed, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now));
                await materializer.MaterializeAsync(new NotificationEvent(
                    Guid.NewGuid(), MerchantId, "AgentRegistrationDecidedV1", "{\"event\":1}", Now,
                    "delivered@example.com"), default);
                await materializer.MaterializeAsync(new NotificationEvent(
                    Guid.NewGuid(), MerchantId, "AgentRegistrationDecidedV1", "{\"event\":2}", Now,
                    "failed@example.com"), default);
            }
            await using (var lookup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                deliveredId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT Id FROM txn.Deliveries WHERE RecipientSnapshot = N'delivered@example.com';
                    """))!);
                failedId = Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(lookup, """
                    SELECT Id FROM txn.Deliveries WHERE RecipientSnapshot = N'failed@example.com';
                    """))!);
            }

            await using (var deliveredDb = CreateRuntimeDb())
            {
                var processor = new NotificationDeliveryProcessor(
                    deliveredDb,
                    new MerchantRuntimeUnitOfWork(deliveredDb, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now),
                    new FakeEmailSender(new DeliveryProviderResult(DeliveryProviderOutcome.Delivered, "message-1")),
                    new NotConfiguredSmsSender());
                await processor.ProcessAsync(deliveredId, default);
            }

            var failedSender = new FakeEmailSender(
                new DeliveryProviderResult(DeliveryProviderOutcome.Failed, FailureCode: "smtp_rejected"));
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await using var failedDb = CreateRuntimeDb();
                var processor = new NotificationDeliveryProcessor(
                    failedDb,
                    new MerchantRuntimeUnitOfWork(failedDb, NoOpSecurityTelemetry.Instance),
                    new FixedClock(Now),
                    failedSender,
                    new NotConfiguredSmsSender());
                await processor.ProcessAsync(failedId, default);

                await using var checkAttempt = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
                var status = Convert.ToInt32(await IntegrationDb.ScalarAsync(checkAttempt, """
                    SELECT Status FROM txn.Deliveries WHERE Id = @id;
                    """, ("@id", failedId)));
                if (attempt < 5)
                {
                    Assert.Equal(1, status);
                    var next = Convert.ToDateTime(await IntegrationDb.ScalarAsync(checkAttempt, """
                        SELECT NextAttemptAt FROM txn.Deliveries WHERE Id = @id;
                        """, ("@id", failedId)));
                    Assert.Equal(Now + Delivery.RetryDelay(attempt), next);
                    await IntegrationDb.ExecAsync(checkAttempt, """
                        UPDATE txn.Deliveries
                        SET NextAttemptAt = @at, Status = 1, CompletedAt = NULL,
                            LeaseOwner = NULL, LeaseExpiresAt = NULL
                        WHERE Id = @id;
                        """, ("@at", Now), ("@id", failedId));
                }
                else
                {
                    Assert.Equal(8, status);
                }
            }

            await using var final = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(final, """
                SELECT Status FROM txn.Deliveries WHERE Id = @id;
                """, ("@id", deliveredId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(final, """
                SELECT AttemptCount FROM txn.Deliveries WHERE Id = @id;
                """, ("@id", deliveredId))));
            Assert.Equal(5, Convert.ToInt32(await IntegrationDb.ScalarAsync(final, """
                SELECT AttemptCount FROM txn.Deliveries WHERE Id = @id;
                """, ("@id", failedId))));
            Assert.Equal(5, Convert.ToInt32(await IntegrationDb.ScalarAsync(final, """
                SELECT COUNT(*) FROM txn.DeliveryAttempts WHERE DeliveryId = @id;
                """, ("@id", failedId))));
            Assert.Equal(5, failedSender.Calls);
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static MerchantRuntimeDbContext CreateRuntimeDb(IWriteAuthorizer? authorizer = null) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
            .Options,
        new IntegrationActor(MerchantId),
        authorizer ?? AllowAllWriteAuthorizer.Instance,
        NoOpSecurityTelemetry.Instance);

    private static ControlPlaneDbContext CreateControlPlaneDb() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
            .Options,
        AllowAllWriteAuthorizer.Instance,
        NoOpSecurityTelemetry.Instance);

    private static PolDbContext CreateMigrationContext() => new(
        new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
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

    private static async Task MigrateAsync()
    {
        await using var context = CreateMigrationContext();
        await context.Database.MigrateAsync();
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
                "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
        await IntegrationDb.ExecAsync(master, $"CREATE DATABASE [{DatabaseName}] COLLATE Thai_100_CI_AS;");
        await IntegrationDb.ExecAsync(master, $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 170;");
        await using var target = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(target, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task DropDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
                "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }

    private static Task SeedOrderAsync(SqlConnection connection, Guid orderId, int paymentStatus) =>
        IntegrationDb.ExecAsync(connection, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, CustomerEmail, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000999', 8, @paymentStatus, @at, @at, 2, 1, @at, @at,
                    NULL, NULL, N'Task7 buyer', '0800000000', 'buyer@example.com', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            """, ("@order", orderId), ("@merchant", MerchantId),
            ("@paymentStatus", paymentStatus), ("@at", Now));

    private sealed class IntegrationActor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId { get; } = merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class DenyDeliveryInsertAuthorizer : IWriteAuthorizer
    {
        public static readonly DenyDeliveryInsertAuthorizer Instance = new();

        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
            entityType != typeof(Notifications.Domain.Delivery) || operation != WriteOperation.Insert;
    }

    private sealed class FakeEmailSender : IEmailSenderPort
    {
        private readonly DeliveryProviderResult? _result;
        private readonly Exception? _exception;

        public FakeEmailSender(DeliveryProviderResult result) => _result = result;
        public FakeEmailSender(Exception exception) => _exception = exception;
        public int Calls { get; private set; }

        public Task<DeliveryProviderResult> SendAsync(
            EmailDeliveryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_exception is not null)
                throw _exception;
            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeSmsSender(bool isConfigured) : ISmsSenderPort
    {
        public bool IsConfigured => isConfigured;
        public int Calls { get; private set; }

        public Task<DeliveryProviderResult> SendAsync(
            SmsDeliveryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new DeliveryProviderResult(DeliveryProviderOutcome.Accepted, "sms-message"));
        }
    }

}
