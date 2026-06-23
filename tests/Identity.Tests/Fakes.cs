using BuildingBlocks.Application;
using Identity.Application;
using Identity.Domain;

namespace Identity.Tests;

internal sealed class FakeTenantUserRepository : ITenantUserRepository
{
    public readonly List<TenantUser> Users = [];
    public readonly List<ExternalLogin> Logins = [];
    public readonly List<TenantUserProfile> Profiles = [];

    public void Add(TenantUser user) => Users.Add(user);
    public void AddExternalLogin(ExternalLogin login) => Logins.Add(login);
    public void AddProfile(TenantUserProfile profile) => Profiles.Add(profile);

    public Task<TenantUser?> GetBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Subject == subject));
    public Task<bool> ExistsBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult(Users.Any(u => u.Subject == subject));
}

internal sealed class FakeRegistrationTicketStore : IRegistrationTicketStore
{
    public readonly List<RegistrationTicket> Tickets = [];

    public void Add(RegistrationTicket ticket) => Tickets.Add(ticket);
    public Task<RegistrationTicket?> GetAsync(Guid ticketId, CancellationToken ct) =>
        Task.FromResult(Tickets.FirstOrDefault(t => t.Id == ticketId));
}

internal sealed class FakeRegistrationAuditWriter : IRegistrationAuditWriter
{
    public readonly List<RegistrationAudit> Appended = [];
    public void Append(RegistrationAudit entry) => Appended.Add(entry);
}

internal sealed class FakeTenantDirectory : ITenantDirectory
{
    public bool ActiveResult = true;
    public Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(ActiveResult);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        await operation(ct);
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
}
