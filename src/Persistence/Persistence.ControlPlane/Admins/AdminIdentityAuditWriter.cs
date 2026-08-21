using System.Globalization;
using System.Text.Json;
using Admins.Application.Users;
using Admins.Domain.Users;
using Governance.Domain;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Admins;

internal sealed class AdminIdentityAuditWriter(GovernanceAuditAppender audits)
    : IAdminIdentityAuditWriter
{
    public Task AppendMicrosoftPreProvisionAsync(
        AdminIdentityAuditEntry entry,
        CancellationToken cancellationToken) => audits.AppendAsync(
            GovernanceScopeKind.Platform,
            merchantId: null,
            entry.ActorAdminId,
            "admin.microsoft-identity.preprovisioned",
            "admin",
            entry.TargetAdminId.ToString("D"),
            "succeeded",
            JsonSerializer.Serialize(new
            {
                provider = User.MicrosoftProvider,
                reason = entry.Reason,
                subjectBoundBefore = false,
                subjectBoundAfter = true,
                fingerprint = entry.IdentityFingerprint,
            }),
            approvalId: null,
            $"v{entry.ResourceVersion.ToString(CultureInfo.InvariantCulture)}",
            entry.CorrelationId,
            entry.OccurredAt,
            cancellationToken);
}
