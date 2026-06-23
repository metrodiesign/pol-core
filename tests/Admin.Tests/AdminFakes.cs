using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

internal sealed class FakeAdminAccountRepository : IAdminAccountRepository
{
    public readonly List<AdminAccount> Accounts = [];
    public readonly List<AdminTenantAssignment> Assignments = [];

    public void Add(AdminAccount account) => Accounts.Add(account);
    public void AddAssignment(AdminTenantAssignment assignment) => Assignments.Add(assignment);
    public void RemoveAssignment(AdminTenantAssignment assignment) => Assignments.RemoveAll(a => a.Id == assignment.Id);

    public Task<AdminAccount?> GetBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Subject == subject));
    public Task<AdminAccount?> GetByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Email == email));
    public Task<AdminAccount?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlySet<Guid>> ListAssignedTenantIdsAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(
            Assignments.Where(a => a.AdminAccountId == adminAccountId).Select(a => a.TenantId).ToHashSet());

    public Task<AdminTenantAssignment?> GetAssignmentAsync(Guid adminAccountId, Guid tenantId, CancellationToken ct) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.AdminAccountId == adminAccountId && a.TenantId == tenantId));
}

internal sealed class FakeAdminAccountAuditWriter : IAdminAccountAuditWriter
{
    public readonly List<AdminAccountAudit> Appended = [];
    public void Append(AdminAccountAudit entry) => Appended.Add(entry);
}

internal sealed class FakeAdminTenantDirectory : IAdminTenantDirectory
{
    public bool ActiveResult = true;
    public Dictionary<Guid, string> Codes = [];
    public Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(ActiveResult);
    public Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> tenantIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Codes.Where(kv => tenantIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Codes.FirstOrDefault(kv => kv.Value == code) is { Key: var id } && id != Guid.Empty ? id : (Guid?)null);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        await operation(ct);
}

/// <summary>Simulates the admin unit of work translating a concurrent unique-violation into a
/// <see cref="ConflictException"/> (REQ-5.2), so the self-provision re-read path can be exercised.</summary>
internal sealed class ConflictingUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => throw new ConflictException("duplicate key");
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        throw new ConflictException("duplicate key");
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
}
