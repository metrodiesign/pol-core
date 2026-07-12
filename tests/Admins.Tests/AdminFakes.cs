using Admins.Application;
using Admins.Application.MasterData;
using Admins.Application.Permissions;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.MasterData;
using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;

namespace Admins.Tests;

internal sealed class FakePlatformUserRepository : IUserRepository
{
    public readonly List<User> Accounts = [];
    public readonly List<MerchantAccess> Assignments = [];

    public void Add(User account) => Accounts.Add(account);
    public void AddAssignment(MerchantAccess assignment) => Assignments.Add(assignment);
    public void RemoveAssignment(MerchantAccess assignment) => Assignments.RemoveAll(a => a.Id == assignment.Id);

    public Task<User?> GetBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Subject == subject));
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Email == email));
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Id == id));
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Accounts.Any(a => a.Id == id));

    public Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(
            Assignments.Where(a => a.PlatformUserId == adminAccountId).Select(a => a.MerchantId).ToHashSet());

    public Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken ct) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.PlatformUserId == adminAccountId && a.MerchantId == merchantId));

    // In-memory SFS stand-in: newest-first + id tiebreak, page-sliced (mirrors the real ordering contract, REQ-1.3).
    public Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken ct)
    {
        var all = Accounts
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new UserListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject is not null))
            .ToList();
        var items = all.Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToList();
        return Task.FromResult(new PagedResult<UserListItem>(items, query.Page, query.Limit, all.Count));
    }
}

internal sealed class FakePlatformUserAuditWriter : IAuditWriter
{
    public readonly List<Audit> Appended = [];
    public void Append(Audit entry) => Appended.Add(entry);
}

/// <summary>In-memory admin session store for command-handler tests. Records revoke calls; a small seed list backs
/// the sessions-list / find-by-id reads (admin-account-management REQ-4/5).</summary>
internal sealed class FakePlatformUserSessionStore : ISessionStore
{
    public readonly List<Session> Sessions = [];
    public readonly List<Guid> RevokedAdmins = [];
    public readonly List<Guid> RevokedFamilies = [];

    public Task<Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct) =>
        Task.FromResult<Session?>(null);
    public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public void Add(Session session) => Sessions.Add(session);
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => Task.FromResult(true);
    public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) { RevokedFamilies.Add(familyId); return Task.CompletedTask; }
    public Task RevokeAllForAdminAsync(Guid adminId, CancellationToken ct) { RevokedAdmins.Add(adminId); return Task.CompletedTask; }
    public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    public Task<IReadOnlyList<Session>> ListByAdminAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Session>>(
            Sessions.Where(s => s.PlatformUserId == adminAccountId).OrderByDescending(s => s.IssuedAt).ThenBy(s => s.Id).ToList());
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.Id == sessionId));
}

internal sealed class FakeAdminRoleRepository : IRoleRepository
{
    public readonly List<Role> Roles = [];
    public readonly List<RoleAssignment> Assignments = [];
    public IReadOnlySet<string> Catalog = Keys.AllKeys;

    public void Add(Role role) => Roles.Add(role);
    public void Remove(Role role) => Roles.RemoveAll(r => r.Id == role.Id);
    public void AddAssignment(RoleAssignment a) => Assignments.Add(a);
    public void RemoveAssignment(RoleAssignment a) => Assignments.RemoveAll(x => x.Id == a.Id);

