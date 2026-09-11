using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Orders.Domain;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.ControlPlane.Orders;
using Persistence.MerchantRuntime.Orders;
using SharedKernel;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "OrdersLinks")]
public sealed class Task5OrdersLinksSqlIntegrationTests
{
    private const string DatabaseName = "PolOrdersLinksTask5Test";

    [Fact]
    [Trait("Requirement", "REQ-6.1")]
    [Trait("Requirement", "REQ-6.6")]
    [Trait("Requirement", "REQ-6.12")]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.3")]
    public async Task Fresh_chain_preserves_legacy_orders_and_creates_hashed_link_contract()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var context = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await context.Database.MigrateAsync("20260910044722_Task4AgentRegistration");

            var legacyOrderId = Guid.NewGuid();
            var legacyItemId = Guid.NewGuid();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                await IntegrationDb.ExecAsync(seed, """
                    INSERT shop.Orders
                        (Id, MerchantId, OrderNo, Status, CreatedAt, SummaryToken, SummaryTokenExpiresAt,
                         CustomerName, CustomerPhone, AmountAmount, AmountCurrency)
                    VALUES
                        (@id, @merchant, @orderNo, 1, '2026-09-10T08:00:00', @token,
                         '2026-09-13T08:00:00', N'Legacy customer', '0800000000', 150.00, 'THB');
                    INSERT shop.OrderItems
                        (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode,
                         DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency)
                    VALUES
                        (@item, @id, @merchant, 2, N'LEGACY-1', 'VMI', 10.00, 'THB', 80.00, 'THB');
                    """,
                    ("@id", legacyOrderId), ("@item", legacyItemId), ("@merchant", IntegrationDb.MerchantA),
                    ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"),
                    ("@token", Guid.NewGuid().ToString("N")));
            }

            await context.Database.MigrateAsync();

            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await AssertHistoryAsync(connection, context);
            await AssertLegacyBackfillAsync(connection, legacyOrderId, legacyItemId);
            await AssertLinkSchemaAsync(connection);
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
    [Trait("Requirement", "REQ-7.1")]
    public async Task Real_sql_create_issue_and_unique_hash_rollback_keep_order_and_link_atomic()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migrationContext.Database.MigrateAsync();

            var actor = new IntegrationActor(IntegrationDb.MerchantA, Guid.NewGuid());
            var clock = new IntegrationClock(new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc));
            await using var db = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var controlPlane = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            var repository = new OrderRepository(db);
            var links = repository;
            var pricing = new IntegrationPricing();
            var owners = new OrderOwnerResolver(controlPlane, actor);
            var tokenService = new FixedTokenService();
            var issuer = new OrderLinkIssuer(tokenService, links, clock);
            var unitOfWork = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
            var idempotency = new EfIdempotencyStore(db, clock, actor);
            var sequence = new OrderNoSequence(db, clock);
            var replayProtector = new DataProtectedPaymentLinkReplayProtector(
                new EphemeralDataProtectionProvider());
            var replays = new PaymentLinkReplayService(
                repository, links, repository, replayProtector, clock);

            var create = new CreateOrderHandler(
                pricing, owners, repository, sequence, issuer, replays, idempotency, unitOfWork, clock);
            var command = new CreateOrderCommand(
                IntegrationDb.MerchantA,
                actor.UserId!.Value,
                "insurance",
                [new OrderItemRequest("trusted-product", 2)],
                new OrderOwnerRequest(null, null),
                true,
                "sql-create-1");

            var first = await create.Handle(command, default);
            Assert.Equal(OrderStatus.Open, first.Order.OrderStatus);
            Assert.Equal(PaymentStatus.Unpaid, first.Order.PaymentStatus);
            Assert.NotNull(first.PaymentLink);
            Assert.NotNull(first.RawToken);

            var duplicateToken = await Assert.ThrowsAsync<ConflictException>(() => create.Handle(
                command with { IdempotencyKey = "sql-create-2" }, default).AsTask());
            Assert.Contains("unique", duplicateToken.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            Assert.Equal(1, await verify.Orders.CountAsync(x => x.MerchantId == IntegrationDb.MerchantA));
            Assert.Equal(1, await verify.PaymentLinks.CountAsync(x => x.MerchantId == IntegrationDb.MerchantA));
            // The Order path namespaces the client key by merchant + operation before it reaches the
            // claim store, so the persisted primary key is the scoped form, not the raw client value.
            Assert.Equal(1, await verify.IdempotencyRecords.CountAsync(
                x => x.Key == $"{IntegrationDb.MerchantA:D}:order.create:sql-create-1"));
            Assert.Equal(0, await verify.IdempotencyRecords.CountAsync(
                x => x.Key == $"{IntegrationDb.MerchantA:D}:order.create:sql-create-2"));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-6.1")]
    public async Task Real_sql_two_draft_orders_persist_without_summary_token_index_collision()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migrationContext.Database.MigrateAsync();

            var actor = new IntegrationActor(IntegrationDb.MerchantA, Guid.NewGuid());
            var clock = new IntegrationClock(new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc));
            await using var db = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var controlPlane = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            var repository = new OrderRepository(db);
            var replayProtector = new DataProtectedPaymentLinkReplayProtector(
                new EphemeralDataProtectionProvider());
            var replays = new PaymentLinkReplayService(
                repository, repository, repository, replayProtector, clock);
            var create = new CreateOrderHandler(
                new IntegrationPricing(),
                new OrderOwnerResolver(controlPlane, actor),
                repository,
                new OrderNoSequence(db, clock),
                new OrderLinkIssuer(new FixedTokenService(), repository, clock),
                replays,
                new EfIdempotencyStore(db, clock, actor),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                clock);

            // Two DRAFT orders (issueNow=false) never mint a PaymentLink, so the only column that could
            // collide is SummaryToken. CreateDraft must leave it NULL — an empty-string sentinel lands
            // inside the filtered unique index IX_Orders_SummaryToken (WHERE [SummaryToken] IS NOT NULL)
            // and the second SaveChanges throws SqlException 2627. Both drafts must persist.
            CreateOrderCommand Draft(string key) => new(
                IntegrationDb.MerchantA,
                actor.UserId!.Value,
                "insurance",
                [new OrderItemRequest("trusted-product", 2)],
                new OrderOwnerRequest(null, null),
                false,
                key);

            var firstDraft = await create.Handle(Draft("draft-1"), default);
            Assert.Equal(OrderStatus.Draft, firstDraft.Order.OrderStatus);
            Assert.Null(firstDraft.PaymentLink);

            var secondDraft = await create.Handle(Draft("draft-2"), default);
            Assert.Equal(OrderStatus.Draft, secondDraft.Order.OrderStatus);
            Assert.Null(secondDraft.PaymentLink);

            await using var verify = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            Assert.Equal(2, await verify.Orders.CountAsync(x => x.MerchantId == IntegrationDb.MerchantA));
            Assert.Equal(0, await verify.Orders.CountAsync(
                x => x.MerchantId == IntegrationDb.MerchantA && x.SummaryToken != null));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Real_sql_idempotency_key_is_scoped_by_merchant_and_operation()
    {
        await ResetDatabaseAsync();
        try
        {
            await using (var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName)))
                await migrationContext.Database.MigrateAsync();

            var clock = new IntegrationClock(new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc));
            const string sharedKey = "shared-idem-key";

            MerchantRuntimeDbContext Runtime(IActorContext actor) => new(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170)).Options,
                actor, AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

            // Drive the real EfIdempotencyStore (single-column primary key) through the guard on real SQL.
            var actorA = new IntegrationActor(IntegrationDb.MerchantA, Guid.NewGuid());
            var actorB = new IntegrationActor(IntegrationDb.MerchantB, Guid.NewGuid());
            await using var dbA = Runtime(actorA);
            await using var dbB = Runtime(actorB);
            var storeA = new EfIdempotencyStore(dbA, clock, actorA);
            var storeB = new EfIdempotencyStore(dbB, clock, actorB);

            // Merchant A claims the key for order.issue.
            await OrderCommandGuards.RequireFirstDeliveryAsync(storeA, IntegrationDb.MerchantA, sharedKey, "order.issue", default);
            // Merchant B reuses the SAME raw key: before the fix the global PK collided and this was a
            // false 409; after namespacing it is Merchant B's own first delivery and must succeed.
            await OrderCommandGuards.RequireFirstDeliveryAsync(storeB, IntegrationDb.MerchantB, sharedKey, "order.issue", default);
            // Merchant A reuses the SAME raw key on a DIFFERENT operation: also a first delivery.
            await OrderCommandGuards.RequireFirstDeliveryAsync(storeA, IntegrationDb.MerchantA, sharedKey, "payment-link.rotate", default);

            // Only the exact merchant + operation + key triple is a replay.
            await Assert.ThrowsAsync<ConflictException>(() =>
                OrderCommandGuards.RequireFirstDeliveryAsync(storeA, IntegrationDb.MerchantA, sharedKey, "order.issue", default));

            await using var verify = Runtime(UnboundActor.Instance);
            Assert.Equal(1, await verify.IdempotencyRecords.IgnoreQueryFilters()
                .CountAsync(x => x.Key == $"{IntegrationDb.MerchantA:D}:order.issue:{sharedKey}"));
            Assert.Equal(1, await verify.IdempotencyRecords.IgnoreQueryFilters()
                .CountAsync(x => x.Key == $"{IntegrationDb.MerchantB:D}:order.issue:{sharedKey}"));
            Assert.Equal(1, await verify.IdempotencyRecords.IgnoreQueryFilters()
                .CountAsync(x => x.Key == $"{IntegrationDb.MerchantA:D}:payment-link.rotate:{sharedKey}"));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.3")]
    [Trait("Requirement", "REQ-6.4")]
    [Trait("Requirement", "REQ-6.5")]
    public async Task Real_sql_agent_owner_comes_from_sale_code_and_cross_merchant_owner_is_denied()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migrationContext.Database.MigrateAsync();

            var branchId = Guid.NewGuid();
            var saleId = Guid.NewGuid();
            var branchBId = Guid.NewGuid();
            var saleBId = Guid.NewGuid();
            var branchCode = $"task5-branch-{Guid.NewGuid():N}";
            var saleCodeA = $"task5-sale-{Guid.NewGuid():N}";
            var branchBCode = $"task5-branch-b-{Guid.NewGuid():N}";
            var saleBCode = $"task5-sale-b-{Guid.NewGuid():N}";
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                await IntegrationDb.InsertMerchantAsync(
                    seed, IntegrationDb.MerchantA, $"task5-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.InsertMerchantAsync(
                    seed, IntegrationDb.MerchantB, $"task5-b-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.ExecAsync(seed, """
                    INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                    VALUES (@branch, @merchant, @branchCode, N'Task5 branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1),
                           (@branchB, @merchantB, @branchBCode, N'Task5 branch B', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                    VALUES (@sale, @merchant, @branch, @saleCode, N'Task5 sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1),
                           (@saleB, @merchantB, @branchB, @saleBCode, N'Task5 sale B', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    """,
                    ("@branch", branchId), ("@sale", saleId), ("@merchant", IntegrationDb.MerchantA),
                    ("@branchCode", branchCode), ("@saleCode", saleCodeA), ("@branchB", branchBId),
                    ("@saleB", saleBId), ("@merchantB", IntegrationDb.MerchantB),
                    ("@branchBCode", branchBCode), ("@saleBCode", saleBCode));
            }

            var accountId = Guid.NewGuid();
            var agent = new IntegrationActor(IntegrationDb.MerchantA, accountId, saleCodeA);
            await using var db = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                agent,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var controlPlane = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                agent,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);

            // Load the exact normalized code through the actor-side master query before resolving ownership.
            var resolvedSaleCode = await controlPlane.Sales.AsNoTracking()
                .Where(x => x.Id == saleId)
                .Select(x => x.Code)
                .SingleAsync();
            await using var resolvedControlPlane = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                new IntegrationActor(IntegrationDb.MerchantA, accountId, resolvedSaleCode),
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            var resolver = new OrderOwnerResolver(
                resolvedControlPlane,
                new IntegrationActor(IntegrationDb.MerchantA, accountId, resolvedSaleCode));
            var derived = await resolver.ResolveAsync(
                IntegrationDb.MerchantA,
                accountId,
                new OrderOwnerRequest(Guid.NewGuid(), Guid.NewGuid()),
                default);

            Assert.Equal(saleId, derived.OwnerSaleId);
            Assert.Equal(branchId, derived.OwnerBranchId);

            var merchantScoped = new OrderOwnerResolver(
                resolvedControlPlane,
                new IntegrationActor(IntegrationDb.MerchantA, accountId));
            await Assert.ThrowsAsync<AccessDeniedException>(() => merchantScoped.ResolveAsync(
                IntegrationDb.MerchantA,
                accountId,
                new OrderOwnerRequest(saleBId, null),
                default));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.4")]
    [Trait("Requirement", "REQ-7.5")]
    public async Task Real_sql_customer_reader_resolves_hash_and_redacts_internal_order_fields()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migrationContext.Database.MigrateAsync();

            var orderId = Guid.NewGuid();
            var linkId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var tokenService = new PaymentLinkTokenService(new byte[32]);
            var token = tokenService.Mint();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                await IntegrationDb.InsertMerchantAsync(
                    seed, IntegrationDb.MerchantA, $"task5-reader-{Guid.NewGuid():N}"[..24]);
                await IntegrationDb.ExecAsync(seed, """
                    INSERT shop.Orders
                        (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                         IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                         CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                         SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                         OrderChargeAmount, OrderChargeCurrency)
                    VALUES
                        (@order, @merchant, N'ORD6900000091', 8, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 2,
                         1, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, NULL,
                         N'private customer', N'0800000000', 194.00, 'THB',
                         195.00, 'THB', 3.00, 'THB', 2.00, 'THB');
                    INSERT shop.OrderItems
                        (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode, VariantName, Metadata,
                         DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency,
                         TaxAmount, TaxCurrency, LineAmount, LineCurrency)
                    VALUES
                        (@item, @order, @merchant, 2, N'DOC-1', 'VMI', N'Visible name', N'{"internal":"must-not-read"}',
                         10.00, 'THB', 100.00, 'THB', 5.00, 'THB', 195.00, 'THB');
                    INSERT checkout.PaymentLinks
                        (Id, OrderId, MerchantId, TokenHash, Status, CreatedAt, ExpiresAt, Version)
                    VALUES
                        (@link, @order, @merchant, @hash, 1, SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()), 1);
                    """,
                    ("@order", orderId), ("@item", itemId), ("@link", linkId),
                    ("@merchant", IntegrationDb.MerchantA), ("@hash", token.Hash));
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                UnboundActor.Instance,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance));
            services.AddScoped<ICustomerCheckoutReader, CustomerCheckoutReader>();
            using var provider = services.BuildServiceProvider();
            var reader = provider.GetRequiredService<ICustomerCheckoutReader>();

            var link = await reader.FindByHashAsync(token.Hash, default);
            Assert.NotNull(link);
            Assert.Equal(orderId, link.OrderId);
            Assert.Equal(2, link.OrderVersion);

            var summary = await reader.GetSummaryAsync(orderId, linkId, default);
            Assert.NotNull(summary);
            Assert.Equal("ORD6900000091", summary.OrderNo);
            Assert.Equal("probe", summary.MerchantName);
            Assert.Equal(194m, summary.TotalAmount.Amount);
            var line = Assert.Single(summary.Lines);
            Assert.Equal("DOC-1", line.ProductCode);
            Assert.Equal(195m, line.LineAmount.Amount);
            Assert.DoesNotContain("Metadata", summary.GetType().GetProperties().Select(x => x.Name));
            Assert.DoesNotContain("OwnerSaleId", summary.GetType().GetProperties().Select(x => x.Name));
            Assert.DoesNotContain("PaymentSessionId", summary.GetType().GetProperties().Select(x => x.Name));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.2")]
    [Trait("Requirement", "REQ-7.3")]
    public async Task Real_sql_rotate_revokes_one_link_and_replay_returns_protected_token_without_new_order()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migrationContext = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migrationContext.Database.MigrateAsync();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
                await IntegrationDb.InsertMerchantAsync(
                    seed, IntegrationDb.MerchantA, $"task5-rotate-{Guid.NewGuid():N}"[..24]);

            var actor = new IntegrationActor(IntegrationDb.MerchantA, Guid.NewGuid());
            // The replay protector uses ASP.NET Data Protection's real UTC clock. Keep the
            // injected domain clock on the same timeline so a protected replay cannot be
            // expired by a stale fixture timestamp.
            var clock = new IntegrationClock(DateTime.UtcNow);
            await using var db = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var controlPlane = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                actor,
                AllowAllWriteAuthorizer.Instance,
                NoOpSecurityTelemetry.Instance);
            var repository = new OrderRepository(db);
            var protector = new EphemeralDataProtectionProvider();
            var keyring = new VaultKeyring("test", new Dictionary<string, byte[]>
            {
                ["test"] = new byte[32],
            });
            var tokenService = new DataProtectedPaymentLinkTokenService(keyring);
            var linkStore = repository;
            var replays = new PaymentLinkReplayService(
                repository,
                linkStore,
                repository,
                new DataProtectedPaymentLinkReplayProtector(protector),
                clock);
            var create = new CreateOrderHandler(
                new IntegrationPricing(),
                new OrderOwnerResolver(controlPlane, actor),
                repository,
                new OrderNoSequence(db, clock),
                new OrderLinkIssuer(tokenService, linkStore, clock),
                replays,
                new EfIdempotencyStore(db, clock, actor),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                clock);
            var created = await create.Handle(new CreateOrderCommand(
                IntegrationDb.MerchantA,
                actor.UserId!.Value,
                "insurance",
                [new OrderItemRequest("trusted-product", 2)],
                new OrderOwnerRequest(null, null),
                true,
                "rotate-create"), default);
            var oldLink = created.PaymentLink!;
            var rotate = new RotatePaymentLinkHandler(
                repository,
                linkStore,
                new OrderLinkIssuer(tokenService, linkStore, clock),
                replays,
                new EfIdempotencyStore(db, clock, actor),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                clock);
            var rotated = await rotate.Handle(new RotatePaymentLinkCommand(
                IntegrationDb.MerchantA,
                created.Order.OrderId,
                created.Order.Version,
                "rotate-link"), default);
            var replayed = await rotate.Handle(new RotatePaymentLinkCommand(
                IntegrationDb.MerchantA,
                created.Order.OrderId,
                created.Order.Version,
                "rotate-link"), default);

            Assert.NotEqual(oldLink.LinkId, rotated.PaymentLink!.LinkId);
            Assert.Equal(rotated.RawToken, replayed.RawToken);
            Assert.True(replayed.Replayed);

            var changed = await Assert.ThrowsAsync<ConflictException>(() => rotate.Handle(
                new RotatePaymentLinkCommand(
                    IntegrationDb.MerchantA,
                    created.Order.OrderId,
                    created.Order.Version + 1,
                    "rotate-link"), default).AsTask());
            Assert.Equal("idempotency_conflict", changed.Code);

            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM shop.Orders WHERE MerchantId = @merchant;
                """, ("@merchant", IntegrationDb.MerchantA))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM checkout.PaymentLinks WHERE MerchantId = @merchant;
                """, ("@merchant", IntegrationDb.MerchantA))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM checkout.PaymentLinks WHERE MerchantId = @merchant AND Status = 1;
                """, ("@merchant", IntegrationDb.MerchantA))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM checkout.PaymentLinks WHERE MerchantId = @merchant AND Status = 2;
                """, ("@merchant", IntegrationDb.MerchantA))));
            var protectedToken = Convert.ToString(await IntegrationDb.ScalarAsync(connection, """
                SELECT ProtectedRawToken FROM checkout.PaymentLinkReplays
                WHERE MerchantId = @merchant AND Operation = N'payment-link.rotate';
                """, ("@merchant", IntegrationDb.MerchantA)));
            Assert.NotNull(protectedToken);
            Assert.NotEqual(rotated.RawToken, protectedToken);
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.tables
                WHERE name IN (N'Payment', N'Payments');
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.tables
                WHERE name = N'Transactions' AND schema_id = SCHEMA_ID(N'txn');
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM txn.PaymentSessions;
                """)));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static async Task AssertHistoryAsync(SqlConnection connection, PolDbContext context)
    {
        var applied = (await ReadStringsAsync(connection, """
            SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
            """)).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("20260910032407_Task2AccountAuthorizationLease", applied);
        Assert.Contains("20260910035334_Task3MerchantMaster", applied);
        Assert.Contains("20260910044722_Task4AgentRegistration", applied);
        Assert.Contains("20260910060757_Task5OrdersLinks", applied);
        Assert.Equal(context.Database.GetMigrations().Count(), applied.Count);
    }

    private static async Task AssertLegacyBackfillAsync(
        SqlConnection connection, Guid orderId, Guid itemId)
    {
        await using var order = connection.CreateCommand();
        order.CommandText = """
            SELECT PaymentStatus, SubtotalAmount, SubtotalCurrency,
                   OrderDiscountCurrency, OrderChargeCurrency, SummaryToken, SummaryTokenExpiresAt
            FROM shop.Orders WHERE Id = @id;
            """;
        order.Parameters.AddWithValue("@id", orderId);
        await using var reader = await order.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(150m, reader.GetDecimal(1));
        Assert.Equal("THB", reader.GetString(2));
        Assert.Equal("THB", reader.GetString(3));
        Assert.Equal("THB", reader.GetString(4));
        Assert.False(reader.IsDBNull(5));
        Assert.False(reader.IsDBNull(6));
        await reader.DisposeAsync();

        await using var item = connection.CreateCommand();
        item.CommandText = """
            SELECT LineAmount, LineCurrency, TaxAmount, TaxCurrency
            FROM shop.OrderItems WHERE Id = @id;
            """;
        item.Parameters.AddWithValue("@id", itemId);
        await using var itemReader = await item.ExecuteReaderAsync();
        Assert.True(await itemReader.ReadAsync());
        Assert.Equal(150m, itemReader.GetDecimal(0));
        Assert.Equal("THB", itemReader.GetString(1));
        Assert.Equal(0m, itemReader.GetDecimal(2));
        Assert.Equal("THB", itemReader.GetString(3));
        await itemReader.DisposeAsync();

        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT is_nullable FROM sys.columns
            WHERE object_id = OBJECT_ID(N'shop.Orders') AND name = N'SummaryToken';
            """)));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT is_nullable FROM sys.columns
            WHERE object_id = OBJECT_ID(N'shop.Orders') AND name = N'SummaryTokenExpiresAt';
            """)));
    }

    private static async Task AssertLinkSchemaAsync(SqlConnection connection)
    {
        Assert.NotNull(await IntegrationDb.ScalarAsync(connection,
            "SELECT OBJECT_ID(N'checkout.PaymentLinks', N'U');"));
        Assert.NotNull(await IntegrationDb.ScalarAsync(connection,
            "SELECT OBJECT_ID(N'checkout.PaymentLinkReplays', N'U');"));

        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.tables
            WHERE name IN (N'Payment', N'Payments') AND schema_id = SCHEMA_ID(N'checkout');
            """)));

        Assert.Equal(32, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT max_length FROM sys.columns
            WHERE object_id = OBJECT_ID(N'checkout.PaymentLinks') AND name = N'TokenHash';
            """)));
        Assert.Equal("binary", Convert.ToString(await IntegrationDb.ScalarAsync(connection, """
            SELECT t.name FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'checkout.PaymentLinks') AND c.name = N'TokenHash';
            """)));
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'checkout.PaymentLinks')
              AND name IN (N'RawToken', N'Token', N'PlainToken');
            """)));
        Assert.Equal("nvarchar(max)", Convert.ToString(await IntegrationDb.ScalarAsync(connection, """
            SELECT t.name + CASE WHEN c.max_length = -1 THEN N'(max)' ELSE N'' END
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'checkout.PaymentLinkReplays')
              AND c.name = N'ProtectedRawToken';
            """)));
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'checkout.PaymentLinkReplays')
              AND name IN (N'RawToken', N'Token', N'PlainToken');
            """)));

        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT CASE WHEN is_unique = 1 AND filter_definition LIKE N'%Status%' THEN 1 ELSE 0 END
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'checkout.PaymentLinks')
              AND name = N'IX_PaymentLinks_OrderId_Status';
            """)));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT is_unique FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'checkout.PaymentLinkReplays')
              AND name = N'IX_PaymentLinkReplays_MerchantId_Operation_IdempotencyKey';
            """)));

        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'checkout.PaymentLinks')
              AND referenced_object_id = OBJECT_ID(N'shop.Orders');
            """)));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'checkout.PaymentLinkReplays')
              AND referenced_object_id = OBJECT_ID(N'shop.Orders');
            """)));
    }

    private static PolDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, new ModuleAssemblies([
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

    private static async Task<List<string>> ReadStringsAsync(SqlConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
            "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
        {
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
        }

        await IntegrationDb.ExecAsync(master,
            $"CREATE DATABASE [{DatabaseName}] COLLATE Thai_100_CI_AS;");
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 170;");
        await using var target = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(target, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task DropDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
            "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
        {
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
        }
    }

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

    private sealed class FixedTokenService : IPaymentLinkTokenService
    {
        private static readonly byte[] Digest = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        public PaymentLinkToken Mint() => new("fixed-token-for-sql-test", Digest.ToArray());
        public byte[] Hash(string rawToken) => Digest.ToArray();
    }

    private sealed class IntegrationActor(Guid merchantId, Guid userId, string? saleCode = null) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => userId;
        public bool HasActor => true;
        public string? SaleCode => saleCode;
    }

    private sealed class UnboundActor : IActorContext
    {
        public static readonly UnboundActor Instance = new();
        public Guid MerchantId => Guid.Empty;
        public Guid? UserId => null;
        public bool HasActor => false;
    }

    private sealed class IntegrationClock(DateTime value) : IClock
    {
        public DateTime UtcNow => value;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
