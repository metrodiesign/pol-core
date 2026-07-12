using BuildingBlocks.Application;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Application.Users.Permissions;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Merchants.Domain.Users.Permissions;

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

internal sealed class FakeVault : IVaultSecretStore
{
    public readonly List<(Guid MerchantId, string Name, string Secret)> Stored = [];

    public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken ct)
    {
        Stored.Add((merchantId, name, plaintextSecret));
        return Task.CompletedTask;
    }

    public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken ct)
    {
        Stored.Add((merchantId, name, plaintextSecret));
        return Task.CompletedTask;
    }

    public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class FakeAuditWriter : IProvisioningAuditWriter
{
    public readonly List<ProvisioningAudit> Appended = [];
    public void Append(ProvisioningAudit entry) => Appended.Add(entry);
}

/// <summary>Runs the transaction delegate (RetriesToSimulate + 1) times to exercise the retrying
/// execution strategy — the handler must stay idempotent (result built fresh each attempt).</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Runs;
    public int RetriesToSimulate;

    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        T result = default!;
        for (var attempt = 0; attempt <= RetriesToSimulate; attempt++)
        {
            Runs++;
            result = await operation(ct);
        }
        return result;
    }
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);
}
