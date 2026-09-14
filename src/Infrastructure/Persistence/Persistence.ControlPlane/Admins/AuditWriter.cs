using Admins.Application.Users;
using Admins.Domain.Users;

namespace Persistence.ControlPlane.Admins;

/// <summary>Stages an append-only <see cref="Audit"/> in the current ControlPlane transaction (REQ-10.2).</summary>
internal sealed class AuditWriter : IAuditWriter
{
    private readonly ControlPlaneDbContext _db;

    public AuditWriter(ControlPlaneDbContext db) => _db = db;

    public void Append(Audit entry) => _db.UserAudits.Add(entry);
}
