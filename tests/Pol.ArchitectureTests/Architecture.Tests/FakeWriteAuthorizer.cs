using BuildingBlocks.Application;

namespace Architecture.Tests;

/// <summary>Minimal <see cref="IWriteAuthorizer"/> test double shared across the rls-to-query-filter
/// write-floor tests — either grants every write (tests that are not exercising CanWrite itself, e.g.
/// seeding fixtures for <see cref="ReadFloorTests"/>) or denies every write (REQ-2.11 default-deny proof).</summary>
internal sealed class FakeWriteAuthorizer : IWriteAuthorizer
{
    public static readonly FakeWriteAuthorizer AllowAll = new(alwaysAllow: true);
    public static readonly FakeWriteAuthorizer DenyAll = new(alwaysAllow: false);

    private readonly bool _alwaysAllow;
    private FakeWriteAuthorizer(bool alwaysAllow) => _alwaysAllow = alwaysAllow;

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => _alwaysAllow;
}
