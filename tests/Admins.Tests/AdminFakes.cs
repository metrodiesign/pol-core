using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Admins.Tests;

internal sealed class FakePlatformUserRepository : IUserRepository
{
    public readonly List<User> Accounts = [];
    public readonly List<MerchantAccess> Assignments = [];

    public void Add(User account) => Accounts.Add(account);
    public void AddAssignment(MerchantAccess assignment) => Assignments.Add(assignment);
    public void RemoveAssignment(MerchantAccess assignment) => Assignments.RemoveAll(a => a.Id == assignment.Id);

    public Task<User?> GetBySubjectAsync(string provider, string subject, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Provider == provider && a.Subject == subject));
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Email == email));
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Accounts.FirstOrDefault(a => a.Id == id));
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Accounts.Any(a => a.Id == id));

    public Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(
            Assignments.Where(a => a.AdminUserId == adminAccountId).Select(a => a.MerchantId).ToHashSet());

    public Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken ct) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.AdminUserId == adminAccountId && a.MerchantId == merchantId));

    // In-memory SFS stand-in: newest-first + id tiebreak, page-sliced (mirrors the real ordering contract, REQ-1.3).
    public Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken ct)
    {
        var all = Accounts
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new UserListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject is not null, a.Version))
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
            Sessions.Where(s => s.AdminUserId == adminAccountId).OrderByDescending(s => s.IssuedAt).ThenBy(s => s.Id).ToList());
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.Id == sessionId));
}

internal sealed class FakeAdminOperationStore : IAdminOperationStore
{
    private readonly Dictionary<(Guid ActorId, string Operation, string Key), AdminOperationReplay> _records = [];
    public int Count => _records.Count;

    public Task AcquireAsync(Guid actorId, string operation, string idempotencyKey, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<AdminOperationReplay?> FindAsync(
        Guid actorId, string operation, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(_records.GetValueOrDefault((actorId, operation, idempotencyKey)));

    public void AddSucceeded(
        Guid actorId, string operation, string idempotencyKey, string requestHash,
        string responseBody, DateTime now) =>
        _records.Add((actorId, operation, idempotencyKey), new AdminOperationReplay(requestHash, responseBody, false));
}

/// <summary>Stands in for the central iam.Roles catalog (rf2) — <see cref="Roles"/> holds
/// <see cref="Iam.Domain.Roles.Role"/> instances directly, since the real repository now resolves/joins
/// against that table instead of an admin-owned one. Only the assignment+resolution surface remains here;
/// CRUD moved to Iam.Application (see Iam.Tests for the CRUD-handler fakes).</summary>
internal sealed class FakeAdminRoleRepository : IRoleRepository
{
    public readonly List<Role> Roles = [];
    public readonly List<RoleAssignment> Assignments = [];

    public void AddAssignment(RoleAssignment a) => Assignments.Add(a);
    public void RemoveAssignment(RoleAssignment a) => Assignments.RemoveAll(x => x.Id == a.Id);

    public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(
            Roles.Where(r => r.Scope == Scope.Platform && r.MerchantId == null && codes.Contains(r.Code))
                .ToDictionary(r => r.Code, r => r.Id));
    public Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(Assignments.Where(a => a.AdminUserId == adminId).Select(a => a.RoleId).ToHashSet());
    public Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken ct) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.AdminUserId == adminId && a.RoleId == roleId));
    public Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken ct) =>
        Task.FromResult(Assignments.Any(a => a.AdminUserId == adminId && a.RoleId == roleId));

    public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken ct)
    {
        var activeRoleIds = Assignments.Where(a => a.AdminUserId == adminId).Select(a => a.RoleId).ToHashSet();
        var keys = Roles.Where(r => activeRoleIds.Contains(r.Id) && r.Status == RoleStatus.Active)
            .SelectMany(r => r.PermissionKeys)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlySet<string>>(keys);
    }

    public Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken ct)
    {
        var roleIds = Assignments.Where(a => a.AdminUserId == adminId).Select(a => a.RoleId).ToHashSet();
        var codes = Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Code).OrderBy(c => c).ToList();
        return Task.FromResult<IReadOnlyList<string>>(codes);
    }
}

/// <summary>In-memory profile-lookup fake for handler tests — enum-keyed rows (id/code/name/active), no
/// reference-module types at all (masterdata-split: Admins.Tests no longer names Division/Level/Office/
/// Position, matching Admins.Application's own zero-module-reference boundary).</summary>
internal sealed class FakeProfileLookup : IProfileLookup
{
    public sealed record Row(Guid Id, string Code, string Name, bool IsActive);

    public readonly Dictionary<ProfileField, List<Row>> Items = [];

    /// <summary>Seeds one row and returns its generated id (the FK the test hands to a command).</summary>
    public Guid Add(ProfileField field, string code, string name, bool isActive = true)
    {
        var row = new Row(Guid.NewGuid(), code, name, isActive);
        if (!Items.TryGetValue(field, out var list))
            Items[field] = list = [];
        list.Add(row);
        return row.Id;
    }

    public Task<bool> ExistsActiveAsync(ProfileField field, Guid id, CancellationToken ct) =>
        Task.FromResult(Rows(field).Any(r => r.Id == id && r.IsActive));

    public Task<ProfileRef?> GetRefAsync(ProfileField field, Guid id, CancellationToken ct) =>
        Task.FromResult<ProfileRef?>(Rows(field).Where(r => r.Id == id)
            .Select(r => new ProfileRef(r.Id, r.Code, r.Name)).FirstOrDefault());

    private IEnumerable<Row> Rows(ProfileField field) => Items.TryGetValue(field, out var list) ? list : [];
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
