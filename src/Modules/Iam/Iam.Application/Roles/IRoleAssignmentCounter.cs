namespace Iam.Application.Roles;

/// <summary>
/// Counts assignment rows bound to a role across BOTH consoles' assignment tables
/// (<c>admin.RoleAssignments</c> / <c>merch.RoleAssignments</c>). Those tables belong to
/// <c>Admins.Domain</c>/<c>Merchants.Domain</c>, which <c>Iam.Application</c> must not reference
/// (module-reference rule — only <c>Iam.Domain</c> is a published language), so the host provides the
/// concrete implementation over ordinary EF queries against both entity types (mirrors
/// <see cref="IRoleAuditSink"/>'s bridge pattern). A role is undeletable while ANY side still assigns it
/// (REQ-6.3); list/get responses carry the count for the UI's bound-user column.
/// Counts are scoped to the caller's <see cref="RoleSideContext"/>: the Platform console sees the global
/// count (both tables), a Merchant console sees only its OWN merchant's assignment rows — a shared role's
/// cross-tenant total would otherwise leak how many users other merchants have (REQ-3.6).
/// </summary>
public interface IRoleAssignmentCounter
{
    Task<int> CountAsync(RoleSideContext context, Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
        RoleSideContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
}
