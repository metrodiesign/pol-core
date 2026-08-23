using Admins.Domain.Users;
using BuildingBlocks.Application;
using SharedKernel;

namespace Admins.Application.Users;

/// <summary>
/// Persistence for the admin realm. Bound by the host to the pol_admin (RLS-bypass) connection — admin tables
/// are control-plane (no per-merchant predicate), and resolution/provisioning run cross-merchant. Lookups are by
/// subject (resolution), email (invite binding) and id (Super-only management); assignments back the
/// accessible-merchant set (REQ-6). The SFS-paged directory backs the account-management console
/// (admin-account-management REQ-1).
/// </summary>
public interface IUserRepository
{
    void Add(User account);
    void AddAssignment(MerchantAccess assignment);
    void RemoveAssignment(MerchantAccess assignment);

    Task AcquireIdentityMutationLockAsync(CancellationToken cancellationToken);
    Task<User?> GetByIdentityAsync(ProviderIdentity identity, CancellationToken cancellationToken);
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task VerifyActiveSuperAsync(
        Guid callerId, long expectedAuthorizationVersion, CancellationToken cancellationToken);

    /// <summary>Cheap existence probe (<c>SELECT … EXISTS</c>) for the 404 gate on read/session handlers that
    /// need only to know the account exists, not its columns — avoids a full tracked-entity load.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken);
    Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken cancellationToken);

    /// <summary>The SFS-paged admin directory (admin-account-management REQ-1): filter/search/sort applied over
    /// the control-plane <c>User</c> set, <c>Total</c> counted after filter/search but before paging.</summary>
    Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken);
}

/// <summary>One admin directory row (admin-account-management REQ-1.2). <see cref="Tier"/>/<see cref="Status"/>
/// are the enums; the host projects them to lowercase wire strings (no global enum converter, B2).
/// <see cref="SubjectBound"/> = the invite has been claimed (Subject != null, REQ-1.2).</summary>
public sealed record UserListItem(
    Guid AdminId, string Email, Tier Tier, UserStatus Status, DateTime CreatedAt, bool SubjectBound, long Version);

/// <summary>Stages an append-only <see cref="Audit"/> in the current transaction (REQ-10.2).</summary>
public interface IAuditWriter
{
    void Append(Audit entry);
}

public sealed record AdminIdentityAuditEntry(
    Guid ActorAdminId,
    Guid TargetAdminId,
    string Reason,
    string IdentityFingerprint,
    long ResourceVersion,
    string CorrelationId,
    DateTime OccurredAt);

public interface IAdminIdentityAuditWriter
{
    Task AppendMicrosoftPreProvisionAsync(
        AdminIdentityAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>Re-resolves a Microsoft identity after a transaction-level unique conflict. Implementations must
/// use a fresh persistence context; the context that observed the failed insert is not a valid read source.</summary>
public interface IAdminIdentityRecoveryReader
{
    Task<ResolveResult> ResolveAfterConflictAsync(
        ProviderIdentity identity, CancellationToken cancellationToken);
}
