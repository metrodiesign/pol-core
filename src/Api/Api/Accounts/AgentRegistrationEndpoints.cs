using Accounts.Application;
using global::Accounts.Domain;
using Admins.Application;
using Api.Iam;
using Iam.Domain.Permissions;
using Merchants.Application.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Api.Accounts;

public sealed record AgentRegistrationApproveRequest(string ContactEvidenceReference);
public sealed record AgentRegistrationRejectRequest(string RejectionReason, string? InternalReviewNote);

/// <summary>OpenAPI shape of the multipart photo upload (jpeg/png/webp, each at most 2 MB).</summary>
public sealed class AgentRegistrationPhotosRequest
{
    public IFormFile Photo { get; init; } = null!;
    public IFormFile? KycPhoto { get; init; }
}

internal static class AgentRegistrationEndpoints
{
    public static void MapAgentRegistrationEndpoints(this RouteGroupBuilder api)
    {
        var applicant = api.MapGroup("/agent-registration")
            .WithSummary("จัดการการสมัครตัวแทน")
            .WithDescription("อ่านและแก้ไข registration case ของ identity ที่ผ่าน registration session โดยไม่เปิดเผยข้อมูลผู้สมัครรายอื่น");
        applicant.MapGet("", GetApplicantCase)
            .AllowAnonymous().WithName("GetAgentRegistration").WithTags("Agent registration");
        applicant.MapPut("", SaveApplicantDraft)
            .AllowAnonymous().WithMetadata(new EtagResponseMarker("200"))
            .WithName("SaveAgentRegistrationDraft").WithTags("Agent registration");
        applicant.MapPut("/photos", SaveApplicantPhotos)
            .AllowAnonymous().DisableAntiforgery().WithMetadata(new EtagResponseMarker("200"))
            .Accepts<AgentRegistrationPhotosRequest>("multipart/form-data")
            .WithName("SaveAgentRegistrationPhotos").WithTags("Agent registration")
            .WithSummary("อัปโหลดรูปถ่ายผู้สมัคร (photo บังคับ, kycPhoto ไม่บังคับ)")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        applicant.MapPost("/submissions", SubmitApplicantDraft)
            .AllowAnonymous().WithMetadata(new IfMatchMutationMarker("201"), new IdempotencyMutationMarker())
            .WithName("SubmitAgentRegistration").WithTags("Agent registration");
        applicant.MapGet("/history", GetApplicantHistory)
            .AllowAnonymous().WithName("GetAgentRegistrationHistory").WithTags("Agent registration");

        var review = api.MapGroup("/agent-registrations")
            .RequireAuthorization("admin");
        review.WithSummary("พิจารณาการสมัครตัวแทน")
            .WithDescription("อ่านและตัดสิน registration ภายใน Admin merchant scope พร้อมตรวจ version และ idempotency");
        review.MapGet("", ListReviewerCases)
            .RequirePermission(Keys.MerchantUserView)
            .WithName("ListAgentRegistrations").WithTags("Agent registration");
        review.MapGet("/{registrationId:guid}", GetReviewerCase)
            .RequirePermission(Keys.MerchantUserView)
            .WithName("GetAgentRegistrationForReview").WithTags("Agent registration");
        review.MapGet("/{registrationId:guid}/attempts", ListReviewerAttempts)
            .RequirePermission(Keys.MerchantUserView)
            .WithName("ListAgentRegistrationAttempts").WithTags("Agent registration");
        review.MapGet("/{registrationId:guid}/attempts/{attemptId:guid}/photos/{kind}", GetReviewerAttemptPhoto)
            .RequirePermission(Keys.MerchantUserView)
            .WithName("GetAgentRegistrationAttemptPhoto").WithTags("Agent registration")
            .WithSummary("อ่านรูปถ่ายที่ผู้สมัครยื่นในรอบนั้น (kind = photo | kyc)")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound);
        review.MapPost("/{registrationId:guid}/attempts/{attemptId:guid}/approve", Approve)
            .RequirePermission(Keys.MerchantUserApprove)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("ApproveAgentRegistration").WithTags("Agent registration");
        review.MapPost("/{registrationId:guid}/attempts/{attemptId:guid}/reject", Reject)
            .RequirePermission(Keys.MerchantUserReject)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("RejectAgentRegistration").WithTags("Agent registration");
    }

    private static async Task<IResult> GetApplicantCase(
        HttpContext http, AgentRegistrationService service, CancellationToken ct)
    {
        var session = await Session(http, service, ct);
        if (session is null)
            return Results.Unauthorized();
        var registration = await service.GetCaseAsync(session, ct);
        if (registration is null)
            return Results.NotFound();
        var attempts = await service.ListAttemptsAsync(registration.Id, ct);
        var current = attempts.LastOrDefault(x => x.Id == registration.CurrentAttemptId);
        VersionEtags.Set(http, registration.Version);
        return Results.Ok(ApplicantCase(registration, current));
    }

    private static async Task<IResult> SaveApplicantDraft(
        HttpContext http, RegistrationDraftRequest body, AgentRegistrationService service, CancellationToken ct)
    {
        var session = await Session(http, service, ct);
        if (session is null)
            return Results.Unauthorized();
        var version = OptionalVersion(http);
        var registration = await service.SaveDraftAsync(session, body, version, ct);
        VersionEtags.Set(http, registration.Version);
        return Results.Ok(ApplicantCase(registration, null));
    }

    /// <summary>Multipart photo upload: bytes are validated (jpeg/png/webp, magic bytes, size) and stored BEFORE the
    /// draft is updated, so the draft only ever references stored objects. Same rules as the legacy merchant-user
    /// register form; the photo is required to submit, the KYC photo is optional.</summary>
    private static async Task<IResult> SaveApplicantPhotos(
        HttpRequest request, HttpContext http, AgentRegistrationService service, IPhotoStore photos, CancellationToken ct)
    {
        var session = await Session(http, service, ct);
        if (session is null)
            return Results.Unauthorized();

        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = (2 * PhotoValidation.DefaultMaxBytes) + 64 * 1024;
        if (!request.HasFormContentType)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "multipart/form-data is required.",
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The upload exceeds the size limit.");
        }

        var photo = await ReadPhotoAsync(form.Files["photo"], ct);
        if (photo.Error is not null)
            return photo.Error;
        if (photo.Bytes is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "photo is required.",
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
        var kyc = await ReadPhotoAsync(form.Files["kycPhoto"], ct);
        if (kyc.Error is not null)
            return kyc.Error;

        var version = OptionalVersion(http);
        var photoKey = await photos.PutAsync(photo.Bytes, photo.ContentType!, ct);
        var kycKey = kyc.Bytes is null ? null : await photos.PutAsync(kyc.Bytes, kyc.ContentType!, ct);
        var registration = await service.SavePhotosAsync(session,
            new RegistrationPhotos(photoKey, photo.ContentType!, kycKey, kyc.ContentType), version, ct);
        VersionEtags.Set(http, registration.Version);
        return Results.Ok(ApplicantCase(registration, null));
    }

    private static async Task<(byte[]? Bytes, string? ContentType, IResult? Error)> ReadPhotoAsync(
        IFormFile? file, CancellationToken ct)
    {
        if (file is not { Length: > 0 })
            return (null, null, null);
        if (file.Length > PhotoValidation.DefaultMaxBytes)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"{file.Name} exceeds the size limit."));
        var buffer = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(buffer, ct);
        var validation = PhotoValidation.Validate(
            file.ContentType, buffer.AsSpan(0, Math.Min(16, buffer.Length)), buffer.Length, PhotoValidation.DefaultMaxBytes);
        if (!validation.IsValid)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: validation.Error,
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" }));
        return (buffer, validation.ContentType, null);
    }

    private static async Task<IResult> GetReviewerAttemptPhoto(
        Guid registrationId, Guid attemptId, string kind, IAdminScope scope, AgentRegistrationService service,
        IPhotoStore photos, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        if (registration is null || !scope.Accessible.Allows(registration.MerchantId))
            return Results.NotFound();
        var attempt = (await service.ListAttemptsAsync(registrationId, ct)).SingleOrDefault(x => x.Id == attemptId);
        var key = kind switch
        {
            "photo" => attempt?.PhotoObjectKey,
            "kyc" => attempt?.KycPhotoObjectKey,
            _ => null,
        };
        if (key is null)
            return Results.NotFound();
        var stored = await photos.GetAsync(key, ct);
        return stored is null
            ? Results.NotFound()
            : Results.File(stored.Value.Bytes, stored.Value.ContentType);
    }

    private static async Task<IResult> SubmitApplicantDraft(
        HttpContext http, AgentRegistrationService service, CancellationToken ct)
    {
        var session = await Session(http, service, ct);
        if (session is null)
            return Results.Unauthorized();
        var result = await service.SubmitAsync(session, IdempotencyKeys.Require(http), VersionEtags.Require(http), ct);
        VersionEtags.Set(http, result.Registration.Version);
        return Results.Created($"/api/v1/agent-registration/history/{result.Attempt.AttemptId}", new
        {
            registration = result.Registration,
            attempt = ApplicantAttempt(result.Attempt),
            replayed = result.Replayed,
        });
    }

    private static async Task<IResult> GetApplicantHistory(
        HttpContext http, AgentRegistrationService service, CancellationToken ct)
    {
        var session = await Session(http, service, ct);
        if (session is null)
            return Results.Unauthorized();
        var registration = await service.GetCaseAsync(session, ct);
        if (registration is null)
            return Results.NotFound();
        var attempts = await service.ListAttemptsAsync(registration.Id, ct);
        VersionEtags.Set(http, registration.Version);
        return Results.Ok(new
        {
            registration = AgentRegistrationService.ToView(registration),
            attempts = attempts.Select(attempt => ApplicantAttempt(
                AgentRegistrationService.ToApplicantAttemptView(attempt))).ToArray(),
        });
    }

    private static async Task<IResult> ListReviewerCases(
        HttpContext http, Guid? merchantId, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var selected = merchantId ?? (scope.Accessible.IsUnrestricted ? Guid.Empty : scope.Accessible.Merchants.SingleOrDefault());
        if (selected == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["merchantId"] = ["merchantId is required when more than one Merchant is accessible."],
            });
        if (!scope.Accessible.Allows(selected))
            return Results.NotFound();
        var registrations = await service.ListCasesAsync(selected, ct);
        return Results.Ok(registrations.Select(x => AgentRegistrationService.ToView(x)).ToArray());
    }

    private static async Task<IResult> GetReviewerCase(
        HttpContext http, Guid registrationId, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        if (registration is null || !scope.Accessible.Allows(registration.MerchantId))
            return Results.NotFound();
        // Approve/Reject require If-Match, so the reviewer read is where the ETag comes from.
        VersionEtags.Set(http, registration.Version);
        return Results.Ok(AgentRegistrationService.ToView(registration));
    }

    private static async Task<IResult> ListReviewerAttempts(
        Guid registrationId, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        if (registration is null || !scope.Accessible.Allows(registration.MerchantId))
            return Results.NotFound();
        var attempts = await service.ListAttemptsAsync(registrationId, ct);
        return Results.Ok(attempts.Select(AgentRegistrationService.ToReviewerAttemptView).ToArray());
    }

    private static async Task<IResult> Approve(
        Guid registrationId, Guid attemptId, AgentRegistrationApproveRequest body,
        HttpContext http, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        if (registration is null || !scope.Accessible.Allows(registration.MerchantId))
            return Results.NotFound();
        var result = await service.ApproveAsync(registrationId, attemptId, scope.Current.AdminId,
            body.ContactEvidenceReference, IdempotencyKeys.Require(http), VersionEtags.Require(http), ct);
        VersionEtags.Set(http, result.Registration.Version);
        return Results.Ok(new { registration = result.Registration, attempt = result.Attempt, replayed = result.Replayed });
    }

    private static async Task<IResult> Reject(
        Guid registrationId, Guid attemptId, AgentRegistrationRejectRequest body,
        HttpContext http, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        if (registration is null || !scope.Accessible.Allows(registration.MerchantId))
            return Results.NotFound();
        var result = await service.RejectAsync(registrationId, attemptId, scope.Current.AdminId,
            body.RejectionReason, body.InternalReviewNote, IdempotencyKeys.Require(http),
            VersionEtags.Require(http), ct);
        VersionEtags.Set(http, result.Registration.Version);
        return Results.Ok(new { registration = result.Registration, attempt = result.Attempt, replayed = result.Replayed });
    }

    private static async Task<RegistrationSession?> Session(
        HttpContext http, AgentRegistrationService service, CancellationToken ct) =>
        await service.ResolveSessionAsync(http.Request.Cookies["pol_registration_session"], ct);

    private static long? OptionalVersion(HttpContext http) =>
        http.Request.Headers.IfMatch.Count == 0 ? null : VersionEtags.Require(http);

    private static object ApplicantCase(AgentRegistration registration, AgentRegistrationAttempt? current) => new
    {
        registration = AgentRegistrationService.ToView(registration, current?.RejectionReason),
        nextAction = registration.Status switch
        {
            AgentRegistrationStatus.Draft or AgentRegistrationStatus.Rejected => "submit",
            AgentRegistrationStatus.Pending => "wait",
            AgentRegistrationStatus.Approved => "login",
            _ => "wait",
        },
        currentAttempt = current is null ? null : ApplicantAttempt(AgentRegistrationService.ToApplicantAttemptView(current)),
    };

    private static object ApplicantAttempt(RegistrationAttemptView attempt) => new
    {
        attemptId = attempt.AttemptId,
        attemptNo = attempt.AttemptNo,
        status = attempt.Status.ToString(),
        submittedAt = attempt.SubmittedAt,
        rejectionReason = attempt.RejectionReason,
        version = attempt.Version,
        decidedAt = attempt.DecidedAt,
    };
}
