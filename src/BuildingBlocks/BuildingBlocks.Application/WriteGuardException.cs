namespace BuildingBlocks.Application;

/// <summary>
/// Raised by a runtime DbContext's sealed write guard (rls-to-query-filter REQ-2) when a tracked change
/// fails the floor: a tenant key (or Merchant's own <c>Id</c>) is <see cref="Guid.Empty"/>, a tenant key
/// mutated after insert outside the one-time pending-approval carve-out, an append-only entity was
/// Modified/Deleted, or <see cref="IWriteAuthorizer"/> denied the (entityType, operation, targetMerchant)
/// capability. Distinct from EF Core's <c>DbUpdateConcurrencyException</c>, which signals a forged/stale
/// concurrency-token mismatch instead.
/// </summary>
public sealed class WriteGuardException : Exception
{
    public WriteGuardException(string message) : base(message) { }
}
