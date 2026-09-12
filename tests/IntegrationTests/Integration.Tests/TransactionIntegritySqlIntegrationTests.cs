extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Carts.Application;
using Carts.Domain;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Outbox;
using Platform.Application.Transactions;
using SharedKernel;

using DirectCoordinator = ApiHost::Api.Orders.OrderCreationCoordinator;
using DirectRequest = ApiHost::Api.Orders.CommitOrderFromCartRequest;
using ProductSnapshot = ApiHost::Api.Orders.ValidatedProductSnapshot;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "TransactionIntegrity")]
[Collection("CheckoutTransactionsSql")]
public sealed class TransactionIntegritySqlIntegrationTests
{
    private const string DatabaseName = "PolTransactionIntegrityTest";
    private const string FailureTriggerName = "TR_TransactionIntegrity_FailOutboxInsert";
    private const string OrderItemsFailureConstraintName = "CK_TransactionIntegrity_OrderItemsFailure";
    private const string PaymentLinksFailureConstraintName = "CK_TransactionIntegrity_PaymentLinksFailure";
    private const string OrdersFailureConstraintName = "CK_TransactionIntegrity_OrdersFailure";
    private static readonly Guid MerchantId = IntegrationDb.MerchantA;
    private static readonly Guid CheckoutOrderId = Guid.Parse("a6000000-0000-4000-8000-000000000001");
    private static readonly Guid CheckoutLinkId = Guid.Parse("a6000000-0000-4000-8000-000000000002");
    private static readonly Guid CheckoutItemId = Guid.Parse("a6000000-0000-4000-8000-000000000003");
    private static readonly Guid ResultOrderId = Guid.Parse("a6000000-0000-4000-8000-000000000011");
    private static readonly Guid ResultItemId = Guid.Parse("a6000000-0000-4000-8000-000000000012");
    private static readonly Guid ResultTransactionId = Guid.Parse("a6000000-0000-4000-8000-000000000013");
    private static readonly Guid CartId = Guid.Parse("a6000000-0000-4000-8000-000000000041");
    private static readonly DateTime Now = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-8.4")]
    [Trait("Requirement", "REQ-8.6")]
    [Trait("Requirement", "REQ-8.7")]
    public async Task Sql_verified_result_failure_rolls_back_all_rows_and_retry_reuses_evidence()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            var credential = Guid.Parse("a6000000-0000-4000-8000-000000000021");
            var connection = NewConnection(credential);
            await MigrateAndSeedResultAsync(connection.Id, credential);
            await InstallOutboxFailureTriggerAsync();
            triggerInstalled = true;

