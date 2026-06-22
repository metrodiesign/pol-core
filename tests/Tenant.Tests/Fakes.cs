using BuildingBlocks.Application;
using Payments.Domain;
using Tenant.Application;
using Tenant.Domain;
using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Tests;

internal sealed class FakeTenantRepository : ITenantRepository
{
    public readonly List<DomainTenant> Added = [];
    public bool Exists;
    public DomainTenant? ByCode;

    public void Add(DomainTenant tenant) => Added.Add(tenant);
    public Task<DomainTenant?> GetByCodeAsync(string normalizedCode, CancellationToken ct) => Task.FromResult(ByCode);
    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken ct) => Task.FromResult(Exists);
}

internal sealed class FakePspConnectionRepository : IAdminPspConnectionRepository
{
    public readonly List<PspConnection> Added = [];

    public void Add(PspConnection connection) => Added.Add(connection);
    public Task<PspConnection?> GetAsync(Guid tenantId, PspCode psp, CancellationToken ct) => Task.FromResult<PspConnection?>(null);
    public Task<PspConnection?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<PspConnection?>(null);
    public Task<IReadOnlyList<PspConnection>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PspConnection>>(Added.Where(x => x.TenantId == tenantId).ToList());
}

internal sealed class FakeVault : IAdminVaultSecretStore
{
    public readonly List<(Guid TenantId, string Name, string Secret)> Stored = [];

    public Task StoreAsync(Guid tenantId, string name, string plaintextSecret, CancellationToken ct)
    {
        Stored.Add((tenantId, name, plaintextSecret));
        return Task.CompletedTask;
    }

    public Task<string> RevealAsync(Guid tenantId, string name, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> MaskedAsync(Guid tenantId, string name, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<bool> ExistsAsync(Guid tenantId, string name, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class FakeAuditWriter : IProvisioningAuditWriter
{
    public readonly List<ProvisioningAudit> Appended = [];
    public void Append(ProvisioningAudit entry) => Appended.Add(entry);
}

/// <summary>Runs the transaction delegate (RetriesToSimulate + 1) times to exercise the retrying
/// execution strategy — the handler must stay idempotent (result built fresh each attempt).</summary>
internal sealed class FakeUnitOfWork : IAdminUnitOfWork
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
