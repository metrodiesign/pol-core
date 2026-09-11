using Accounts.Application;
using global::Accounts.Domain;
using Admins.Application;
using Api.Iam;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Api.Accounts;

public sealed record AgentRegistrationApproveRequest(string ContactEvidenceReference);
public sealed record AgentRegistrationRejectRequest(string RejectionReason, string? InternalReviewNote);

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
        applicant.MapPost("/submissions", SubmitApplicantDraft)
            .AllowAnonymous().WithMetadata(new IfMatchMutationMarker("201"), new IdempotencyMutationMarker())
            .WithName("SubmitAgentRegistration").WithTags("Agent registration");
        applicant.MapGet("/history", GetApplicantHistory)
            .AllowAnonymous().WithName("GetAgentRegistrationHistory").WithTags("Agent registration");

        var review = api.MapGroup("/agent-registrations")
            .RequireAuthorization("admin").RequireCsrf();
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
        Guid registrationId, IAdminScope scope, AgentRegistrationService service, CancellationToken ct)
    {
        var registration = await service.GetCaseByIdAsync(registrationId, ct);
        return registration is null || !scope.Accessible.Allows(registration.MerchantId)
            ? Results.NotFound()
            : Results.Ok(AgentRegistrationService.ToView(registration));
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
        status = attempt.Status.ToString().ToUpperInvariant(),
        submittedAt = attempt.SubmittedAt,
        rejectionReason = attempt.RejectionReason,
        version = attempt.Version,
        decidedAt = attempt.DecidedAt,
    };
}
