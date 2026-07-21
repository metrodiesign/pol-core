using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>The registration form fields (REQ-7.1). FirstName/LastName are required (they compose the DisplayName);
/// the remaining merchant-user detail fields are optional (format validation deferred — open question in the spec).
/// Identity (subject/email/hd) is NEVER taken from here — it comes only from the verified ticket (REQ-4.2).</summary>
public sealed record RegistrationForm(
    string FirstName,
    string LastName,
    PersonType? PersonType = null,
    string? IdNumber = null,
    string? ProducerCode = null,
    string? LicenseNumber = null,
    string? Phone = null);

/// <summary>
/// Submits (Registration) or resubmits (Correction) a merchant-user registration (REQ-4/5/7). Identity fields are the
/// ticket's verified values, captured at the callback from the Google id_token — the host unprotects the wire
/// ticket and passes them here; the form body cannot override them (REQ-4.2). The photo, if any, has already been
/// type/magic-byte/size validated at the host (REQ-7.3/7.4) and arrives as raw bytes + the canonical content-type.
/// NOT <c>IMerchantScoped</c>: registration runs merchant-less on the pol_admin connection (REQ-19.2).
/// </summary>
public sealed record SubmitRegistrationCommand(
    string Subject,
    string Email,
    string? HostedDomain,
    TicketPurpose Purpose,
    RegistrationForm Form,
    byte[]? PhotoBytes,
    string? PhotoContentType,
    string CorrelationId,
    string Provider = ExternalLogin.Google) : ICommand<SubmitRegistrationResult>;

public sealed record SubmitRegistrationResult(Guid MerchantUserId, UserStatus Status);

/// <summary>
/// Handles registration + correction in ONE pol_admin transaction (REQ-4.1/5.3): for a first-time Registration create
/// User(Pending)+ExternalLogin+Profile, or for a Correction load the existing account (must be Rejected),
/// update its Profile and Resubmit it (Rejected→Pending). The wire ticket is a stateless signed+time-limited token
/// (verified at the host); replay/duplicate safety is the account's unique (Subject)/(Provider,Subject) index — a
/// second submission surfaces as a 409 via the unit of work (S9), and Correction's Resubmit() enforces Rejected-only.
/// An audit row and a <see cref="MerchantUserRegistrationSubmitted"/> outbox event are enqueued in the same transaction
/// (REQ-20/21).
/// </summary>
public sealed class SubmitRegistrationHandler : ICommandHandler<SubmitRegistrationCommand, SubmitRegistrationResult>
{
    private readonly IUserRepository _accounts;
    private readonly IExternalLoginRepository _logins;
    private readonly IRegistrationAuditWriter _audits;
    private readonly IRegistrationOutboxWriter _outbox;
    private readonly IRegistrationUnitOfWork _unitOfWork;
    private readonly IPhotoStore _photos;
    private readonly IClock _clock;

    public SubmitRegistrationHandler(
        IUserRepository accounts,
        IExternalLoginRepository logins,
        IRegistrationAuditWriter audits,
        IRegistrationOutboxWriter outbox,
        IRegistrationUnitOfWork unitOfWork,
        IPhotoStore photos,
        IClock clock)
    {
        _accounts = accounts;
        _logins = logins;
        _audits = audits;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _photos = photos;
        _clock = clock;
    }

    public async ValueTask<SubmitRegistrationResult> Handle(
        SubmitRegistrationCommand command, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            Guid merchantUserId;
            string action;
            string displayName;

            if (command.Purpose == TicketPurpose.Registration)
            {
                // First-time applicant: create the account (with its person details) + external login (REQ-4.1). The
                // wire ticket is stateless; a duplicate subject (a replayed still-valid token or a concurrent second
                // tab) violates the unique (Subject)/(Provider,Subject) index and the unit of work turns it into a
                // 409 (REQ-4.6/S9).
                var account = User.Register(command.Subject, command.Email, now);
                _accounts.Add(account);
                _logins.Add(ExternalLogin.Create(command.Subject, account.Id, command.Provider));

                ApplyForm(account, command.Form);
                await ApplyPhotoAsync(account, command, ct);

                merchantUserId = account.Id;
                action = RegistrationAuditAction.Registered;
                displayName = account.DisplayName;
            }
            else
            {
                // Correction resubmission (REQ-5.3/5.4/5.5): edit the EXISTING record bound to the subject —
                // never a second user/login. Resubmit() enforces the source state is Rejected (else throws → 409).
                var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                    ?? throw new InvalidOperationException("No registration exists for this subject to correct.");
                account.Resubmit(now);

                ApplyForm(account, command.Form);
                await ApplyPhotoAsync(account, command, ct);

                merchantUserId = account.Id;
                action = RegistrationAuditAction.Resubmitted;
                displayName = account.DisplayName;
            }

            // Audit + outbox event in the SAME transaction (REQ-20.2/21.1). The event carries a sentinel merchant
            // (stamped by the writer); no actor subject — this is a self-service action. DisplayName is the
            // domain-computed value, not a form field.
            _audits.Append(RegistrationAudit.For(action, command.Subject, command.CorrelationId, now));
            _outbox.Enqueue(new MerchantUserRegistrationSubmitted(
                merchantUserId, command.Subject, command.Email, command.HostedDomain, displayName, now));

            await _unitOfWork.SaveChangesAsync(ct);
            return new SubmitRegistrationResult(merchantUserId, UserStatus.PendingApproval);
        }, cancellationToken);
    }

    private static void ApplyForm(User account, RegistrationForm form) =>
        account.SetDetails(form.FirstName, form.LastName, form.PersonType, form.IdNumber,
            form.ProducerCode, form.LicenseNumber, form.Phone);

    private async Task ApplyPhotoAsync(User account, SubmitRegistrationCommand command, CancellationToken ct)
    {
        if (command.PhotoBytes is not { Length: > 0 } bytes || command.PhotoContentType is null)
            return;
        // ponytail: PutAsync happens before SaveChanges. The only way to orphan a stored blob is a duplicate-subject
        // 409 racing AFTER a successful put — vanishingly rare; a store sweeper (or prod object-store lifecycle
        // policy) reclaims it. Not worth a compensating delete here.
        var key = await _photos.PutAsync(bytes, command.PhotoContentType, ct);
        account.SetPhoto(key, command.PhotoContentType);
    }
}
