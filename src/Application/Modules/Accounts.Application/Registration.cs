using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounts.Domain;
using BuildingBlocks.Application;

namespace Accounts.Application;

public sealed record RegistrationDraftRequest(
    string SaleCode,
    string Email,
    string PhoneNumber,
    JsonElement Profile);

/// <summary>Uploaded photo object keys to attach to the draft (the API validates bytes and stores them first).</summary>
public sealed record RegistrationPhotos(
    string PhotoObjectKey,
    string PhotoContentType,
    string? KycPhotoObjectKey,
    string? KycPhotoContentType);

/// <summary>The applicant's own case: draft fields ride along so the SPA can prefill after a rejection.</summary>
public sealed record RegistrationCaseView(
    Guid RegistrationId,
    Guid MerchantId,
    AgentRegistrationStatus Status,
    int CurrentAttemptNo,
    Guid? CurrentAttemptId,
    string? RejectionReason,
    long Version,
    string SaleCode,
    string Email,
    string PhoneNumber,
    JsonElement Profile,
    bool HasPhoto,
    bool HasKycPhoto,
    bool PhoneVerified);

public sealed record RegistrationAttemptView(
    Guid AttemptId,
    int AttemptNo,
    AgentRegistrationAttemptStatus Status,
    DateTime SubmittedAt,
    string? RejectionReason,
    long Version,
    string? SaleCode,
    string? Email,
    string? PhoneNumber,
    string? ProfileJson,
    string? InternalReviewNote,
    Guid? DecidedByAccountId,
    DateTime? DecidedAt,
    bool HasPhoto = false,
    bool HasKycPhoto = false);

public sealed record RegistrationSubmitResult(RegistrationCaseView Registration, RegistrationAttemptView Attempt, bool Replayed);

public sealed record RegistrationDecisionResult(
    RegistrationCaseView Registration,
    RegistrationAttemptView Attempt,
    bool Replayed);

public sealed record ContactVerificationIssue(ContactVerification Verification, string Code);
public sealed record ContactVerificationConfirmation(ContactVerificationOutcome Outcome, ContactVerification Verification, AgentRegistration Registration);

public interface IAgentRegistrationStore
{
    Task<AgentRegistration> CreateAnonymousAsync(Guid merchantId, RegistrationDraftRequest draft,
        byte[] referenceHash, DateTime now, TimeSpan lifetime, CancellationToken ct);
    Task<ContactVerificationIssue> IssueContactVerificationAsync(RegistrationSession session, CancellationToken ct);
    Task<ContactVerificationConfirmation> ConfirmContactVerificationAsync(RegistrationSession session,
        Guid verificationId, string code, CancellationToken ct);
    Task<AgentRegistration?> BindIdentityByEmailAsync(ExternalIdentity identity, string? email,
        string? displayName, Guid merchantId, CancellationToken ct);

    Task<RegistrationSession?> FindSessionAsync(byte[] sessionReferenceHash, CancellationToken cancellationToken);

    Task<AgentRegistration?> FindCaseAsync(
        ExternalIdentity identity, Guid merchantId, CancellationToken cancellationToken);

    Task<AgentRegistration?> FindCaseByIdAsync(Guid registrationId, CancellationToken cancellationToken);

    Task<AgentRegistration> SaveDraftAsync(
        RegistrationSession session, RegistrationDraftRequest draft, long? expectedVersion,
        CancellationToken cancellationToken);

    Task<AgentRegistration> SavePhotosAsync(
        RegistrationSession session, RegistrationPhotos photos, long? expectedVersion,
        CancellationToken cancellationToken);

    Task<RegistrationSubmitResult> SubmitAsync(
        RegistrationSession session, string idempotencyKey, long? expectedVersion,
        CancellationToken cancellationToken);

    Task<RegistrationDecisionResult> ApproveAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string contactEvidenceReference,
        string idempotencyKey, long? expectedVersion, CancellationToken cancellationToken);

    Task<RegistrationDecisionResult> RejectAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string rejectionReason,
        string? internalReviewNote, string idempotencyKey, long? expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentRegistrationAttempt>> ListAttemptsAsync(
        Guid registrationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentRegistration>> ListCasesAsync(
        Guid merchantId, CancellationToken cancellationToken);
}

/// <summary>Application boundary for the target registration lifecycle.</summary>
public sealed class AgentRegistrationService(IAgentRegistrationStore store)
{
    public async Task<(AgentRegistration Registration, string RawReference)> StartAnonymousAsync(
        Guid merchantId, RegistrationDraftRequest request, DateTime now, TimeSpan lifetime, CancellationToken ct)
    {
        ValidateDraft(request);
        var (raw, hash) = RegistrationSessionReference.Create();
        var registration = await store.CreateAnonymousAsync(merchantId, request, hash, now, lifetime, ct);
        return (registration, raw);
    }

