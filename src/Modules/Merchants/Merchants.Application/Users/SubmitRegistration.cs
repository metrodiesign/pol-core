using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>The registration form fields (REQ-7.1). FirstName/LastName/IdentityType are required; the remaining
/// merchant-user detail fields are optional. Identity (subject/email/hd) is NEVER taken from here — it comes only
/// from the verified ticket (REQ-4.2).</summary>
public sealed record RegistrationForm(
    string FirstName,
    string LastName,
    IdentityType IdentityType,
    string? IdentityNumber = null,
    string? SaleCode = null,
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
    string Provider = ExternalLogin.Google,
    byte[]? KycPhotoBytes = null,
    string? KycPhotoContentType = null,
    Guid KycOperationId = default,
    Guid? InvitationId = null) : ICommand<SubmitRegistrationResult>;

public sealed record SubmitRegistrationResult(Guid UserId, UserStatus Status);

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
    private readonly IAccountStore _accounts;
    private readonly IExternalLoginRepository _logins;
    private readonly IRegistrationAuditWriter _audits;
    private readonly IRegistrationAttemptWriter _attempts;
    private readonly IRegistrationOutboxWriter _outbox;
    private readonly IRegistrationUnitOfWork _unitOfWork;
    private readonly IPhotoStore _photos;
    private readonly IInvitationRepository _invitations;
    private readonly IManagementAuditWriter _managementAudits;
    private readonly IClock _clock;

    // IAccountStore, never IUserRepository: this endpoint is anonymous (ticket-gated) — no actor is bound, and
    // the Correction branch's target row has a NULL MerchantId the filtered repository would hide
    // (bugfix-merchant-prebind-wiring F2).
    public SubmitRegistrationHandler(
        IAccountStore accounts,
        IExternalLoginRepository logins,
        IRegistrationAuditWriter audits,
        IRegistrationAttemptWriter attempts,
        IRegistrationOutboxWriter outbox,
        IRegistrationUnitOfWork unitOfWork,
        IPhotoStore photos,
        IInvitationRepository invitations,
        IManagementAuditWriter managementAudits,
        IClock clock)
    {
        _accounts = accounts;
        _logins = logins;
        _audits = audits;
        _attempts = attempts;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _photos = photos;
        _invitations = invitations;
        _managementAudits = managementAudits;
        _clock = clock;
    }

    public SubmitRegistrationHandler(
        IAccountStore accounts,
        IExternalLoginRepository logins,
        IRegistrationAuditWriter audits,
        IRegistrationAttemptWriter attempts,
        IRegistrationOutboxWriter outbox,
        IRegistrationUnitOfWork unitOfWork,
        IPhotoStore photos,
        IClock clock)
        : this(accounts, logins, audits, attempts, outbox, unitOfWork, photos,
            NoInvitationRepository.Instance, NoManagementAuditWriter.Instance, clock) { }

    private sealed class NoInvitationRepository : IInvitationRepository
    {
        public static readonly NoInvitationRepository Instance = new();
        public Task<MerchantUserInvitation?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<MerchantUserInvitation?>(null);
        public Task<MerchantUserInvitation?> FindPendingByNormalizedEmailAsync(string email, CancellationToken ct) => Task.FromResult<MerchantUserInvitation?>(null);
        public Task<MerchantUserInvitation?> FindByTokenHashUnfilteredAsync(string hash, CancellationToken ct) => Task.FromResult<MerchantUserInvitation?>(null);
        public Task<MerchantUserInvitation?> FindByIdUnfilteredAsync(Guid id, CancellationToken ct) => Task.FromResult<MerchantUserInvitation?>(null);
        public void Add(MerchantUserInvitation invitation) => throw new NotSupportedException();
    }

    private sealed class NoManagementAuditWriter : IManagementAuditWriter
    {
        public static readonly NoManagementAuditWriter Instance = new();
        public void Append(MerchantUserManagementAudit audit) { }
    }

    public async ValueTask<SubmitRegistrationResult> Handle(
        SubmitRegistrationCommand command, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var hasKycBytes = command.KycPhotoBytes is { Length: > 0 };
        var hasKycContentType = !string.IsNullOrWhiteSpace(command.KycPhotoContentType);
        if (hasKycBytes != hasKycContentType)
            throw new ArgumentException("KYC photo bytes and content type must be supplied together.");

        // Staging must happen exactly once outside the execution-strategy transaction delegate. A retry of the
        // same signed registration ticket carries the same operation id and therefore resolves to the same key —
        // PutStagedAsync reports whether THIS call created it, so the failure cleanup below never discards a key
        // a different (possibly already-committed) attempt is relying on (see stagedKycCreatedNew below).
        string? stagedKycKey = null;
        var stagedKycCreatedNew = false;
        if (command.KycPhotoBytes is { Length: > 0 } kycBytes && command.KycPhotoContentType is not null)
        {
            if (command.KycOperationId == Guid.Empty)
                throw new ArgumentException("A KYC operation id is required when a KYC photo is supplied.");
            (stagedKycKey, stagedKycCreatedNew) = await _photos.PutStagedAsync(
                command.KycOperationId, kycBytes, command.KycPhotoContentType, cancellationToken);
        }

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                User account;
                string action;

                if (command.Purpose == TicketPurpose.Registration)
                {
                    // First-time applicant: create the account (with its person details) + external login (REQ-4.1). The
                    // wire ticket is stateless; a duplicate subject (a replayed still-valid token or a concurrent second
                    // tab) violates the unique (Subject)/(Provider,Subject) index and the unit of work turns it into a
                    // 409 (REQ-4.6/S9).
                    if (await _accounts.FindBySubjectAsync(command.Provider, command.Subject, ct) is not null)
                        throw new ConflictException(
                            "A registration already exists for this identity.", "already-registered");

                    MerchantUserInvitation? invitation = null;
                    if (command.InvitationId is { } invitationId)
                    {
                        invitation = await _invitations.FindByIdUnfilteredAsync(invitationId, ct)
                            ?? throw new InvalidRequestException(
                                "The invitation is invalid or expired.", "invitation-invalid");
                        if (invitation.AcceptedAt is not null)
                            throw new ConflictException(
                                "The invitation has already been used.", "invitation-used");
                        if (!invitation.IsPending(now)
                            || !string.Equals(invitation.NormalizedEmail,
                                MerchantUserInvitation.NormalizeEmail(command.Email), StringComparison.Ordinal))
                            throw new InvalidRequestException(
                                "The invitation is invalid or expired.", "invitation-invalid");
                    }

                    account = invitation is null
                        ? User.Register(command.Provider, command.Subject, command.Email, now)
                        : User.RegisterInvited(command.Provider, command.Subject, command.Email, invitation.MerchantId, now);
                    _accounts.Add(account);
                    _logins.Add(ExternalLogin.Create(command.Subject, account.Id, command.Provider));
                    if (invitation is not null)
                    {
                        invitation.Accept(account.Id, command.Email, now);
                        _managementAudits.Append(MerchantUserManagementAudit.For(
                            invitation.MerchantId, null, account.Id, invitation.Id,
                            MerchantUserManagementAudit.Actions.InviteAccept, command.CorrelationId, now));
                    }
                    action = RegistrationAuditAction.Registered;
                }
                else
                {
                    // Correction resubmission (REQ-5.3/5.4/5.5): edit the EXISTING record bound to the subject —
                    // never a second user/login. Resubmit() enforces the source state is Rejected (else throws → 409).
                    account = await _accounts.FindBySubjectAsync(command.Provider, command.Subject, ct)
                        ?? throw new InvalidOperationException("No registration exists for this subject to correct.");
                    account.Resubmit(now);
                    action = RegistrationAuditAction.Resubmitted;
                }

                ApplyForm(account, command.Form);
                await ApplyPhotoAsync(account, command, ct);

                if (stagedKycKey is not null)
                {
                    var oldKycKey = account.KycPhotoObjectKey;
                    account.SetKycPhoto(stagedKycKey);
                    _outbox.Enqueue(new KycPhotoLifecycleRequested(
                        stagedKycKey,
                        string.Equals(oldKycKey, stagedKycKey, StringComparison.Ordinal) ? null : oldKycKey));
                }

                // Per-submit snapshot in the SAME transaction (registration-attempt-history REQ-1.1/1.8): frozen from
                // the account AFTER ApplyForm/ApplyPhoto (trimmed values, this attempt's photo key), Email from the
                // verified ticket. An AttemptNo race is settled by the unique index at SaveChanges → 409 (REQ-1.9).
                var attemptNo = await _attempts.NextAttemptNoAsync(account.Id, ct);
                _attempts.Add(RegistrationAttempt.Capture(account, attemptNo, command.Purpose, command.Email, now));

                // Audit + outbox event in the SAME transaction (REQ-20.2/21.1). The event carries a sentinel merchant
                // (stamped by the writer); no actor subject — this is a self-service action. DisplayName is the
                // domain-computed value, not a form field.
                _audits.Append(RegistrationAudit.For(action, account.Id, command.Subject, command.CorrelationId, now));
                _outbox.Enqueue(new MerchantUserRegistrationSubmitted(account.Id, now));

                await _unitOfWork.SaveChangesAsync(ct);
                return new SubmitRegistrationResult(account.Id, UserStatus.PendingApproval);
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            // Only discard a key THIS call staged fresh. A retry that resolved to an already-staged (or already
            // committed) key from a different attempt must never delete it — that attempt may have already
            // committed its row/outbox event and depends on the object still being there (Codex review #191:
            // a retried request whose earlier attempt already succeeded was deleting the winning attempt's
            // still-staged KYC object out from under it).
            if (stagedKycKey is not null && stagedKycCreatedNew)
            {
                try
                {
                    await _photos.DiscardStagedAsync(stagedKycKey, CancellationToken.None);
                }
                catch
                {
                    // Best effort only. Store-side 24-hour staging TTL is the crash-safe cleanup floor.
                }
            }

            if (command.InvitationId is not null && exception is ConcurrencyConflictException)
                throw new ConflictException(
                    "The invitation was consumed concurrently.", "invitation-used", exception);
            if (command.InvitationId is not null && exception is ConflictException { Code: null })
                throw new ConflictException(
                    "The invitation was consumed concurrently.", "invitation-used", exception);
            if (command.InvitationId is null && exception is ConflictException { Code: null })
                throw new ConflictException(
                    "A registration already exists for this identity.", "already-registered", exception);
            throw;
        }
    }

    private static void ApplyForm(User account, RegistrationForm form) =>
        account.SetDetails(form.FirstName, form.LastName, form.IdentityType, form.IdentityNumber,
            form.SaleCode, form.LicenseNumber, form.Phone);

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