    public Task<Role?> GetByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Roles.FirstOrDefault(r => r.Code == code));
    public Task<bool> CodeExistsAsync(string code, CancellationToken ct) =>
        Task.FromResult(Roles.Any(r => r.Code == code));
    public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken ct) =>
        Task.FromResult(Assignments.Count(a => a.RoleId == roleId));

    public Task<PagedResult<RoleListItem>> ListAsync(PagedQuery query, CancellationToken ct)
    {
        var all = Roles.Select(ToItem).ToList();
        var items = all.Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToList();
        return Task.FromResult(new PagedResult<RoleListItem>(items, query.Page, query.Limit, all.Count));
    }
    public Task<RoleListItem?> GetListItemByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Roles.Where(r => r.Code == code).Select(ToItem).FirstOrDefault());

    public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(Roles.Where(r => codes.Contains(r.Code)).ToDictionary(r => r.Code, r => r.Id));
    public Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(Assignments.Where(a => a.PlatformUserId == adminId).Select(a => a.RoleId).ToHashSet());
    public Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken ct) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.PlatformUserId == adminId && a.RoleId == roleId));
    public Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken ct) =>
        Task.FromResult(Assignments.Any(a => a.PlatformUserId == adminId && a.RoleId == roleId));

    public Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken ct) => Task.FromResult(Catalog);
    public Task<PermissionCatalogResult> ListCatalogAsync(CancellationToken ct) =>
        Task.FromResult(new PermissionCatalogResult([], []));

    public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken ct)
    {
        var activeRoleIds = Assignments.Where(a => a.PlatformUserId == adminId).Select(a => a.RoleId).ToHashSet();
        var keys = Roles.Where(r => activeRoleIds.Contains(r.Id) && r.Status == RoleStatus.Active)
            .SelectMany(r => r.PermissionKeys)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlySet<string>>(keys);
    }

    public Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken ct)
    {
        var roleIds = Assignments.Where(a => a.PlatformUserId == adminId).Select(a => a.RoleId).ToHashSet();
        var codes = Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Code).OrderBy(c => c).ToList();
        return Task.FromResult<IReadOnlyList<string>>(codes);
    }

    private RoleListItem ToItem(Role r) =>
        new(r.Code, r.Name, r.Description, r.Color, r.Status, [.. r.PermissionKeys], Assignments.Count(a => a.RoleId == r.Id));
}

/// <summary>In-memory master-data store for handler tests. One flat list holds all four master types; the
/// generic methods filter by concrete type via <c>OfType&lt;T&gt;</c> (mirrors the real per-table Set&lt;T&gt;()).</summary>
internal sealed class FakeMasterDataStore : IMasterDataStore
{
    public readonly List<MasterDataItem> Items = [];

    public Task<PagedResult<MasterItem>> ListAsync<T>(int page, int limit, string? search, CancellationToken ct) where T : MasterDataItem
    {
        var all = Items.OfType<T>()
            .Where(m => string.IsNullOrWhiteSpace(search) || m.Name.Contains(search!) || m.Code.Contains(search!))
            .OrderBy(m => m.Name)
            .Select(m => new MasterItem(m.Id, m.Code, m.Name, m.IsActive)).ToList();
        var items = all.Skip((page - 1) * limit).Take(limit).ToList();
        return Task.FromResult(new PagedResult<MasterItem>(items, page, limit, all.Count));
    }

    public Task<MasterItem> CreateAsync<T>(T entity, CancellationToken ct) where T : MasterDataItem
    {
        if (Items.OfType<T>().Any(m => m.Code == entity.Code))
            throw new ConflictException($"A record with code '{entity.Code}' already exists.");
        Items.Add(entity);
        return Task.FromResult(new MasterItem(entity.Id, entity.Code, entity.Name, entity.IsActive));
    }

    public Task<MasterItem> UpdateAsync<T>(Guid id, string name, bool isActive, CancellationToken ct) where T : MasterDataItem
    {
        var e = Items.OfType<T>().FirstOrDefault(m => m.Id == id)
            ?? throw new NotFoundException("The record was not found.");
        e.Rename(name);
        if (isActive) e.Activate(); else e.Deactivate();
        return Task.FromResult(new MasterItem(e.Id, e.Code, e.Name, e.IsActive));
    }

    public Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken ct) where T : MasterDataItem =>
        Task.FromResult(Items.OfType<T>().Any(m => m.Id == id && m.IsActive));

    public Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken ct) where T : MasterDataItem =>
        Task.FromResult(Items.OfType<T>().Where(m => m.Id == id)
            .Select(m => new MasterRef(m.Id, m.Code, m.Name)).FirstOrDefault());
}

internal sealed class FakeAdminMerchantDirectory : IAdminMerchantDirectory
{
    public bool ActiveResult = true;
    public Dictionary<Guid, string> Codes = [];
    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken ct) => Task.FromResult(ActiveResult);
    public Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> merchantIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Codes.Where(kv => merchantIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
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
