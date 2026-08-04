using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Domain;

namespace Integration.Tests;

/// <summary>
/// <c>ProductRepository.UpsertByDocumentNoAsync</c> against the live catalogue (products-sp-gateway REQ-7.2,
/// 7.4, 7.8, 9.2). This is the one part of the upsert that only a real SQL Server can prove: the unique
/// <c>IX_Products_DocumentNo</c> and the <c>DbUpdateException</c> it raises, which the repository has to
/// answer by resetting the change tracker and replaying the page. Every document here carries a run-unique
/// <c>DocumentNo</c> prefix and is deleted again at the end, so the suite can run repeatedly and beside the
/// demo seed without either disturbing the other.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductUpsertIntegrationTests : IAsyncLifetime
{
    /// <summary>Products carry no merchant of their own (central catalogue, no query filter), so the bound
    /// actor only has to exist.</summary>
    private static readonly Guid AnyMerchant = IntegrationDb.MerchantA;

    private readonly string _prefix = $"IT-{Guid.NewGuid():N}";

    private string DocumentNo(string suffix) => $"{_prefix}/{suffix}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(connection,
            "DELETE FROM shop.Products WHERE DocumentNo LIKE @prefix + N'%';", ("@prefix", _prefix));
    }

    private static MerchantRuntimeDbContext NewContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.AppConn);
        if (interceptor is not null) options.AddInterceptors(interceptor);

        return new MerchantRuntimeDbContext(
            options.Options, new FakeActor(AnyMerchant), AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);
    }

    private static ProductInput Input(
        string documentNo, decimal totalPremium = 15900m, string? showName = null,
        PaymentStatus paymentStatus = PaymentStatus.UNPAID, DateTime? paidDate = null) =>
        new(ProductGroup.VMI, DocumentType.POLICY, documentNo, "77001", totalPremium,
            paymentStatus, paidDate, ShowName: showName);

    [Fact]
    public async Task An_unseen_document_is_created()
    {
        var documentNo = DocumentNo("new-1");

        await using (var db = NewContext())
        {
            var stored = Assert.Single(
                await new ProductRepository(db).UpsertByDocumentNoAsync([Input(documentNo, showName: "ผู้เอาประกัน")], default));

            Assert.NotEqual(Guid.Empty, stored.Id);
            Assert.Equal(documentNo, stored.DocumentNo);
        }

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.DocumentNo == documentNo);
        Assert.Equal(15900m, row.TotalPremium);
        Assert.Equal("ผู้เอาประกัน", row.ShowName);
        Assert.Equal(PaymentStatus.UNPAID, row.PaymentStatus);
    }

    [Fact]
    public async Task A_document_already_here_is_refreshed_in_place()
    {
        var documentNo = DocumentNo("refresh-1");
        Guid id;

        await using (var db = NewContext())
            id = Assert.Single(await new ProductRepository(db).UpsertByDocumentNoAsync([Input(documentNo)], default)).Id;

        await using (var db = NewContext())
        {
            var stored = Assert.Single(await new ProductRepository(db)
                .UpsertByDocumentNoAsync([Input(documentNo, totalPremium: 16900m, showName: "ชื่อใหม่")], default));

            // The same document keeps its identity: a cart line opened off the earlier page still resolves.
            Assert.Equal(id, stored.Id);
        }

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.DocumentNo == documentNo);
        Assert.Equal(16900m, row.TotalPremium);
        Assert.Equal("ชื่อใหม่", row.ShowName);
    }

    [Fact]
    public async Task A_case_variant_DocumentNo_refreshes_the_same_row()
    {
        // shop.Products.DocumentNo collates case-insensitively (Thai_100_CI_AS), so a
        // case-variant upstream row is the same document: it must refresh the stored row in place, never
        // stage a second Add into IX_Products_DocumentNo and fail the page.
        var documentNo = DocumentNo("CASE-1");
        Guid id;

        await using (var db = NewContext())
            id = Assert.Single(await new ProductRepository(db).UpsertByDocumentNoAsync([Input(documentNo)], default)).Id;

        await using (var db = NewContext())
        {
            var stored = Assert.Single(await new ProductRepository(db)
                .UpsertByDocumentNoAsync([Input(documentNo.ToLowerInvariant(), totalPremium: 20250m)], default));

            Assert.Equal(id, stored.Id);
        }

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.Id == id);
        Assert.Equal(documentNo.ToLowerInvariant(), row.DocumentNo);
        Assert.Equal(20250m, row.TotalPremium);
    }

    // REQ-7.4 — the upstream reporting UNPAID must never undo a payment taken here; the PaidDate survives too.
    [Fact]
    public async Task A_local_PAID_is_never_downgraded_by_an_UNPAID_row()
    {
        var documentNo = DocumentNo("paid-1");
        var paidAt = new DateTime(2026, 7, 20, 9, 15, 0);

        await using (var db = NewContext())
            await new ProductRepository(db).UpsertByDocumentNoAsync(
                [Input(documentNo, paymentStatus: PaymentStatus.PAID, paidDate: paidAt)], default);

        await using (var db = NewContext())
        {
            var stored = Assert.Single(await new ProductRepository(db)
                .UpsertByDocumentNoAsync([Input(documentNo, totalPremium: 16900m)], default));

            Assert.Equal(PaymentStatus.PAID, stored.PaymentStatus);
            Assert.Equal(paidAt, stored.PaidDate);
            Assert.Equal(16900m, stored.TotalPremium);   // every other field still refreshes
        }

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.DocumentNo == documentNo);
        Assert.Equal(PaymentStatus.PAID, row.PaymentStatus);
        Assert.Equal(paidAt, row.PaidDate);
    }

    // REQ-7.8 — two requests searching the same page at once: the loser of the insert race must not fail the
    // page. The interceptor creates the document from another connection in the window between this
    // repository's read and its INSERT, which is exactly the race, and only fires once so the replay can win.
    [Fact]
    public async Task A_document_inserted_by_a_concurrent_writer_is_reloaded_and_the_page_still_answers()
    {
        var documentNo = DocumentNo("race-1");
        var interceptor = new InsertOnFirstSave(() => Input(documentNo, totalPremium: 100m, showName: "ผู้ชนะการแข่งขัน"));

        await using (var db = NewContext(interceptor))
        {
            var stored = Assert.Single(await new ProductRepository(db)
                .UpsertByDocumentNoAsync([Input(documentNo, totalPremium: 16900m, showName: "ผลลัพธ์ที่ถูกต้อง")], default));

            Assert.Equal(documentNo, stored.DocumentNo);
            // The retry refreshed the row the other writer created rather than inserting a second one.
            Assert.Equal(16900m, stored.TotalPremium);
        }

        Assert.True(interceptor.Fired, "the race was never provoked — the test proved nothing.");

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.DocumentNo == documentNo);
        Assert.Equal(16900m, row.TotalPremium);
        Assert.Equal("ผลลัพธ์ที่ถูกต้อง", row.ShowName);
    }

    [Fact]
    public async Task An_empty_page_touches_nothing()
    {
        await using var db = NewContext();
        Assert.Empty(await new ProductRepository(db).UpsertByDocumentNoAsync([], default));
    }

    /// <summary>Creates the document from a separate context the first time the intercepted context saves —
    /// a competing writer landing inside the read-to-write window, deterministically.</summary>
    private sealed class InsertOnFirstSave(Func<ProductInput> competitor) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await using var other = NewContext();
                other.Set<Product>().Add(Product.Create(competitor()));
                await other.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

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