    public Task<ContactVerificationIssue> IssueContactVerificationAsync(RegistrationSession session, CancellationToken ct) =>
        store.IssueContactVerificationAsync(session, ct);

    public async Task<ContactVerificationConfirmation> ConfirmContactVerificationAsync(
        RegistrationSession session, Guid verificationId, string code, CancellationToken ct)
    {
        if (code is null || code.Length != ContactVerificationPolicy.CodeLength || code.Any(c => c is < '0' or > '9'))
            throw new InvalidRequestException("A six-digit code is required.", "validation_failed");
        var result = await store.ConfirmContactVerificationAsync(session, verificationId, code, ct);
        // The store commits failed attempts before the HTTP exception is raised.
        return result.Outcome switch
        {
            ContactVerificationOutcome.Confirmed or ContactVerificationOutcome.AlreadyConfirmed => result,
            ContactVerificationOutcome.Invalid => throw new InvalidRequestException("The verification code is invalid.", "verification_code_invalid"),
            ContactVerificationOutcome.Expired => throw new ConflictException("The verification expired.", "verification_expired"),
            ContactVerificationOutcome.AttemptsExceeded => throw new ConflictException("The verification attempt limit was reached.", "verification_attempts_exceeded"),
            _ => throw new InvalidOperationException("Unknown verification outcome."),
        };
    }

    public Task<AgentRegistration?> BindIdentityByEmailAsync(ExternalIdentity identity, string? email,
        string? displayName, Guid merchantId, CancellationToken ct) =>
        store.BindIdentityByEmailAsync(identity, email, displayName, merchantId, ct);

