using Admins.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// rls-to-query-filter task 5 (design.md "Pre-owner-bind READS vs WRITES" — <c>IBindInvitedAdminIdentity</c>,
/// gated by the invited-admin email allowlist + verified Subject). Binds a Google subject to an invited
/// Scoped account on its first login (mirrors <c>Admins.Domain.Users.User.BindSubject</c> / the existing
/// <c>BindInvitedHandler</c>, one level lower). <c>admin.Users</c> carries no query filter (control-plane, no
/// merchant predicate), so a plain tracked lookup-then-mutate is sufficient — no bypass primitive, no
/// allowlist entry needed (unlike the MerchantUser-side write ports, which suppress a merchant filter).
/// </summary>
internal enum BindInvitedOutcome
{
    Bound,
    NoInviteFound,
    AlreadyBound,
}

internal interface IBindInvitedAdminIdentity
{
    Task<BindInvitedOutcome> BindAsync(string provider, string subject, string email, CancellationToken cancellationToken);
}

internal sealed class AdminBindInvitedIdentityWriter(ControlPlaneDbContext db) : IBindInvitedAdminIdentity
{
    public async Task<BindInvitedOutcome> BindAsync(string provider, string subject, string email, CancellationToken cancellationToken)
    {
        if (string.Equals(provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Microsoft identities cannot use the historical invite-bind writer.", nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var account = await db.Users.FirstOrDefaultAsync(
            u => u.TenantId == null && u.Email == email, cancellationToken);
        if (account is null)
            return BindInvitedOutcome.NoInviteFound;
        if (account.Subject is not null)
            return BindInvitedOutcome.AlreadyBound;

        account.BindSubject(provider, subject);
        await db.SaveChangesAsync(cancellationToken);
        return BindInvitedOutcome.Bound;
    }
}
