using BuildingBlocks.Application;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

internal sealed class FakeMerchantRepository : IMerchantRepository
{
    public readonly List<Merchant> Added = [];
    public bool Exists;
    public Merchant? ByCode;

    public void Add(Merchant merchant) => Added.Add(merchant);
    public Task<Merchant?> GetByCodeAsync(string normalizedCode, CancellationToken ct) => Task.FromResult(ByCode);
    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken ct) => Task.FromResult(Exists);
}

internal sealed class FakePspConnectionRepository : IConnectionRepository
{
    public readonly List<Connection> Added = [];

    public void Add(Connection connection) => Added.Add(connection);
    public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken ct) => Task.FromResult<Connection?>(null);
    public Task<Connection?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<Connection?>(null);
    public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Connection>>(Added.Where(x => x.MerchantId == merchantId).ToList());
}

/// <summary>Stands in for the cross-context provisioning UoW (task 8.5.4). Records every call and replays
/// the STORED result for a repeated <c>operationKey</c> (mirrors the real coordinator's idempotency-ledger
/// behavior closely enough for a handler-level test, without the SQL machinery — full retry/ledger coverage
/// lives in ProvisioningCoordinatorTests).</summary>
internal sealed class FakeProvisioningWriter : IProvisioningWriter
{
    public readonly List<(ProvisionSpec Spec, Guid CallerAdminId, long ExpectedAuthorizationVersion, string OperationKey)> Calls = [];
    private readonly Dictionary<string, ProvisioningWriteResult> _resultsByKey = new(StringComparer.Ordinal);

    public Task<ProvisioningWriteResult> ProvisionAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey,
        CancellationToken cancellationToken)
    {
        Calls.Add((spec, callerAdminId, expectedAuthorizationVersion, operationKey));

        if (_resultsByKey.TryGetValue(operationKey, out var existing))
            return Task.FromResult(existing);

        var result = new ProvisioningWriteResult(
            Guid.NewGuid(),
            [.. spec.Connections.Select(c => new ProvisionedConnectionWrite(Guid.NewGuid(), c.Psp, c.MaskedSecretHints))]);
        _resultsByKey[operationKey] = result;
        return Task.FromResult(result);
    }
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);
}