    public async Task<RegistrationSession?> ResolveSessionAsync(string? rawReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawReference))
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawReference));
        var session = await store.FindSessionAsync(hash, ct);
        return session is not null && session.IsLiveAt(DateTime.UtcNow) ? session : null;
    }

    public async Task<AgentRegistration?> GetCaseAsync(RegistrationSession session, CancellationToken ct)
    {
        var registration = session.RegistrationId is { } id
            ? await store.FindCaseByIdAsync(id, ct)
            : session.Identity is { } identity
                ? await store.FindCaseAsync(identity, session.MerchantId, ct)
                : null;
        return registration?.MerchantId == session.MerchantId ? registration : null;
    }

    public Task<AgentRegistration?> GetCaseByIdAsync(Guid registrationId, CancellationToken ct) =>
        store.FindCaseByIdAsync(registrationId, ct);

    public async Task<AgentRegistration> SaveDraftAsync(
        RegistrationSession session, RegistrationDraftRequest request, long? expectedVersion, CancellationToken ct)
    {
        ValidateDraft(request);
        return await store.SaveDraftAsync(session, request with { Profile = request.Profile.Clone() }, expectedVersion, ct);
    }

    public Task<AgentRegistration> SavePhotosAsync(
        RegistrationSession session, RegistrationPhotos photos, long? expectedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(photos.PhotoObjectKey) || string.IsNullOrWhiteSpace(photos.PhotoContentType))
            throw new InvalidRequestException("Photo is required.", "photo_required");
        return store.SavePhotosAsync(session, photos, expectedVersion, ct);
    }

    public Task<RegistrationSubmitResult> SubmitAsync(
        RegistrationSession session, string idempotencyKey, long? expectedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new InvalidRequestException("Idempotency-Key is required.", "idempotency_key_required");
        return store.SubmitAsync(session, idempotencyKey.Trim(), expectedVersion, ct);
    }

    public Task<RegistrationDecisionResult> ApproveAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string evidenceReference,
        string idempotencyKey, long? expectedVersion, CancellationToken ct)
    {
        if (reviewerAccountId == Guid.Empty)
            throw new AccessDeniedException("Reviewer identity is required.");
        if (string.IsNullOrWhiteSpace(evidenceReference))
            throw new InvalidRequestException("Contact evidence is required.", "contact_evidence_required");
        RequireIdempotencyKey(idempotencyKey);
        return store.ApproveAsync(registrationId, attemptId, reviewerAccountId, evidenceReference.Trim(),
            idempotencyKey.Trim(), expectedVersion, ct);
    }

    public Task<RegistrationDecisionResult> RejectAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string reason, string? internalNote,
        string idempotencyKey, long? expectedVersion, CancellationToken ct)
    {
        if (reviewerAccountId == Guid.Empty)
            throw new AccessDeniedException("Reviewer identity is required.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidRequestException("Rejection reason is required.", "rejection_reason_required");
        RequireIdempotencyKey(idempotencyKey);
        return store.RejectAsync(registrationId, attemptId, reviewerAccountId, reason.Trim(), internalNote,
            idempotencyKey.Trim(), expectedVersion, ct);
    }

    public Task<IReadOnlyList<AgentRegistrationAttempt>> ListAttemptsAsync(
        Guid registrationId, CancellationToken ct) => store.ListAttemptsAsync(registrationId, ct);

    public Task<IReadOnlyList<AgentRegistration>> ListCasesAsync(Guid merchantId, CancellationToken ct) =>
        store.ListCasesAsync(merchantId, ct);

    public static RegistrationCaseView ToView(AgentRegistration registration, string? rejectionReason = null) =>
        new(registration.Id, registration.MerchantId, registration.Status, registration.CurrentAttemptNo,
            registration.CurrentAttemptId, rejectionReason, registration.Version,
            registration.SaleCode, registration.Email, registration.PhoneNumber, ParseProfile(registration.ProfileJson),
            registration.PhotoObjectKey is not null, registration.KycPhotoObjectKey is not null, registration.PhoneVerified);

    public static RegistrationAttemptView ToApplicantAttemptView(AgentRegistrationAttempt attempt) =>
        new(attempt.Id, attempt.AttemptNo, attempt.Status, attempt.SubmittedAt, attempt.RejectionReason,
            attempt.Version, null, null, null, null, null, attempt.DecidedByAccountId, attempt.DecidedAt,
            attempt.PhotoObjectKey is not null, attempt.KycPhotoObjectKey is not null);

    public static RegistrationAttemptView ToReviewerAttemptView(AgentRegistrationAttempt attempt) =>
        new(attempt.Id, attempt.AttemptNo, attempt.Status, attempt.SubmittedAt, attempt.RejectionReason,
            attempt.Version, attempt.SaleCode, attempt.Email, attempt.PhoneNumber, attempt.ProfileJson,
            attempt.InternalReviewNote, attempt.DecidedByAccountId, attempt.DecidedAt,
            attempt.PhotoObjectKey is not null, attempt.KycPhotoObjectKey is not null);

    private static JsonElement ParseProfile(string profileJson)
    {
        using var document = JsonDocument.Parse(profileJson);
        return document.RootElement.Clone();
    }

    /// <summary>Profile contract shared with the agent SPA and the reviewer console (schemaVersion 1):
    /// firstName, lastName, personType (Individual|Juristic), idNumber required; licenseNumber, acceptedTermsAt optional.</summary>
    private static readonly string[] RequiredProfileFields = ["firstName", "lastName", "idNumber"];

    private static void ValidateDraft(RegistrationDraftRequest request)
    {
        if (request is null)
            throw new InvalidRequestException("Registration draft is required.", "validation_failed");
        if (string.IsNullOrWhiteSpace(request.SaleCode) || request.SaleCode.Trim().Length > 64)
            throw new InvalidRequestException("SaleCode is invalid.", "validation_failed");
        if (!MailAddress.TryCreate(request.Email?.Trim(), out _)
            || request.Email.Trim().Length > 320)
            throw new InvalidRequestException("Email is invalid.", "validation_failed");
        ValidatePhone(request.PhoneNumber);
        if (request.Profile.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException("Profile must be a JSON object.", "validation_failed");
        var raw = request.Profile.GetRawText();
        if (raw.Length > 32_768)
            throw new InvalidRequestException("Profile is too large.", "validation_failed");
        ValidateProfile(request.Profile);
    }

    public static void ValidatePhone(string phoneNumber)
    {
        try
        {
            AgentRegistration.NormalizePhone(phoneNumber);
        }
        catch (ArgumentException)
        {
            throw new InvalidRequestException("PhoneNumber is invalid.", "phone_invalid");
        }
    }

    private static void ValidateProfile(JsonElement profile)
    {
        if (profile.TryGetProperty("schemaVersion", out var schemaVersion)
            && (schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1))
            throw new InvalidRequestException("Profile schemaVersion must be 1.", "validation_failed");
        foreach (var field in RequiredProfileFields)
        {
            if (!profile.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Trim().Length > 200)
                throw new InvalidRequestException($"Profile {field} is required.", "validation_failed");
        }
        if (!profile.TryGetProperty("personType", out var personType) || personType.ValueKind != JsonValueKind.String
            || personType.GetString() is not ("Individual" or "Juristic"))
            throw new InvalidRequestException("Profile personType must be Individual or Juristic.", "validation_failed");
        if (profile.TryGetProperty("licenseNumber", out var license)
            && license.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidRequestException("Profile licenseNumber must be a string or null.", "validation_failed");
        if (profile.TryGetProperty("acceptedTermsAt", out var acceptedAt)
            && (acceptedAt.ValueKind != JsonValueKind.String || !acceptedAt.TryGetDateTimeOffset(out _)))
            throw new InvalidRequestException("Profile acceptedTermsAt must be an ISO-8601 timestamp.", "validation_failed");
    }

    private static void RequireIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200 || value.Any(char.IsControl))
            throw new InvalidRequestException("Idempotency-Key is invalid.", "invalid_idempotency_key");
    }
}
