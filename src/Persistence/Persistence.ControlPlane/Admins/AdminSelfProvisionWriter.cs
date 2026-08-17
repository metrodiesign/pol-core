using Admins.Domain.Users;
using Microsoft.EntityFrameworkCore;
using SharedKernel;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// rls-to-query-filter task 5 (design.md "Pre-owner-bind READS vs WRITES" — <c>ISelfProvisionSuperWriter</c>,
/// gated by the bootstrap Subject allowlist). Self-contained inside <c>Persistence.ControlPlane</c> — the
/// allowlist gate itself is enforced by the host BEFORE this port is called (mirrors
/// the deleted <c>AdminResolveLoginBySubject</c>'s trust model: this port trusts an already-verified Subject).
/// <c>admin.Users</c> carries no query filter (control-plane, no merchant predicate — REQ-3.2), so no
/// <c>IgnoreQueryFilters()</c> escape hatch is needed here, unlike the MerchantUser-side write ports. Idempotent
/// on a concurrent first-login race via the unique <c>(Provider, Subject)</c> index (mirrors
/// <c>SelfProvisionSuperHandler</c>'s catch-and-reread pattern, one level lower). Scoped to ONLY the
/// <c>admin.Users</c> insert; role-assignment/audit compose around this port at the transaction-orchestration
/// layer (task 8's wiring), same discipline as the MerchantUser approve/reject ports.
/// </summary>
internal readonly record struct SelfProvisionOutcome(Guid AdminId, string Email, bool AlreadyExisted);

internal interface ISelfProvisionSuperWriter
{
    Task<SelfProvisionOutcome> ProvisionAsync(ProviderIdentity identity, string email, DateTime now, CancellationToken cancellationToken);
}

internal sealed class AdminSelfProvisionWriter(ControlPlaneDbContext db) : ISelfProvisionSuperWriter
{
    public async Task<SelfProvisionOutcome> ProvisionAsync(
        ProviderIdentity identity, string email, DateTime now, CancellationToken cancellationToken)
    {
        var (provider, subject) = identity;
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var existing = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Provider == provider && u.Subject == subject, cancellationToken);
        if (existing is not null)
            return new SelfProvisionOutcome(existing.Id, existing.Email, AlreadyExisted: true);

        var account = User.SelfProvision(provider, subject, email, now);
        db.Users.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent first-login race: the other request's insert won. Re-read the winning row so both
            // callers resolve the single account (mirrors SelfProvisionSuperHandler's ConflictException catch).
            var raced = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Provider == provider && u.Subject == subject, cancellationToken);
            if (raced is null)
                throw;
            return new SelfProvisionOutcome(raced.Id, raced.Email, AlreadyExisted: true);
        }

        return new SelfProvisionOutcome(account.Id, account.Email, AlreadyExisted: false);
    }
}
