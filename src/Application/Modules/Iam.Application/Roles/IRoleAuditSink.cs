namespace Iam.Application.Roles;

/// <summary>
/// Optional per-request audit hook for role CRUD, invoked from INSIDE the same store transaction that writes
/// the role (before <c>SaveChangesAsync</c>) so a crash can never separate the write from its audit row.
/// <c>Iam.Application</c> must not reference <c>Admins.Application</c>'s <c>IAuditWriter</c>/<c>Audit</c>/
/// <c>AuditAction</c> types directly (module-reference rule — only <c>Iam.Domain</c> is a published
/// language), so this abstraction is the seam: the HOST registers one concrete sink that resolves the acting
/// admin from <c>IAdminScope</c> and no-ops when no admin is bound to the request — merchant-side role CRUD
/// has never been audited (the old Merchants catalog's own "not in REQ-21" note), so the same sink instance
/// naturally serves both consoles without either duplicating a null check.
/// </summary>
public interface IRoleAuditSink
{
    void RoleCreated(Guid roleId, string correlationId);
    void RoleUpdated(Guid roleId, string correlationId);
    void RoleDeleted(Guid roleId, string correlationId);
}

/// <summary>The default when nothing overrides the registration — every call is a no-op.</summary>
public sealed class NullRoleAuditSink : IRoleAuditSink
{
    public static readonly NullRoleAuditSink Instance = new();

    public void RoleCreated(Guid roleId, string correlationId) { }
    public void RoleUpdated(Guid roleId, string correlationId) { }
    public void RoleDeleted(Guid roleId, string correlationId) { }
}
