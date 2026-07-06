using SharedKernel;

namespace Admin.Domain;

/// <summary>The audited admin actions (REQ-10.1). Constants so the writer and tests never drift on a string.</summary>
public static class AdminAuditAction
{
    public const string SelfProvision = "self-provision";
    public const string CreateScoped = "create-scoped";
    public const string AssignTenant = "assign-tenant";
    public const string UnassignTenant = "unassign-tenant";
    public const string Suspend = "suspend";
    // admin-account-management: lifecycle + session management (REQ-3.2, REQ-5.2).
    public const string Reactivate = "reactivate";
    public const string SessionRevoke = "session-revoke";
    // admin-account-management: org-profile edit (Position/Office/Level/Division FKs), targets an admin.
    public const string UpdateProfile = "update-profile";

    // Role RBAC events (admin-role-rbac REQ-10.1). Role CRUD targets a role (TargetRoleId); assign/unassign
    // targets an admin (TargetAdminId).
    public const string RoleCreated = "role-created";
    public const string RoleUpdated = "role-updated";
    public const string RoleDeleted = "role-deleted";
    public const string RoleAssigned = "role-assigned";
    public const string RoleUnassigned = "role-unassigned";
}

/// <summary>
/// Append-only record of an admin identity/assignment change with a typed actor tuple <c>(ActorType,
/// ActorId)</c> (REQ-10). Forward-only — the legacy RegistrationAudits/ProvisioningAudits are NOT rewritten
/// to this shape (decision-4); <see cref="AdminAccount.Subject"/> is the bridge to their string AdminSubject.
/// Control-plane (no tenant predicate); never holds a secret.
/// </summary>
public sealed class AdminAccountAudit : Entity<Guid>
{
    public string Action { get; private set; } = default!;

    /// <summary>The actor kind. Only <c>"admin"</c> in Spec 1 (intentional forward-compat for a future
    /// system/automation actor).</summary>
    public string ActorType { get; private set; } = default!;

    /// <summary>The acting <see cref="AdminAccount.Id"/>.</summary>
    public Guid ActorId { get; private set; }

    public Guid? TargetAdminId { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>The role a role-CRUD event acted on (REQ-10.2). NULL for non-role events.</summary>
    public Guid? TargetRoleId { get; private set; }

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAt { get; private set; }

    private AdminAccountAudit() { }

    private AdminAccountAudit(Guid id, string action, Guid actorId, Guid? targetAdminId, Guid? tenantId,
        Guid? targetRoleId, string correlationId, DateTime occurredAt) : base(id)
    {
        Action = action;
        ActorType = "admin";
        ActorId = actorId;
        TargetAdminId = targetAdminId;
        TenantId = tenantId;
        TargetRoleId = targetRoleId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row for one of <see cref="AdminAuditAction"/>. <paramref name="actorId"/> is the
    /// acting admin (for self-provision, the admin's own id); the optional target/tenant/role locate the subject.</summary>
    public static AdminAccountAudit For(string action, Guid actorId, string correlationId, DateTime occurredAt,
        Guid? targetAdminId = null, Guid? tenantId = null, Guid? targetRoleId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
        return new AdminAccountAudit(Guid.NewGuid(), action, actorId, targetAdminId, tenantId, targetRoleId, correlationId, occurredAt);
    }
}
