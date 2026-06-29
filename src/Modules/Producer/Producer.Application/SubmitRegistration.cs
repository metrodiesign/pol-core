using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>The registration form fields (REQ-7.1). DisplayName is required; the producer detail fields are optional
/// (format validation deferred — open question in the spec). Identity (subject/email/hd) is NEVER taken from here —
/// it comes only from the verified ticket (REQ-4.2).</summary>
public sealed record RegistrationForm(
    string DisplayName,
    string? FirstName = null,
    string? LastName = null,
    PersonType? PersonType = null,
    string? IdNumber = null,
    string? ProducerCode = null,
    string? LicenseNumber = null,
    string? Phone = null);

/// <summary>
/// Submits (Registration) or resubmits (Correction) a producer registration (REQ-4/5/7). Identity fields are the
/// ticket's verified values, captured at the callback from the Google id_token — the host unprotects the wire
/// ticket and passes them here; the form body cannot override them (REQ-4.2). The photo, if any, has already been
/// type/magic-byte/size validated at the host (REQ-7.3/7.4) and arrives as raw bytes + the canonical content-type.
/// NOT <c>ITenantScoped</c>: registration runs tenant-less on the pol_admin connection (REQ-19.2).
/// </summary>
public sealed record SubmitRegistrationCommand(
    Guid TicketId,
    string Subject,
    string Email,
    string? HostedDomain,
    TicketPurpose Purpose,
    RegistrationForm Form,
    byte[]? PhotoBytes,
    string? PhotoContentType,
    string CorrelationId) : ICommand<SubmitRegistrationResult>;

public sealed record SubmitRegistrationResult(Guid TenantUserId, ProducerAccountStatus Status);

/// <summary>
/// Handles registration + correction in ONE pol_admin transaction (REQ-4.1/5.3): consume the ticket (conditional
/// UPDATE, exactly one winner — REQ-3.3), then for a first-time Registration create ProducerAccount(Pending)+ExternalLogin
/// +Profile, or for a Correction load the existing account (must be Rejected), update its Profile and Resubmit it
/// (Rejected→Pending). An audit row and a <see cref="TenantUserRegistrationSubmitted"/> outbox event are enqueued in
/// the same transaction (REQ-20/21). A duplicate subject surfaces as a 409 via the unit of work (S9).
/// </summary>
public sealed class SubmitRegistrationHandler : ICommandHandler<SubmitRegistrationCommand, SubmitRegistrationResult>
{
    private readonly IProducerAccountRepository _accounts;
    private readonly IExternalLoginRepository _logins;
    private readonly IRegistrationTicketRepository _tickets;
    private readonly ITenantUserProfileRepository _profiles;
    private readonly IRegistrationAuditWriter _audits;
    private readonly IProducerOutboxWriter _outbox;
    private readonly IProducerRegistrationUnitOfWork _unitOfWork;
    private readonly IPhotoStore _photos;
    private readonly IClock _clock;

    public SubmitRegistrationHandler(
        IProducerAccountRepository accounts,
        IExternalLoginRepository logins,
        IRegistrationTicketRepository tickets,
        ITenantUserProfileRepository profiles,
        IRegistrationAuditWriter audits,
        IProducerOutboxWriter outbox,
        IProducerRegistrationUnitOfWork unitOfWork,
        IPhotoStore photos,
        IClock clock)
    {
        _accounts = accounts;
        _logins = logins;
        _tickets = tickets;
        _profiles = profiles;
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
            // 1) Single-use ticket consume (REQ-3.3/3.6/4.1): the conditional UPDATE is the replay authority — a
            // valid wire signature alone is replayable until expiry. A miss (used/expired/unknown/wrong-purpose)
            // is a bad request (400), no row created/changed.
            if (!await _tickets.TryConsumeAsync(command.TicketId, command.Purpose, now, ct))
                throw new ArgumentException("The registration ticket is invalid, expired, already used, or of the wrong purpose.");

            Guid tenantUserId;
            string action;

            if (command.Purpose == TicketPurpose.Registration)
            {
                // 2a) First-time applicant: create user + external login + profile (REQ-4.1). A duplicate subject
                // (incl. a concurrent second tab) violates the unique (Subject)/(Provider,Subject) index and the
                // unit of work turns it into a 409 (REQ-4.6/S9).
                var account = ProducerAccount.Register(command.Subject, command.Email, now);
                _accounts.Add(account);
                _logins.Add(ExternalLogin.Create(command.Subject, account.Id));

                var profile = TenantUserProfile.Create(account.Id, command.Form.DisplayName);
                ApplyForm(profile, command.Form);
                await ApplyPhotoAsync(profile, command, ct);
                _profiles.Add(profile);

                tenantUserId = account.Id;
                action = RegistrationAuditAction.Registered;
            }
            else
            {
                // 2b) Correction resubmission (REQ-5.3/5.4/5.5): edit the EXISTING record bound to the subject —
                // never a second user/login. Resubmit() enforces the source state is Rejected (else throws → 409).
                var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                    ?? throw new InvalidOperationException("No registration exists for this subject to correct.");
                account.Resubmit(now);

                var profile = await _profiles.FindByProducerAccountIdAsync(account.Id, ct)
                    ?? throw new InvalidOperationException("The registration has no profile to correct.");
                profile.SetDetails(command.Form.DisplayName, command.Form.FirstName, command.Form.LastName,
                    command.Form.PersonType, command.Form.IdNumber, command.Form.ProducerCode,
                    command.Form.LicenseNumber, command.Form.Phone);
                await ApplyPhotoAsync(profile, command, ct);

                tenantUserId = account.Id;
                action = RegistrationAuditAction.Resubmitted;
            }

            // 3) Audit + outbox event in the SAME transaction (REQ-20.2/21.1). The event carries a sentinel tenant
            // (stamped by the writer); no actor subject — this is a self-service action.
            _audits.Append(RegistrationAudit.For(action, command.Subject, command.CorrelationId, now));
            _outbox.Enqueue(new TenantUserRegistrationSubmitted(
                tenantUserId, command.Subject, command.Email, command.HostedDomain, command.Form.DisplayName, now));

            await _unitOfWork.SaveChangesAsync(ct);
            return new SubmitRegistrationResult(tenantUserId, ProducerAccountStatus.PendingApproval);
        }, cancellationToken);
    }

    private static void ApplyForm(TenantUserProfile profile, RegistrationForm form) =>
        profile.SetDetails(form.DisplayName, form.FirstName, form.LastName, form.PersonType, form.IdNumber,
            form.ProducerCode, form.LicenseNumber, form.Phone);

    private async Task ApplyPhotoAsync(TenantUserProfile profile, SubmitRegistrationCommand command, CancellationToken ct)
    {
        if (command.PhotoBytes is not { Length: > 0 } bytes || command.PhotoContentType is null)
            return;
        // ponytail: PutAsync happens after the ticket consume but before SaveChanges. The only way to orphan a
        // stored blob is a duplicate-subject 409 racing AFTER a successful consume+put — vanishingly rare; a
        // store sweeper (or prod object-store lifecycle policy) reclaims it. Not worth a compensating delete here.
        var key = await _photos.PutAsync(bytes, command.PhotoContentType, ct);
        profile.SetPhoto(key, command.PhotoContentType);
    }
}
