using SharedKernel;

namespace Merchants.Domain.Users;

public sealed class MerchantUserManagementAudit : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public Guid? TargetUserId { get; private set; }
    public Guid? InvitationId { get; private set; }
    public string Action { get; private set; } = default!;
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private MerchantUserManagementAudit() { }

    public static MerchantUserManagementAudit For(Guid merchantId, Guid? actorUserId, Guid? targetUserId,
        Guid? invitationId, string action, string correlationId, DateTime now)
    {
        if (merchantId == Guid.Empty || (targetUserId is null && invitationId is null))
            throw new ArgumentException("Merchant and target are required.");
        if (!Actions.All.Contains(action))
            throw new ArgumentException("Unknown management audit action.", nameof(action));
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new MerchantUserManagementAudit
        {
            Id = Guid.CreateVersion7(), MerchantId = merchantId, ActorUserId = actorUserId,
            TargetUserId = targetUserId, InvitationId = invitationId, Action = action,
            CorrelationId = correlationId.Trim(), OccurredAt = now,
        };
    }

    public static class Actions
    {
        public const string InviteCreate = "invite-create";
        public const string InviteRevoke = "invite-revoke";
        public const string InviteAccept = "invite-accept";
        public const string Reveal = "reveal";
        public const string Update = "update";
        public const string Approve = "approve";
        public const string Reject = "reject";
        public const string Suspend = "suspend";
        public const string Reactivate = "reactivate";
        public const string SetRoles = "set-roles";
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            InviteCreate, InviteRevoke, InviteAccept, Reveal, Update, Approve, Reject, Suspend, Reactivate, SetRoles,
        };
    }
}
