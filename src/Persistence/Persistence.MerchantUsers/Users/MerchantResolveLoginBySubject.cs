using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>rls-to-query-filter task 5 pre-bind read port (design.md "Pre-owner-bind READS vs WRITES") over
/// <c>MerchantUserDbContext</c>. Login-by-subject runs BEFORE any actor is bound (the whole point is
/// discovering which merchant, if any, this subject belongs to) — the default query filter
/// (<c>MerchantId==CurrentMerchant</c>, which for an unbound caller is <c>Guid.Empty</c>) would hide EVERY
/// row, including an already-approved user's real merchant. This is a sanctioned <c>IgnoreQueryFilters()</c>
/// narrow port (design.md §"Escape-hatch allowlist" — the Architecture.Tests bypass-primitive allowlist
/// names this file). Not exposed as a <c>Merchants.Application</c> port: <c>Persistence.MerchantUsers</c>
/// never references a module's Application/Infrastructure project — this stays a narrow, self-contained
/// internal type until a later task (8's cutover) wires a caller onto it.</summary>
internal interface IMerchantResolveLoginBySubject
{
    Task<MerchantLoginLookup?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);
}

internal sealed record MerchantLoginLookup(Guid MerchantUserId, string Email, Guid? MerchantId, UserStatus Status);

internal sealed class MerchantResolveLoginBySubject(MerchantUserDbContext db) : IMerchantResolveLoginBySubject
{
    public async Task<MerchantLoginLookup?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Subject == subject)
            .Select(u => new MerchantLoginLookup(u.Id, u.Email, u.MerchantId, u.Status))
            .FirstOrDefaultAsync(cancellationToken);
}
