using System.Data.Common;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;

namespace Integration.Tests;

/// <summary>
/// <c>DocumentSaleProbe</c> against the real SQL Server (products-external-source-of-truth REQ-5.1, 5.2,
/// 5.10-5.15). The probe is the only thing standing between a document that has already been paid for and a
/// second customer buying it, and every rule it encodes is a property of live rows in three tables — order
/// status, payment-session status, and session AGE — so a fake context proves none of it. The lapse-by-age
/// rule in particular (REQ-5.13) cannot be shown by any in-memory double: it is the absence of a sweeper that
/// makes it necessary.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DocumentSaleProbeIntegrationTests
{
    private const string Group = "VMI";

    [Fact]
    public async Task A_paid_order_makes_the_document_sold_even_when_it_belongs_to_another_merchant()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // Sold by merchant B; the probe below runs bound to merchant A (REQ-5.2 — the read floor must not
            // apply, or every merchant could resell what another merchant already sold).
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantB, OrderStatusPaid, documentNo);

            var status = Assert.Single(await ProbeAsync(documentNo));

            Assert.Equal(DocumentSaleState.Sold, status.State);
            // REQ-5.14: the answer names the holder and the reason, not a bare true/false.
            Assert.Equal(soldBy, status.HeldByOrderId);
            Assert.Equal(documentNo, status.Key.DocumentNo);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_document_held_only_under_a_different_product_group_is_still_sellable()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // REQ-5.1 matches BOTH columns — the residual ProductGroup predicate the probe applies in memory.
            await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo, group: "CMI");

            Assert.Empty(await ProbeAsync(documentNo, group: "VMI"));
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task An_awaiting_payment_order_with_no_session_leaves_the_document_sellable()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // REQ-5.11. AwaitingPayment has no automatic way out, so treating it as a lock would strand the
            // document forever the first time a customer walked away from the payment page.
            await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusAwaitingPayment, documentNo);

            Assert.Empty(await ProbeAsync(documentNo));
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_cancelled_order_with_no_session_leaves_the_document_sellable()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusCancelled, documentNo); // REQ-5.12

            Assert.Empty(await ProbeAsync(documentNo));
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_chargeable_session_inside_its_ttl_holds_the_document()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var orderId = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusAwaitingPayment, documentNo);
            await SeedSessionAsync(c, orderId, SessionStatusCreated, ageHours: 1); // REQ-5.10

            var status = Assert.Single(await ProbeAsync(documentNo));

            Assert.Equal(DocumentSaleState.PaymentInFlight, status.State);
            Assert.Equal(orderId, status.HeldByOrderId);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_chargeable_session_past_its_ttl_releases_the_document_without_anyone_touching_the_row()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // REQ-5.13. Nothing in this system ever marks an abandoned session Expired on its own, so the hold
            // has to lapse from the row's AGE. 25h is past Session.OpenTtl (24h) while the row still says Created.
            var orderId = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusAwaitingPayment, documentNo);
            var sessionId = await SeedSessionAsync(c, orderId, SessionStatusCreated, ageHours: 25);

            Assert.Empty(await ProbeAsync(documentNo));

            var statusAfter = (int)(await IntegrationDb.ScalarAsync(c,
                "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", sessionId)))!;
            Assert.Equal(SessionStatusCreated, statusAfter);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_psp_confirmed_session_holds_the_document_before_the_order_flips_to_paid()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // REQ-5.10, the window between the PSP webhook and the outbox: the session already says Paid, the
            // order does not yet. Without Session.Paid in the predicate the document escapes both locks here —
            // and it is aged well past the TTL on purpose, so only the Paid branch can be what holds it.
            var orderId = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusAwaitingPayment, documentNo);
            await SeedSessionAsync(c, orderId, SessionStatusPaid, ageHours: 48);

            var status = Assert.Single(await ProbeAsync(documentNo));

            Assert.Equal(DocumentSaleState.PaymentInFlight, status.State);
            Assert.Equal(orderId, status.HeldByOrderId);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task Document_numbers_that_differ_only_in_letter_case_are_the_same_document()
    {
        // REQ-2.3/REQ-2.7 — SQL's Thai_100_CI_AS collation owns the equality rule. Real document numbers are
        // digits, slashes and Thai letters (no case at all), so proving case-insensitivity needs a contrived
        // Latin value on the VCentralPay side; the Thai part rides along to prove the round-trip too.
        var stored = $"AB-{Guid.NewGuid():N}/กธ/900001";
        var asked = stored.ToLowerInvariant();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, stored);

            var status = Assert.Single(await ProbeAsync(asked));

            Assert.Equal(DocumentSaleState.Sold, status.State);
            Assert.Equal(soldBy, status.HeldByOrderId);
            // The status echoes the key the CALLER asked with, not the DB's spelling.
            Assert.Equal(asked, status.Key.DocumentNo);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_key_padded_with_blanks_still_finds_the_document_that_was_sold()
    {
        // REQ-2.3/REQ-5.1. This is the fail-OPEN direction and the reason the trim cannot live in SQL: SQL
        // Server ignores a trailing blank when it compares but NOT a leading one, so an untrimmed " 69100/..."
        // matches no row, comes back absent, and absent means sellable — a second sale of a paid document.
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo);

            var padded = $"  {documentNo}\t";
            var status = Assert.Single(await ProbeAsync(padded, group: $" {Group} "));

            Assert.Equal(DocumentSaleState.Sold, status.State);
            Assert.Equal(soldBy, status.HeldByOrderId);
            Assert.Equal(padded, status.Key.DocumentNo); // echoed verbatim, not the trimmed form
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_stored_number_carrying_a_trailing_blank_still_answers_a_clean_key()
    {
        // The mirror of the case above, from the row's side: the seed writes raw SQL, so it can plant the
        // trailing blank the domain would have trimmed. SQL matches it, the in-memory pass must too.
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo + " ");

            var status = Assert.Single(await ProbeAsync(documentNo));

            Assert.Equal(DocumentSaleState.Sold, status.State);
            Assert.Equal(soldBy, status.HeldByOrderId);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_zero_weight_character_the_collation_ignores_does_not_break_the_sold_check()
    {
        // Thai_100_CI_AS gives U+00AD SOFT HYPHEN zero weight, so SQL returns the row for this key while an
        // ordinal comparer would refuse to attribute it — the answer would be either "sellable" (a double sale)
        // or a failure of the whole batch. The document is held, and saying so is the only acceptable outcome.
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo);

            var status = Assert.Single(await ProbeAsync(documentNo + '\u00AD'));

            Assert.Equal(DocumentSaleState.Sold, status.State);
            Assert.Equal(soldBy, status.HeldByOrderId);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task Twenty_five_keys_are_answered_by_a_single_database_read()
    {
        // REQ-5.15: 25 is the page size of the catalogue, i.e. the largest batch the list path can ask about.
        // One query per document would put 25 round-trips on every search.
        var documentNos = Enumerable.Range(0, 25).Select(_ => NewDocumentNo()).ToArray();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            var soldBy = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNos[7]);

            var counter = new CommandCounter();
            await using var db = NewContext(counter);
            var sut = new DocumentSaleProbe(db, new FixedClock(DateTime.UtcNow));

            var statuses = await sut.ProbeAsync(
                [.. documentNos.Select(d => new DocumentKey(d, Group))], CancellationToken.None);

            Assert.Equal(1, counter.Count);
            var held = Assert.Single(statuses);
            Assert.Equal(documentNos[7], held.Key.DocumentNo);
            Assert.Equal(soldBy, held.HeldByOrderId);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    // probe-dependency-failure-mapping REQ-1.1/2.1: the REAL SqlClient failing a REAL connection attempt
    // (a port nothing listens on) comes out of ProbeAsync as the classified DependencyUnavailableException
    // with the provider exception preserved as inner — never as a raw SqlException for the opaque-500 bucket.
    [Fact]
    public async Task A_dead_platform_database_surfaces_as_DependencyUnavailable_not_a_raw_SqlException()
    {
        var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=VCentralPay;User Id=pol_app;Password=unused;"
                + "Encrypt=True;TrustServerCertificate=True;Connect Timeout=1");
        await using var db = new MerchantRuntimeDbContext(
            options.Options, new FakeActor(IntegrationDb.MerchantA), AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);
        var sut = new DocumentSaleProbe(db, new FixedClock(DateTime.UtcNow));

        var thrown = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            sut.ProbeAsync([new DocumentKey(NewDocumentNo(), Group)], CancellationToken.None));

        Assert.IsAssignableFrom<DbException>(thrown.InnerException);
    }

    // ---- harness -------------------------------------------------------------------------------------

    private const int OrderStatusAwaitingPayment = 0;
    private const int OrderStatusPaid = 1;
    private const int OrderStatusCancelled = 2;

    private const int SessionStatusCreated = 0;
    private const int SessionStatusPaid = 2;

    /// <summary>Run-unique so parallel runs (and leftovers from a crashed run) cannot answer for each other.</summary>
    private static string NewDocumentNo() => $"69100/{Guid.NewGuid():N}";

    /// <summary>Bound to merchant A always — every cross-merchant assertion in this file depends on the probe
    /// ignoring that binding, so it must be a real one.</summary>
    private static MerchantRuntimeDbContext NewContext(CommandCounter? counter = null)
    {
        var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlServer(IntegrationDb.AppConn);
        if (counter is not null)
            options.AddInterceptors(counter);
        return new MerchantRuntimeDbContext(
            options.Options, new FakeActor(IntegrationDb.MerchantA), AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);
    }

    private static async Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(string documentNo, string group = Group)
    {
        await using var db = NewContext();
        var sut = new DocumentSaleProbe(db, new FixedClock(DateTime.UtcNow));
        return await sut.ProbeAsync([new DocumentKey(documentNo, group)], CancellationToken.None);
    }

    private static async Task<Guid> SeedOrderAsync(
        SqlConnection c, List<Guid> orders, Guid merchantId, int status, string documentNo, string group = Group)
    {
        var orderId = Guid.NewGuid();
        orders.Add(orderId);
        await IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt)
            VALUES (@id, @m, @orderNo, 15000, N'THB', @status, SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()));
            """,
            ("@id", orderId), ("@m", merchantId), ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"),
            ("@status", status), ("@token", Guid.NewGuid().ToString("N")));

        await IntegrationDb.ExecAsync(c,
            """
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                 DiscountAmount, DiscountCurrency, DocumentNo, ProductGroup, DocumentType,
                 InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth)
            VALUES (@id, @orderId, @m, 1, 15000, N'THB', 0, N'THB', @documentNo, @group, 'POLICY',
                    N'Somchai', N'Jaidee', N'1234567890123', '1990-01-01');
            """,
            ("@id", Guid.NewGuid()), ("@orderId", orderId), ("@m", merchantId),
            ("@documentNo", documentNo), ("@group", group));

        return orderId;
    }

    private static async Task<Guid> SeedSessionAsync(SqlConnection c, Guid orderId, int status, int ageHours)
    {
        var sessionId = Guid.NewGuid();
        await IntegrationDb.ExecAsync(c,
            """
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, Status, CreatedAt, UpdatedAt, AmountAmount, AmountCurrency)
            VALUES (@id, @m, @orderId, N'card', 0, @status, DATEADD(hour, @age, SYSUTCDATETIME()),
                    SYSUTCDATETIME(), 15000, N'THB');
            """,
            ("@id", sessionId), ("@m", IntegrationDb.MerchantA), ("@orderId", orderId), ("@status", status),
            ("@age", -ageHours));
        return sessionId;
    }

    /// <summary>Runs as <c>sa</c>, not <c>pol_app</c>: the application principal deliberately holds no DELETE
    /// on <c>txn.PaymentSessions</c> (a payment attempt is never erased in production), so only the teardown
    /// needs the stronger login. Seeding and probing both still go through <c>pol_app</c>.</summary>
    private static async Task CleanupAsync(List<Guid> orders)
    {
        await using var sa = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        foreach (var orderId in orders)
        {
            await IntegrationDb.ExecAsync(sa, "DELETE txn.PaymentSessions WHERE OrderId = @id;", ("@id", orderId));
            await IntegrationDb.ExecAsync(sa, "DELETE shop.OrderItems WHERE OrderId = @id;", ("@id", orderId));
            await IntegrationDb.ExecAsync(sa, "DELETE shop.Orders WHERE Id = @id;", ("@id", orderId));
        }
    }

    /// <summary>Counts the commands EF actually sends — the only honest way to assert "one read" (REQ-5.15).</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
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
