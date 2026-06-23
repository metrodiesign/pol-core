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

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAtUtc { get; private set; }

    private AdminAccountAudit() { }

    private AdminAccountAudit(Guid id, string action, Guid actorId, Guid? targetAdminId, Guid? tenantId,
        string correlationId, DateTime occurredAtUtc) : base(id)
    {
        Action = action;
        ActorType = "admin";
        ActorId = actorId;
        TargetAdminId = targetAdminId;
        TenantId = tenantId;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>Builds an audit row for one of <see cref="AdminAuditAction"/>. <paramref name="actorId"/> is the
    /// acting admin (for self-provision, the admin's own id); the optional target/tenant locate the subject.</summary>
    public static AdminAccountAudit For(string action, Guid actorId, string correlationId, DateTime occurredAtUtc,
        Guid? targetAdminId = null, Guid? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
        return new AdminAccountAudit(Guid.NewGuid(), action, actorId, targetAdminId, tenantId, correlationId, occurredAtUtc);
    }
}
