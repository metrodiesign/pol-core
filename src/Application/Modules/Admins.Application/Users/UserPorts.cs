using Admins.Domain.Users;

namespace Admins.Application.Users;

/// <summary>Stages an append-only <see cref="Audit"/> in the current transaction (REQ-10.2). The admin audit
/// sink (admin.UserAudits) stays live for the canonical /api/v1/accounts/* and role endpoints.</summary>
public interface IAuditWriter
{
    void Append(Audit entry);
}
