using Admins.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>rls-to-query-filter task 5 pre-bind read port (design.md "Pre-owner-bind READS vs WRITES") over
/// <c>ControlPlaneDbContext</c>. No query filter to suppress — <c>admin.Users</c> carries none (control-plane,
/// cross-merchant by design). Not exposed as an <c>Admins.Application</c> port: <c>Persistence.ControlPlane</c>
/// never references a module's Application/Infrastructure project (compile-time boundary, REQ-11.8's sibling
/// rule for the control-plane side) — this stays a narrow, self-contained internal type until a later task
/// (8's cutover) wires a caller onto it.</summary>
internal interface IAdminResolveLoginBySubject
{
    Task<AdminLoginLookup?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
}

internal sealed record AdminLoginLookup(Guid AdminId, string Email, Tier Tier, UserStatus Status);

internal sealed class AdminResolveLoginBySubject(ControlPlaneDbContext db) : IAdminResolveLoginBySubject
{
    public async Task<AdminLoginLookup?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Subject == subject)
            .Select(u => new AdminLoginLookup(u.Id, u.Email, u.Tier, u.Status))
            .FirstOrDefaultAsync(cancellationToken);
}
