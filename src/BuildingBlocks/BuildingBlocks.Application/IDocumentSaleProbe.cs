namespace BuildingBlocks.Application;

/// <summary>
/// The pair that identifies one insurance document for a sold-check. <paramref name="DocumentNo"/> alone is
/// the identifier everywhere else (products-external-source-of-truth REQ-2.1); the group is carried too
/// because uniqueness across the Motor/NonMotor catalogues is a convention, not a DB constraint, and
/// <c>shop.OrderItems</c> already stores both (design decision #2). <paramref name="ProductGroup"/> is the
/// wire value of the enum (a plain string) so this port does not drag <c>Products.Domain</c> into
/// BuildingBlocks.
/// </summary>
public sealed record DocumentKey(string DocumentNo, string ProductGroup);

/// <summary>Why a document cannot be sold — or that it can.</summary>
public enum DocumentSaleState
{
    /// <summary>No order holds it (REQ-5.11, REQ-5.12).</summary>
    Sellable = 0,

    /// <summary>A <c>Paid</c> order holds it — permanent (REQ-5.1).</summary>
    Sold = 1,

    /// <summary>An order with a still-chargeable or PSP-confirmed payment session holds it (REQ-5.10). The
    /// hold lapses on its own once the session ages past its TTL — no row has to be marked (REQ-5.13).</summary>
    PaymentInFlight = 2,
}

/// <summary>
/// The answer for one key: the reason and the order holding it, not a bool (REQ-5.14). Callers turn this into
/// 400/409 WITHOUT echoing <see cref="HeldByOrderId"/> to the client — the holder may belong to another
/// merchant (REQ-5.7).
/// </summary>
public sealed record DocumentSaleStatus(DocumentKey Key, DocumentSaleState State, Guid? HeldByOrderId);

/// <summary>
/// Answers "has this insurance document already been sold through this platform?" — a question the upstream
/// catalogue can never answer, because it is read-only and never learns about our sales (REQ-5.1). The answer
/// is inferred from Orders.
/// </summary>
public interface IDocumentSaleProbe
{
    /// <summary>
    /// Checks every key of one request in a SINGLE database read (REQ-5.15) and across ALL merchants — a
    /// document sold by another merchant is still sold (REQ-5.2). A key that is absent from the result is
    /// <see cref="DocumentSaleState.Sellable"/>; only keys that are held come back.
    /// <para>Keys are compared trimmed and case-insensitively, folding fullwidth forms so the C# side is never
    /// stricter than the column collation (REQ-2.3/2.7). Attribution is deliberately no LOOSER than the SQL
    /// comparison decides — it never reports a held document as sellable — but two numbers that differ only in
    /// characters the collation ignores may attribute to the same key. The result carries at most ONE status per
    /// distinct <see cref="DocumentKey"/> (duplicate keys in the request are collapsed), so a caller may key the
    /// answer with <c>ToDictionary(s =&gt; s.Key)</c>. The <see cref="DocumentSaleStatus.Key"/> that comes back is
    /// the caller's own instance, untrimmed, so results can be matched to the request by reference or by value.</para>
    /// </summary>
    Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken);
}