            var adapter = new ResultAdapter(PspChargeStatus.Paid, Money.Of(100m, "THB"));
            Exception? failure;
            await using (var db = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid())))
            {
                failure = await Record.ExceptionAsync(() => CreateCheckoutService(
                    db, connection, credential, adapter).VerifyAsync(
                        MerchantId, ResultTransactionId, "webhook", "same-provider-evidence", default));
            }

            Assert.NotNull(failure);
            Assert.Contains("transaction-integrity injected outbox failure", failure!.ToString(),
                StringComparison.OrdinalIgnoreCase);

            await using (var afterFailure = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal((int)TransactionStatus.Created, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    afterFailure, "SELECT Status FROM txn.Transactions WHERE Id=@id;", ("@id", ResultTransactionId))));
                Assert.Equal((int)PaymentStatus.Processing, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    afterFailure, "SELECT PaymentStatus FROM shop.Orders WHERE Id=@id;", ("@id", ResultOrderId))));
                Assert.True(IsNull(await IntegrationDb.ScalarAsync(
                    afterFailure, "SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id=@id;", ("@id", ResultOrderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM txn.TransactionEvents WHERE TransactionId=@id;
                    """, ("@id", ResultTransactionId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;
                    """, ("@merchant", MerchantId))));
            }

            await DropOutboxFailureTriggerAsync();
            triggerInstalled = false;

            await using (var retryDb = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid())))
            {
                var retry = await CreateCheckoutService(retryDb, connection, credential, adapter).VerifyAsync(
                    MerchantId, ResultTransactionId, "webhook", "same-provider-evidence", default);
                Assert.Equal(TransactionStatus.Succeeded, retry.TransactionStatus);
            }

            await using var afterRetry = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal((int)TransactionStatus.Succeeded, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT Status FROM txn.Transactions WHERE Id=@id;", ("@id", ResultTransactionId))));
            Assert.Equal((int)PaymentStatus.Paid, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT PaymentStatus FROM shop.Orders WHERE Id=@id;", ("@id", ResultOrderId))));
            Assert.Equal(ResultTransactionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id=@id;", ("@id", ResultOrderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM txn.TransactionEvents
                WHERE TransactionId=@id AND Source=N'webhook' AND EventReference=N'same-provider-evidence';
                """, ("@id", ResultTransactionId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM txn.OutboxMessages
                WHERE MerchantId=@merchant AND Type=N'payments.transaction-succeeded.v1';
                """, ("@merchant", MerchantId))));
            Assert.Equal(2, adapter.FetchCalls);
        }
        finally
        {
            if (triggerInstalled)
                await DropOutboxFailureTriggerAsync();
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.4")]
    [Trait("Requirement", "REQ-8.6")]
    public async Task Sql_psp_create_and_fetch_probe_no_active_transaction_after_intent_commit()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAndSeedCheckoutAsync();
            var credential = Guid.Parse("a6000000-0000-4000-8000-000000000031");
            var connection = NewConnection(credential);
            await using var db = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid()));
            var adapter = new TransactionBoundaryProbeAdapter(db);
            var service = CreateCheckoutService(db, connection, credential, adapter);

            var started = await service.StartAsync(
                new CheckoutConfirmCommand(
                    MerchantId,
                    CheckoutOrderId,
                    2,
                    "proof",
                    "csrf",
                    "card",
                    "canonical-start-probe",
                    "tab"),
                CheckoutLinkId,
                "tab",
                default);
            Assert.Equal(TransactionStatus.Created, started.TransactionStatus);

            var verified = await service.VerifyAsync(
                MerchantId, started.TransactionId, "webhook", "canonical-fetch-probe", default);
            Assert.Equal(TransactionStatus.Succeeded, verified.TransactionStatus);
            Assert.Equal(1, adapter.CreateCalls);
            Assert.Equal(1, adapter.FetchCalls);
            Assert.True(adapter.CreateObservedNoActiveTransaction);
            Assert.True(adapter.CreateObservedCommittedIntent);
            Assert.True(adapter.FetchObservedNoActiveTransaction);
            Assert.True(adapter.FetchObservedCommittedIntent);

            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal((int)PaymentStatus.Paid, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                check, "SELECT PaymentStatus FROM shop.Orders WHERE Id=@id;", ("@id", CheckoutOrderId))));
            Assert.Equal(started.TransactionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(
                check, "SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id=@id;", ("@id", CheckoutOrderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.OutboxMessages
                WHERE MerchantId=@merchant AND Type=N'payments.transaction-succeeded.v1';
                """, ("@merchant", MerchantId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.1")]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-8.1")]
    public async Task Sql_issue_failure_rolls_back_link_replay_idempotency_and_outbox_then_retries()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            Guid orderId;
            long draftVersion;

            await using (var createDb = CreateRuntimeDb(actor))
            {
                var created = await CreateOrderHandlerFor(createDb, actor, clock).Handle(
                    new CreateOrderCommand(
                        MerchantId,
                        actor.UserId!.Value,
                        "insurance",
                        [new OrderItemRequest("trusted-product", 2)],
                        new OrderOwnerRequest(null, null),
                        IssueNow: false,
                        IdempotencyKey: "draft-for-issue",
                        NotifyOnIssue: true,
                        NotificationEmail: "buyer@example.test"),
                    default);
                orderId = created.Order.OrderId;
                draftVersion = created.Order.Version;
                Assert.Equal(OrderStatus.Draft, created.Order.OrderStatus);
                Assert.Null(created.PaymentLink);
            }

            await InstallOutboxFailureTriggerAsync();
            triggerInstalled = true;
            var issueCommand = new IssueOrderCommand(MerchantId, orderId, draftVersion, "issue-with-failure");
            Exception? failure;
            await using (var issueDb = CreateRuntimeDb(actor))
            {
                failure = await Record.ExceptionAsync(() => IssueOrderHandlerFor(issueDb, actor, clock)
                    .Handle(issueCommand, default).AsTask());
            }

            Assert.NotNull(failure);
            Assert.Contains("transaction-integrity injected outbox failure", failure!.ToString(),
                StringComparison.OrdinalIgnoreCase);

            await using (var afterFailure = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal((int)OrderStatus.Draft, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    afterFailure, "SELECT Status FROM shop.Orders WHERE Id=@id;", ("@id", orderId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM shop.OrderItems WHERE OrderId=@order;
                    """, ("@order", orderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM checkout.PaymentLinks WHERE OrderId=@order;
                    """, ("@order", orderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM checkout.PaymentLinkReplays
                    WHERE OrderId=@order AND Operation=N'order.issue';
                    """, ("@order", orderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM txn.IdempotencyRecords
                    WHERE MerchantId=@merchant AND Context=N'order.issue';
                    """, ("@merchant", MerchantId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure, """
                    SELECT COUNT(*) FROM txn.OutboxMessages
                    WHERE MerchantId=@merchant AND Type=N'PaymentLinkNotificationRequestedV1';
                    """, ("@merchant", MerchantId))));
            }

            await DropOutboxFailureTriggerAsync();
            triggerInstalled = false;

            await using (var retryDb = CreateRuntimeDb(actor))
            {
                var issued = await IssueOrderHandlerFor(retryDb, actor, clock).Handle(issueCommand, default);
                Assert.Equal(OrderStatus.Open, issued.Order.OrderStatus);
                Assert.NotNull(issued.PaymentLink);
            }

            await using var afterRetry = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal((int)OrderStatus.Open, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT Status FROM shop.Orders WHERE Id=@id;", ("@id", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM shop.OrderItems WHERE OrderId=@order;
                """, ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM checkout.PaymentLinks
                WHERE OrderId=@order AND Status=1;
                """, ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM checkout.PaymentLinkReplays
                WHERE OrderId=@order AND Operation=N'order.issue';
                """, ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM txn.IdempotencyRecords
                WHERE MerchantId=@merchant AND Context=N'order.issue';
                """, ("@merchant", MerchantId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM txn.OutboxMessages
                WHERE MerchantId=@merchant AND Type=N'PaymentLinkNotificationRequestedV1';
                """, ("@merchant", MerchantId))));
        }
        finally
        {
            if (triggerInstalled)
                await DropOutboxFailureTriggerAsync();
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Sql_patch_item_replacement_failure_rolls_back_order_and_items()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            Guid orderId;
            long draftVersion;
            await using (var createDb = CreateRuntimeDb(actor))
            {
                var draft = await CreateOrderHandlerFor(createDb, actor, clock).Handle(
                    new CreateOrderCommand(
                        MerchantId,
                        actor.UserId!.Value,
                        "insurance",
                        [new OrderItemRequest("trusted-product", 2)],
                        new OrderOwnerRequest(null, null),
                        IssueNow: false,
                        IdempotencyKey: "draft-for-patch"),
                    default);
                orderId = draft.Order.OrderId;
                draftVersion = draft.Order.Version;
            }

            await InstallFailureConstraintAsync(
                "shop", "OrderItems", OrderItemsFailureConstraintName, "[ProductCode] <> N'SQL-DOC'");
            triggerInstalled = true;
            Exception? failure;
            await using (var patchDb = CreateRuntimeDb(actor))
            {
                failure = await Record.ExceptionAsync(() => new PatchDraftOrderHandler(
                    new IntegrationPricing(),
                    new FixedOrderOwnerResolver(),
                    new OrderRepository(patchDb),
                    new MerchantRuntimeUnitOfWork(patchDb, NoOpSecurityTelemetry.Instance),
                    clock).Handle(
                    new PatchDraftOrderCommand(
                        MerchantId,
                        orderId,
                        actor.UserId!.Value,
                        null,
                        [new OrderItemRequest("replacement-request", 3)],
                        null,
                        draftVersion),
                    default).AsTask());
            }

            Assert.NotNull(failure);
            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal((int)OrderStatus.Draft, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT Status FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            Assert.Equal(draftVersion, Convert.ToInt64(await IntegrationDb.ScalarAsync(
                verify, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId))));
            Assert.Equal("SQL-DOC", Convert.ToString(await IntegrationDb.ScalarAsync(
                verify, "SELECT ProductCode FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId))));
        }
        finally
        {
            if (triggerInstalled)
                await DropFailureConstraintAsync("shop", "OrderItems", OrderItemsFailureConstraintName);
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Sql_rotate_failure_rolls_back_old_link_new_link_replay_and_claim()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            var created = await CreateIssuedOrderAsync(actor, clock, "issued-for-rotate");
            var oldLinkId = created.PaymentLink!.LinkId;
            var originalVersion = created.Order.Version;

            await InstallFailureConstraintAsync(
                "checkout", "PaymentLinks", PaymentLinksFailureConstraintName, "[Status] <> 2");
            triggerInstalled = true;
            var command = new RotatePaymentLinkCommand(
                MerchantId, created.Order.OrderId, originalVersion, "rotate-with-failure");
            Exception? failure;
            await using (var rotateDb = CreateRuntimeDb(actor))
            {
                failure = await Record.ExceptionAsync(() => RotatePaymentLinkHandlerFor(
                    rotateDb, actor, clock).Handle(command, default).AsTask());
            }

            Assert.NotNull(failure);
            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal(originalVersion, Convert.ToInt64(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", created.Order.OrderId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM checkout.PaymentLinks WHERE OrderId=@order AND Status=1;
                    """, ("@order", created.Order.OrderId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM checkout.PaymentLinks WHERE Id=@link;
                    """, ("@link", oldLinkId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM checkout.PaymentLinkReplays
                    WHERE OrderId=@order AND Operation=N'payment-link.rotate';
                    """, ("@order", created.Order.OrderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM txn.IdempotencyRecords
                    WHERE MerchantId=@merchant AND Context=N'payment-link.rotate';
                    """, ("@merchant", MerchantId))));
            }

            await DropFailureConstraintAsync("checkout", "PaymentLinks", PaymentLinksFailureConstraintName);
            triggerInstalled = false;
            await using (var retryDb = CreateRuntimeDb(actor))
            {
                var retry = await RotatePaymentLinkHandlerFor(retryDb, actor, clock)
                    .Handle(command, default);
                Assert.NotEqual(oldLinkId, retry.PaymentLink!.LinkId);
            }
        }
        finally
        {
            if (triggerInstalled)
                await DropFailureConstraintAsync("checkout", "PaymentLinks", PaymentLinksFailureConstraintName);
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Sql_revoke_failure_rolls_back_link_order_and_claim()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            var created = await CreateIssuedOrderAsync(actor, clock, "issued-for-revoke");
            var originalVersion = created.Order.Version;

            await InstallFailureConstraintAsync(
                "checkout", "PaymentLinks", PaymentLinksFailureConstraintName, "[Status] <> 2");
            triggerInstalled = true;
            var command = new RevokePaymentLinkCommand(MerchantId, created.PaymentLink!.LinkId, "revoke-with-failure");
            Exception? failure;
            await using (var revokeDb = CreateRuntimeDb(actor))
            {
                failure = await Record.ExceptionAsync(() => new RevokePaymentLinkHandler(
                    new OrderRepository(revokeDb),
                    new OrderRepository(revokeDb),
                    new EfIdempotencyStore(revokeDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(revokeDb, NoOpSecurityTelemetry.Instance),
                    clock).Handle(command, default).AsTask());
            }

            Assert.NotNull(failure);
            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal((int)PaymentLinkStatus.Active, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Status FROM checkout.PaymentLinks WHERE Id=@link;",
                    ("@link", created.PaymentLink.LinkId))));
                Assert.Equal(originalVersion, Convert.ToInt64(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", created.Order.OrderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM txn.IdempotencyRecords
                    WHERE MerchantId=@merchant AND Context=N'payment-link.revoke';
                    """, ("@merchant", MerchantId))));
            }

            await DropFailureConstraintAsync("checkout", "PaymentLinks", PaymentLinksFailureConstraintName);
            triggerInstalled = false;
            await using (var retryDb = CreateRuntimeDb(actor))
            {
                var retry = await new RevokePaymentLinkHandler(
                    new OrderRepository(retryDb),
                    new OrderRepository(retryDb),
                    new EfIdempotencyStore(retryDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(retryDb, NoOpSecurityTelemetry.Instance),
                    clock).Handle(command, default);
                Assert.Equal(PaymentLinkStatus.Revoked, retry.Status);
            }
        }
        finally
        {
            if (triggerInstalled)
                await DropFailureConstraintAsync("checkout", "PaymentLinks", PaymentLinksFailureConstraintName);
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Sql_cancel_failure_rolls_back_order_link_and_claim()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            var created = await CreateIssuedOrderAsync(actor, clock, "issued-for-cancel");

            await InstallFailureConstraintAsync(
                "shop", "Orders", OrdersFailureConstraintName, "[Status] <> 6");
            triggerInstalled = true;
            var command = new CancelManagedOrderCommand(
                MerchantId, created.Order.OrderId, created.Order.Version, "cancel-with-failure", "cancel-with-failure");
            Exception? failure;
            await using (var cancelDb = CreateRuntimeDb(actor))
            {
                var repository = new OrderRepository(cancelDb);
                failure = await Record.ExceptionAsync(() => new CancelManagedOrderHandler(
                    repository,
                    repository,
                    new NoBlockingPaymentSessionProbe(),
                    new EfIdempotencyStore(cancelDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(cancelDb, NoOpSecurityTelemetry.Instance),
                    clock).Handle(command, default).AsTask());
            }

            Assert.NotNull(failure);
            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal((int)OrderStatus.Open, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Status FROM shop.Orders WHERE Id=@order;", ("@order", created.Order.OrderId))));
                Assert.Equal((int)PaymentLinkStatus.Active, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Status FROM checkout.PaymentLinks WHERE OrderId=@order AND Status=1;",
                    ("@order", created.Order.OrderId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM txn.IdempotencyRecords
                    WHERE MerchantId=@merchant AND Context=N'order.cancel';
                    """, ("@merchant", MerchantId))));
            }

            await DropFailureConstraintAsync("shop", "Orders", OrdersFailureConstraintName);
            triggerInstalled = false;
            await using (var retryDb = CreateRuntimeDb(actor))
            {
                var repository = new OrderRepository(retryDb);
                var retry = await new CancelManagedOrderHandler(
                    repository,
                    repository,
                    new NoBlockingPaymentSessionProbe(),
                    new EfIdempotencyStore(retryDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(retryDb, NoOpSecurityTelemetry.Instance),
                    clock).Handle(command, default);
                Assert.Equal(OrderStatus.Cancelled, retry.Order.OrderStatus);
            }
        }
        finally
        {
            if (triggerInstalled)
                await DropFailureConstraintAsync("shop", "Orders", OrdersFailureConstraintName);
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.1")]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Sql_cart_to_order_failure_rolls_back_order_items_outbox_and_cart()
    {
        await ResetDatabaseAsync();
        var triggerInstalled = false;
        try
        {
            await MigrateAsync();
            await using var merchant = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await IntegrationDb.InsertMerchantAsync(merchant, MerchantId, "transaction-integrity-cart");

            var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
            var clock = new FixedClock(Now);
            await IntegrationDb.ExecAsync(merchant, """
                INSERT merch.Users
                    (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                     DisplayName, FirstName, LastName, IdentityType)
                VALUES (@id, N'microsoft', @subject, @email, 2, @merchant, 1, @at,
                        N'Cart User', N'Cart', N'User', 1);
                """,
                ("@id", actor.UserId!.Value),
                ("@subject", $"transaction-integrity-{actor.UserId:N}"),
                ("@email", "cart-user@example.test"),
                ("@merchant", MerchantId),
                ("@at", Now));
            var metadata = new CommerceItemMetadata(
                CommerceItemMetadataCodec.InsuranceDocumentSource,
                "POLICY",
                "POL-1",
                new DateOnly(2026, 9, 1),
                new DateOnly(2027, 9, 1));
            var cart = new Carts.Domain.Cart(CartId, MerchantId, "SALE-1", Now);
            cart.AddItem("DOC-1", "SALE-1", "VMI", "Document", 1, Money.Of(100m, "THB"), metadata);
            var item = cart.Items.Single();
            await using (var seed = CreateRuntimeDb(actor))
            {
                seed.Carts.Add(cart);
                await seed.SaveChangesAsync();
            }

            var request = new DirectRequest(
                MerchantId,
                CartId,
                1,
                "SALE-1",
                CustomerContact.Of("Buyer", "0800000000", "buyer@example.test"),
                "promptpay",
                [new ProductSnapshot(item.Id, "DOC-1", "VMI", "Document", 1, metadata)],
                OrderInitiatingAudience.User,
                actor.UserId);
            await InstallOutboxFailureTriggerAsync();
            triggerInstalled = true;
            Exception? failure;
            await using (var coordinatorDb = CreateRuntimeDb(actor))
            {
                failure = await Record.ExceptionAsync(() => new DirectCoordinator(
                    null!,
                    null!,
                    new CartRepository(coordinatorDb),
                    new OrderRepository(coordinatorDb),
                    new FixedOrderNo("ORD6900000701"),
                    new EfOutbox(coordinatorDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(coordinatorDb, NoOpSecurityTelemetry.Instance),
                    clock,
                    new NoOpAuthorizationLocks(),
                    new AllowCapabilities()).CommitAsync(request, default));
            }

            Assert.NotNull(failure);
            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal("Open", Convert.ToString(await IntegrationDb.ScalarAsync(
                    verify, "SELECT Status FROM shop.Carts WHERE Id=@cart;", ("@cart", CartId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT COUNT(*) FROM shop.CartItems WHERE CartId=@cart;", ("@cart", CartId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT COUNT(*) FROM shop.OrderItems WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    verify, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
            }

            await DropOutboxFailureTriggerAsync();
            triggerInstalled = false;
            await using (var retryDb = CreateRuntimeDb(actor))
            {
                var retry = await new DirectCoordinator(
                    null!,
                    null!,
                    new CartRepository(retryDb),
                    new OrderRepository(retryDb),
                    new FixedOrderNo("ORD6900000701"),
                    new EfOutbox(retryDb, clock, actor),
                    new MerchantRuntimeUnitOfWork(retryDb, NoOpSecurityTelemetry.Instance),
                    clock,
                    new NoOpAuthorizationLocks(),
                    new AllowCapabilities()).CommitAsync(request, default);
                Assert.Equal(nameof(OrderStatus.Pending), retry.Status);
            }

            await using var afterRetry = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal("CheckedOut", Convert.ToString(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT Status FROM shop.Carts WHERE Id=@cart;", ("@cart", CartId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT COUNT(*) FROM shop.OrderItems WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                afterRetry, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", MerchantId))));
        }
        finally
        {
            if (triggerInstalled)
                await DropOutboxFailureTriggerAsync();
            await DropDatabaseAsync();
        }
    }

    private static CheckoutTransactionService CreateCheckoutService(
        MerchantRuntimeDbContext db,
        Connection connection,
        Guid credential,
        IPspAdapter adapter)
    {
        var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
        var repository = new OrderRepository(db);
        return new CheckoutTransactionService(
            repository,
            repository,
            new FixedRoute(connection.Id, credential),
            new FakeConnectionRepository(connection),
            new FakeAdapterFactory(adapter),
            new FakeVault(credential),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            new EfIdempotencyStore(db, new FixedClock(Now), actor),
            new EfOutbox(db, new FixedClock(Now), actor),
            new FixedClock(Now),
            new TransactionInquiryScheduler(),
            new FakeReturnBinding());
    }

    private static CreateOrderHandler CreateOrderHandlerFor(
        MerchantRuntimeDbContext db, IntegrationActor actor, FixedClock clock)
    {
        var repository = new OrderRepository(db);
        return new CreateOrderHandler(
            new IntegrationPricing(),
            new FixedOrderOwnerResolver(),
            repository,
            new OrderNoSequence(db, clock),
            new OrderLinkIssuer(new FixedTokenService(), repository, clock),
            new PaymentLinkReplayService(
                repository,
                repository,
                repository,
                new DataProtectedPaymentLinkReplayProtector(new EphemeralDataProtectionProvider()),
                clock),
            new EfIdempotencyStore(db, clock, actor),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            clock);
    }

    private static IssueOrderHandler IssueOrderHandlerFor(
        MerchantRuntimeDbContext db, IntegrationActor actor, FixedClock clock)
    {
        var repository = new OrderRepository(db);
        return new IssueOrderHandler(
            repository,
            new OrderLinkIssuer(new FixedTokenService(), repository, clock),
            new PaymentLinkReplayService(
                repository,
                repository,
                repository,
                new DataProtectedPaymentLinkReplayProtector(new EphemeralDataProtectionProvider()),
                clock),
            new EfIdempotencyStore(db, clock, actor),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            clock,
            outbox: new EfOutbox(db, clock, actor),
            notificationProtector: new DataProtectedPaymentLinkNotificationProtector(
                new EphemeralDataProtectionProvider()));
    }

    private static async Task<OrderCommandResult> CreateIssuedOrderAsync(
        IntegrationActor actor, FixedClock clock, string idempotencyKey)
    {
        await using var db = CreateRuntimeDb(actor);
        return await CreateOrderHandlerFor(db, actor, clock).Handle(
            new CreateOrderCommand(
                MerchantId,
                actor.UserId!.Value,
                "insurance",
                [new OrderItemRequest("trusted-product", 2)],
                new OrderOwnerRequest(null, null),
                IssueNow: true,
                IdempotencyKey: idempotencyKey),
            default);
    }

    private static RotatePaymentLinkHandler RotatePaymentLinkHandlerFor(
        MerchantRuntimeDbContext db, IntegrationActor actor, FixedClock clock)
    {
        var repository = new OrderRepository(db);
        return new RotatePaymentLinkHandler(
            repository,
            repository,
            new OrderLinkIssuer(new DistinctTokenService(), repository, clock),
            new PaymentLinkReplayService(
                repository,
                repository,
                repository,
                new DataProtectedPaymentLinkReplayProtector(new EphemeralDataProtectionProvider()),
                clock),
            new EfIdempotencyStore(db, clock, actor),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            clock);
    }

    private static async Task MigrateAndSeedCheckoutAsync()
    {
        await MigrateAsync();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.InsertMerchantAsync(connection, MerchantId, "transaction-integrity-checkout");
        await IntegrationDb.ExecAsync(connection, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000601', 8, 1, @at, @at, 2, 1, @at, @at,
                    NULL, NULL, N'Probe customer', '0800000000', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode, VariantName, Metadata,
                 DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency,
                 TaxAmount, TaxCurrency, LineAmount, LineCurrency)
            VALUES (@item, @order, @merchant, 1, N'DOC-1', 'VMI', N'Document', NULL,
                    0.00, 'THB', 100.00, 'THB', 0.00, 'THB', 100.00, 'THB');
            INSERT checkout.PaymentLinks
                (Id, OrderId, MerchantId, TokenHash, Status, CreatedAt, ExpiresAt, Version)
            VALUES (@link, @order, @merchant, @hash, 1, @at, DATEADD(hour, 1, @at), 1);
            """,
            ("@order", CheckoutOrderId), ("@item", CheckoutItemId), ("@merchant", MerchantId),
            ("@link", CheckoutLinkId), ("@hash", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ("@at", Now));
    }

    private static async Task MigrateAndSeedResultAsync(Guid providerAccountId, Guid credential)
    {
        await MigrateAsync();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.InsertMerchantAsync(connection, MerchantId, "transaction-integrity-result");
        await IntegrationDb.ExecAsync(connection, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000611', 8, 2, @at, @at, 3, 1, @at, @at,
                    NULL, NULL, N'Result customer', '0800000000', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode, VariantName, Metadata,
                 DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency,
                 TaxAmount, TaxCurrency, LineAmount, LineCurrency)
            VALUES (@item, @order, @merchant, 1, N'DOC-1', 'VMI', N'Document', NULL,
                    0.00, 'THB', 100.00, 'THB', 0.00, 'THB', 100.00, 'THB');
            INSERT txn.Transactions
                (Id, MerchantId, OrderId, TransactionNo, AttemptNo,
                 AmountAmount, AmountCurrency, PaymentMethod, Provider, ProviderAccountId,
                 Environment, CredentialVersionId, ConfigurationVersion,
                 ProviderRequestReference, ProviderReference, RedirectUrl, ReturnBinding,
                 Status, ProviderStatus, OrderSnapshot, SafeProviderMetadata, NeedsReview, ReviewCode,
                 CreatedAt, UpdatedAt, SucceededAt, LastInquiryAt, NextInquiryAt, InquiryAttempts, Version)
            VALUES (@transaction, @merchant, @order, N'TXN-INTEGRITY-RESULT', 1,
                    100.00, 'THB', 'card', 1, @provider, 1, @credential, 1,
                    N'request-integrity-result', N'charge-integrity-result', N'https://psp.example/redirect', NULL,
                    1, N'redirect_created', N'{"schemaVersion":1,"provenance":"CAPTURED_AT_CONFIRM"}',
                    NULL, 0, NULL, @at, @at, NULL, NULL, NULL, 0, 1);
            """,
            ("@order", ResultOrderId), ("@item", ResultItemId), ("@merchant", MerchantId),
            ("@transaction", ResultTransactionId), ("@provider", providerAccountId),
            ("@credential", credential), ("@at", Now));
    }

    private static async Task MigrateAsync()
    {
        await using var migration = CreateMigrationContext();
        await migration.Database.MigrateAsync();
    }

    private static async Task InstallOutboxFailureTriggerAsync()
        => await InstallFailureTriggerAsync(
            "txn", "OutboxMessages", FailureTriggerName, "INSERT",
            "transaction-integrity injected outbox failure");

    private static async Task DropOutboxFailureTriggerAsync()
        => await DropFailureTriggerAsync("txn", FailureTriggerName);

    private static async Task InstallFailureTriggerAsync(
        string schema, string table, string triggerName, string action, string message)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(connection, $"""
            CREATE OR ALTER TRIGGER [{schema}].[{triggerName}]
            ON [{schema}].[{table}]
            AFTER {action}
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51002, N'{message}', 1;
            END;
            """);
    }

    private static async Task DropFailureTriggerAsync(string schema, string triggerName)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(connection, $"""
            IF OBJECT_ID(N'{schema}.{triggerName}', N'TR') IS NOT NULL
                DROP TRIGGER [{schema}].[{triggerName}];
            """);
    }

    private static async Task InstallFailureConstraintAsync(
        string schema, string table, string constraintName, string predicate)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(connection, $"""
            ALTER TABLE [{schema}].[{table}] WITH NOCHECK
            ADD CONSTRAINT [{constraintName}] CHECK ({predicate});
            """);
    }

    private static async Task DropFailureConstraintAsync(
        string schema, string table, string constraintName)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(connection, $"""
            IF EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE name = N'{constraintName}'
                  AND parent_object_id = OBJECT_ID(N'{schema}.{table}'))
                ALTER TABLE [{schema}].[{table}] DROP CONSTRAINT [{constraintName}];
            """);
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

    private static MerchantRuntimeDbContext CreateRuntimeDb(IActorContext actor) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
            .Options,
        actor,
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

    private static Connection NewConnection(Guid credential)
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, "card", "transaction-integrity/secret", Now);
        connection.SetInitialSecretVersion(credential, PspEnvironment.Sandbox);
        return connection;
    }

    private static bool IsNull(object? value) => value is null or DBNull;

    private sealed class IntegrationPricing : ITrustedOrderPricingSource
    {
        public Task<TrustedOrderPricing> PriceAsync(
            Guid merchantId,
            string businessType,
            IReadOnlyList<OrderItemRequest> requestedItems,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TrustedOrderPricing(
                "THB",
                [new TrustedOrderLineInput(
                    "SQL-DOC", "VMI", "trusted", 2,
                    Money.Of(100m, "THB"), Money.Of(10m, "THB"), Money.Of(5m, "THB"),
                    Money.Of(195m, "THB"), "catalog-v1")],
                Money.Of(3m, "THB"), Money.Of(2m, "THB")));
    }

    private sealed class FixedOrderOwnerResolver : IOrderOwnerResolver
    {
        public Task<ResolvedOrderOwner> ResolveAsync(
            Guid merchantId,
            Guid accountId,
            OrderOwnerRequest requested,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedOrderOwner(requested.OwnerSaleId, requested.OwnerBranchId));
    }

    private sealed class FixedTokenService : IPaymentLinkTokenService
    {
        private static readonly byte[] Digest = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        public PaymentLinkToken Mint() => new("transaction-integrity-token", Digest.ToArray());
        public byte[] Hash(string rawToken) => Digest.ToArray();
    }

    private sealed class DistinctTokenService : IPaymentLinkTokenService
    {
        private static readonly byte[] Digest = Enumerable.Range(33, 32).Select(i => (byte)i).ToArray();

        public PaymentLinkToken Mint() => new("transaction-integrity-rotated-token", Digest.ToArray());
        public byte[] Hash(string rawToken) => Digest.ToArray();
    }

    private sealed class ResultAdapter(PspChargeStatus status, Money amount) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>(["card"]);
        public int FetchCalls { get; private set; }

        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session,
            Guid pspConnectionId,
            string secret,
            PspEnvironment environment,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PspCharge("unused", "https://psp.example/redirect"));

        public Task<PspProbeResult> TestConnectionAsync(
            string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspProbeResult("ok", "transaction-integrity"));

        public bool VerifyWebhook(string rawPayload, string signature, string secret) => true;
        public WebhookEvent ParseWebhook(string rawPayload) => new("event", rawPayload, status);
        public PspWebhookReference ExtractWebhookReference(string rawPayload) => new("event", rawPayload);

        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId,
            string secret,
            PspEnvironment environment,
            CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(new PspChargeConfirmation(status, amount));
        }
    }

    private sealed class TransactionBoundaryProbeAdapter(MerchantRuntimeDbContext db) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>(["card"]);
        public int CreateCalls { get; private set; }
        public int FetchCalls { get; private set; }
        public bool CreateObservedNoActiveTransaction { get; private set; }
        public bool CreateObservedCommittedIntent { get; private set; }
        public bool FetchObservedNoActiveTransaction { get; private set; }
        public bool FetchObservedCommittedIntent { get; private set; }

        public async Task<PspCharge> CreateRedirectChargeAsync(
            Session session,
            Guid pspConnectionId,
            string secret,
            PspEnvironment environment,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            CreateObservedNoActiveTransaction = db.Database.CurrentTransaction is null;
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.Status, t.OrderSnapshot, o.PaymentStatus
                FROM txn.Transactions t
                JOIN shop.Orders o ON o.Id = t.OrderId AND o.MerchantId = t.MerchantId
                WHERE t.Id = @transaction AND o.Id = @order;
                """;
            command.Parameters.AddWithValue("@transaction", session.Id);
            command.Parameters.AddWithValue("@order", session.OrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                CreateObservedCommittedIntent =
                    reader.GetInt32(0) == (int)TransactionStatus.Created
                    && reader.GetInt32(2) == (int)PaymentStatus.Processing
                    && reader.GetString(1).Contains("CAPTURED_AT_CONFIRM", StringComparison.Ordinal);
            }
            return new PspCharge("probe-charge", "https://psp.example/probe");
        }

        public Task<PspProbeResult> TestConnectionAsync(
            string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspProbeResult("ok", "transaction-integrity"));

        public bool VerifyWebhook(string rawPayload, string signature, string secret) => true;
        public WebhookEvent ParseWebhook(string rawPayload) => new("event", rawPayload, PspChargeStatus.Paid);
        public PspWebhookReference ExtractWebhookReference(string rawPayload) => new("event", rawPayload);

        public async Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId,
            string secret,
            PspEnvironment environment,
            CancellationToken cancellationToken)
        {
            FetchCalls++;
            FetchObservedNoActiveTransaction = db.Database.CurrentTransaction is null;
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.Status, t.OrderSnapshot, o.PaymentStatus
                FROM txn.Transactions t
                JOIN shop.Orders o ON o.Id = t.OrderId AND o.MerchantId = t.MerchantId
                WHERE t.ProviderReference = @reference;
                """;
            command.Parameters.AddWithValue("@reference", externalChargeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                FetchObservedCommittedIntent =
                    reader.GetInt32(0) == (int)TransactionStatus.Created
                    && reader.GetInt32(2) == (int)PaymentStatus.Processing
                    && reader.GetString(1).Contains("CAPTURED_AT_CONFIRM", StringComparison.Ordinal);
            }
            return new PspChargeConfirmation(PspChargeStatus.Paid, Money.Of(100m, "THB"));
        }
    }

    private sealed class FixedRoute(Guid connectionId, Guid credential) : IPaymentRouteSelector
    {
        public Task<PspRouteSelection> SelectAsync(
            Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken) =>
            Task.FromResult(new PspRouteSelection(connectionId, Code.TwoCTwoP, credential, PspEnvironment.Sandbox));
    }

    private sealed class FakeConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(connection);

        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(id == connection.Id ? connection : null);

        public void Add(Connection value) { }

        public Task<IReadOnlyList<Connection>> ListByTenantAsync(
            Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Connection>>([connection]);
    }

    private sealed class FakeAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter;
    }

    private sealed class FakeVault(Guid credential) : IVaultSecretStore
    {
        public Task<string> ReadVersionForServerAsync(
            Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
            versionId == credential
                ? Task.FromResult("transaction-integrity-secret")
                : throw new InvalidOperationException("Unexpected credential version.");

        public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReturnBinding : ITransactionReturnBindingService
    {
        public string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt) =>
            "transaction-integrity-return";

        public bool TryRead(string value, out TransactionReturnBinding binding)
        {
            binding = default!;
            return false;
        }
    }

    private sealed class NoBlockingPaymentSessionProbe : IPaymentSessionProbe
    {
        public Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class FixedOrderNo(string value) : IOrderNoSequence
    {
        public Task<string> NextAsync(CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class NoOpAuthorizationLocks : IPaymentAuthorizationLockManager
    {
        public Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllowCapabilities : IEffectivePaymentCapabilityResolver
    {
        public Task<PaymentMethodDecision> ResolveMethodAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult(new PaymentMethodDecision(true, request.Method, PaymentCapabilityDenial.None, Guid.NewGuid()));

        public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
            PaymentCapabilitySubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentMethod>>([]);

        public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentOption>>([]);
    }

    private sealed class IntegrationActor(Guid merchantId, Guid? userId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => userId;
        public bool HasActor => true;
        public string? SaleCode => null;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
