using Admins.Domain;
using BuildingBlocks.Application;

namespace Admins.Application;

/// <summary>
/// Persistence for the admin realm. Bound by the host to the pol_admin (RLS-bypass) connection — admin tables
/// are control-plane (no per-merchant predicate), and resolution/provisioning run cross-merchant. Lookups are by
/// subject (resolution), email (invite binding) and id (Super-only management); assignments back the
/// accessible-merchant set (REQ-6). The SFS-paged directory backs the account-management console
/// (admin-account-management REQ-1).
/// </summary>
public interface IPlatformUserRepository
{
    void Add(PlatformUser account);
    void AddAssignment(PlatformMerchantAccess assignment);
    void RemoveAssignment(PlatformMerchantAccess assignment);

    Task<PlatformUser?> GetBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<PlatformUser?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task<PlatformUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Cheap existence probe (<c>SELECT … EXISTS</c>) for the 404 gate on read/session handlers that
    /// need only to know the account exists, not its columns — avoids a full tracked-entity load.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken);
    Task<PlatformMerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken cancellationToken);

    /// <summary>The SFS-paged admin directory (admin-account-management REQ-1): filter/search/sort applied over
    /// the control-plane <c>PlatformUser</c> set, <c>Total</c> counted after filter/search but before paging.</summary>
    Task<PagedResult<PlatformUserListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken);
}

/// <summary>One admin directory row (admin-account-management REQ-1.2). <see cref="Tier"/>/<see cref="Status"/>
/// are the enums; the host projects them to lowercase wire strings (no global enum converter, B2).
/// <see cref="SubjectBound"/> = the invite has been claimed (Subject != null, REQ-1.2).</summary>
public sealed record PlatformUserListItem(
    Guid AdminId, string Email, PlatformUserTier Tier, AdminStatus Status, DateTime CreatedAt, bool SubjectBound);

/// <summary>Stages an append-only <see cref="PlatformUserAudit"/> in the current transaction (REQ-10.2).</summary>
public interface IPlatformUserAuditWriter
{
    void Append(PlatformUserAudit entry);
}

/// <summary>
/// Read-only merchant checks the admin module needs, implemented in the HOST over the pol_admin (bypass)
/// connection so Admins.Application stays free of a Merchant-module dependency (mirrors Identity's
/// <c>IMerchantDirectory</c>). <see cref="IsActiveMerchantAsync"/> validates a merchant at assignment time (REQ-4.3);
/// <see cref="GetCodesByIdsAsync"/> maps a Scoped admin's assigned ids to SPA-friendly codes for <c>GET
/// /admin/me</c> (REQ-13.3); <see cref="GetIdByCodeAsync"/> is the cheap code-&gt;id lookup the cross-merchant
/// read seam uses to apply the accessible-merchant floor BEFORE loading a full merchant projection (REQ-7.1).
/// </summary>
public interface IAdminMerchantDirectory
{
    Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken);
    Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken);
}
