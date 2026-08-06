using System.Text;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Domain;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IDocumentSaleProbe"/> (products-external-source-of-truth REQ-5.1,
/// 5.2, 5.10-5.15). Lives here because this is the only assembly that maps <c>shop.OrderItems</c>,
/// <c>shop.Orders</c> AND <c>txn.PaymentSessions</c> in one context — the sibling of
/// <see cref="PaymentSessionProbe"/>.
/// <para>LINQ, deliberately, not raw SQL: a schema-qualified raw query would be SQL-Server-only, and this
/// same query has to run against the SQLite substitution the Hosts.Tests suite uses (design decision S6).</para>
/// <para><c>IgnoreQueryFilters</c> on all three sets is the point of the port, not an oversight: a document
/// sold by ANOTHER merchant is still sold (REQ-5.2), so the merchant read floor must not apply here. It is a
/// read of three columns with no merchant predicate to widen, and it never emits
/// <c>ISecurityTelemetry</c> — see the reason recorded on its <c>BypassPrimitiveTests</c> allowlist entry.</para>
/// </summary>
internal sealed class DocumentSaleProbe : IDocumentSaleProbe
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly IClock _clock;

    public DocumentSaleProbe(MerchantRuntimeDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return [];

        // REQ-2.3: blanks around a document number never make it a different document. The trim happens HERE,
        // in C#, before the parameter is built — never as LTRIM/RTRIM around the column, which would cost the
        // index seek (design decision #5). It is not cosmetic: SQL Server ignores a TRAILING blank when it
        // compares but not a LEADING one, so " 69100/900001" matched no row at all, the key fell out of the
        // result, and the caller read that absence as "sellable" for a document a Paid order already holds.
        // DistinctBy(Key): the same DocumentKey twice in the request would otherwise emit two DocumentSaleStatus
        // rows with an equal Key, and a caller keying the answer with ToDictionary(s => s.Key) would throw. One
        // status per distinct key is the contract (see IDocumentSaleProbe).
        var probes = keys
            .Select(key => (Key: key, DocumentNo: key.DocumentNo.Trim(), ProductGroup: key.ProductGroup.Trim()))
            .DistinctBy(p => p.Key)
            .ToArray();
        var documentNos = probes.Select(p => p.DocumentNo).Distinct(StringComparer.Ordinal).ToArray();

        // The whole liveness rule, expressed as an age instead of a row state: nothing in this system ever
        // marks an abandoned session expired on its own (Session.MarkExpired is request-driven), so reading
        // the status alone would lock a document forever. Comparing CreatedAt against now - OpenTtl makes the
        // hold lapse by itself (REQ-5.13).
        var openSince = _clock.UtcNow - Session.OpenTtl;

        var matched = await (
            from item in _db.OrderItems.IgnoreQueryFilters()
            join order in _db.Orders.IgnoreQueryFilters() on item.OrderId equals order.Id
            where documentNos.Contains(item.DocumentNo)
                && (order.Status == OrderStatus.Paid
                    // Session.Paid has to count on its own: between the PSP webhook marking the session paid
                    // and the outbox flipping the order, neither side says "sold" — and the document would
                    // fall out of both locks in that window.
                    || _db.PaymentSessions.IgnoreQueryFilters().Any(session =>
                        session.OrderId == order.Id
                        && (session.Status == SessionStatus.Paid
                            || ((session.Status == SessionStatus.Created || session.Status == SessionStatus.Redirected)
                                && session.CreatedAt > openSince))))
            select new HeldDocument(item.DocumentNo, item.ProductGroup, order.Id, order.Status))
            .ToListAsync(cancellationToken);

        // The same trim on the stored side. Values are trimmed on write (Orders.Domain Item does it), but SQL
        // Server matched a row that carries a trailing blank anyway, and that row must not be the thing that
        // trips the guard below.
        var rows = matched.ConvertAll(row => row with
        {
            DocumentNo = row.DocumentNo.Trim(),
            ProductGroup = row.ProductGroup.Trim(),
        });

        // SQL, under the database's Thai_100_CI_AS collation, owns the REQ-2.3 equality rule (design decision
        // #5), so the C# side must never be STRICTER than it: a row SQL matched but C# drops is a sold document
        // reported sellable. A row that matches no requested number at all is therefore a hard stop, not a shrug.
        var requested = new HashSet<string>(documentNos.Select(Fold), DocumentComparer);
        if (rows.Exists(row => !requested.Contains(Fold(row.DocumentNo))))
            throw new InvalidOperationException(
                "A sold-check row matched in SQL but not under the in-memory comparer — the C# fast-path and "
                + "the column collation disagree. Refusing to report the document as sellable.");

        var statuses = new List<DocumentSaleStatus>();
        foreach (var probe in probes)
        {
            // The residual predicate (REQ-5.1 matches BOTH columns): ProductGroup is applied here, in memory,
            // because it has 4 values and would not narrow the index seek.
            HeldDocument? sold = null;
            HeldDocument? inFlight = null;
            foreach (var row in rows)
            {
                if (!DocumentComparer.Equals(Fold(row.DocumentNo), Fold(probe.DocumentNo))
                    || !DocumentComparer.Equals(Fold(row.ProductGroup), Fold(probe.ProductGroup)))
                    continue;

                if (row.Status == OrderStatus.Paid)
                    sold ??= row;
                else
                    inFlight ??= row;
            }

            var held = sold ?? inFlight;
            if (held is null)
                continue; // absent from the result = Sellable

            // Echoes the key the CALLER asked with, untrimmed, so it can match the answer to its own request.
            statuses.Add(new DocumentSaleStatus(
                probe.Key, sold is not null ? DocumentSaleState.Sold : DocumentSaleState.PaymentInFlight,
                held.OrderId));
        }

        return statuses;
    }

    /// <summary>
    /// Compatibility folding (<see cref="NormalizationForm.FormKC"/>) applied ONLY when a matched row is
    /// attributed back to a key — never to the value bound into the SQL parameter or stored in a column. It exists
    /// to keep the C# side from being STRICTER than <c>Thai_100_CI_AS</c>, which is width-insensitive: a document
    /// number carrying a FULLWIDTH digit or slash compares equal to its halfwidth form in SQL, so SQL returns the
    /// row, but <c>InvariantCultureIgnoreCase</c> alone keeps them apart — the row would then match in SQL and no
    /// key in memory, tripping the guard below and failing the whole batch with a 500 over one document. FormKC
    /// folds the fullwidth forms to halfwidth so the two sides agree.
    /// </summary>
    private static string Fold(string value) => value.Normalize(NormalizationForm.FormKC);

    /// <summary>
    /// How a folded row is attributed back to the key that asked for it. Linguistic, not <c>OrdinalIgnoreCase</c>:
    /// ordinal case-folding is STRICTER than <c>Thai_100_CI_AS</c> — it keeps apart strings the collation folds
    /// together, because the collation gives zero weight to characters like U+00AD SOFT HYPHEN (verified on the
    /// live database: <c>N'69100/900001' = N'69100/900001' + NCHAR(0x00AD)</c> is true). Under the ordinal
    /// comparer such a row matched in SQL but could be attributed to no key, which left only two outcomes on a
    /// money path — drop it and call a sold document sellable, or throw and fail the whole batch. The invariant
    /// culture ignores the same zero-weight characters, so the two sides agree. Looser is the safe direction
    /// here and only here: SQL already decided WHICH rows are relevant, this only decides which key each row
    /// answers, and over-attributing can only report a document as held.
    /// </summary>
    private static readonly StringComparer DocumentComparer = StringComparer.InvariantCultureIgnoreCase;

    private sealed record HeldDocument(string DocumentNo, string ProductGroup, Guid OrderId, OrderStatus Status);
}
