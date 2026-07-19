using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// rls-to-query-filter task 5 (design.md "Pre-owner-bind READS vs WRITES" — <c>IRegistrationWriter</c>,
/// anonymous, gated by a verified registration ticket at the host). Self-contained inside
/// <c>Persistence.MerchantUsers</c> (same assembly-boundary constraint as
/// <see cref="MerchantRegistrationWriter"/>). Mirrors <c>SubmitRegistrationHandler</c>'s two branches
/// (Registration/Correction) one level lower, but verifies its own trust root by STATE rather than trusting
/// the caller's ticket purpose: no existing row for the Subject -&gt; Registration; an existing row -&gt; only
/// a Rejected one may Resubmit. A registration/correction row's <c>MerchantId</c> is always NULL (Approve is
/// the only writer of that column), so — like the approve/reject ports — the pre-bind lookup by Subject needs
/// <c>IgnoreQueryFilters()</c>: an unbound (anonymous) caller's <c>CurrentMerchant</c> is
/// <see cref="Guid.Empty"/>, which never matches a NULL <c>MerchantId</c> row either.
/// <para>
/// Scoped to the User + ExternalLogin rows only (the "exact entity/state allowlist" this registration write
/// touches). Photo bytes are stored externally BEFORE this call (<c>IPhotoStore</c>, not a DB concern — the
/// caller passes the already-stored object key); audit + outbox enqueue compose around this port at the
/// transaction-orchestration layer (task 8's wiring), same discipline as the approve/reject ports.
/// </para>
/// </summary>
internal sealed record RegistrationWrite(
    string Subject,
    string Email,
    bool IsCorrection,
    string FirstName,
    string LastName,
    PersonType? PersonType,
    string? IdNumber,
    string? ProducerCode,
    string? LicenseNumber,
    string? Phone,
    string? PhotoObjectKey,
    string? PhotoContentType,
    DateTime Now);

internal enum RegistrationWriteOutcome
{
    Registered,
    Resubmitted,
    DuplicateSubject,
    CorrectionTargetNotFound,
    CorrectionTargetNotRejected,
}

internal sealed record RegistrationWriteResult(RegistrationWriteOutcome Outcome, Guid? MerchantUserId, string? DisplayName);

internal interface IRegistrationWriter
{
    Task<RegistrationWriteResult> SubmitAsync(RegistrationWrite write, CancellationToken cancellationToken);
}

internal sealed class MerchantRegistrationSubmitWriter(MerchantUserDbContext db) : IRegistrationWriter
{
    public async Task<RegistrationWriteResult> SubmitAsync(RegistrationWrite write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Email);

        var existing = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Subject == write.Subject, cancellationToken);

        if (!write.IsCorrection)
        {
            if (existing is not null)
                return new RegistrationWriteResult(RegistrationWriteOutcome.DuplicateSubject, existing.Id, null);

            var account = User.Register(write.Subject, write.Email, write.Now);
            db.Users.Add(account);
            db.ExternalLogins.Add(ExternalLogin.Create(write.Subject, account.Id));
            ApplyForm(account, write);

            await db.SaveChangesAsync(cancellationToken);
            return new RegistrationWriteResult(RegistrationWriteOutcome.Registered, account.Id, account.DisplayName);
        }

        if (existing is null)
            return new RegistrationWriteResult(RegistrationWriteOutcome.CorrectionTargetNotFound, null, null);
        if (existing.Status != UserStatus.Rejected)
            return new RegistrationWriteResult(RegistrationWriteOutcome.CorrectionTargetNotRejected, existing.Id, null);

        existing.Resubmit(write.Now);
        ApplyForm(existing, write);

        await db.SaveChangesAsync(cancellationToken);
        return new RegistrationWriteResult(RegistrationWriteOutcome.Resubmitted, existing.Id, existing.DisplayName);
    }

    private static void ApplyForm(User account, RegistrationWrite write)
    {
        account.SetDetails(write.FirstName, write.LastName, write.PersonType,
            write.IdNumber, write.ProducerCode, write.LicenseNumber, write.Phone);
        if (write.PhotoObjectKey is not null)
            account.SetPhoto(write.PhotoObjectKey, write.PhotoContentType);
    }
}
