using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Admins.Application;
using Api.Admins;
using Api.Iam;
using BuildingBlocks.Application;
using Governance.Application;
using Governance.Domain;
using Iam.Domain.Permissions;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Api.Governance;

internal sealed record GovernanceEtagMarker(string ResponseStatus);
internal sealed record GovernanceDecisionMarker(string ResponseStatus);

internal static class GovernanceOpenApi
{
    public static void Apply(OpenApiOperation operation, GovernanceEtagMarker marker)
    {
        if (operation.Responses?.TryGetValue(marker.ResponseStatus, out var response) == true
            && response is OpenApiResponse concrete)
        {
            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
            concrete.Headers["ETag"] = new OpenApiHeader
            {
                Required = true,
                Description = "Optimistic version token for If-Match.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^\\\"v[1-9][0-9]*\\\"$" },
            };
        }
    }

    public static void Apply(OpenApiOperation operation, GovernanceDecisionMarker marker)
    {
        var parameters = operation.Parameters ??= [];
        RequireHeader(parameters, "If-Match", "Current ETag from approval detail.");
        RequireHeader(parameters, "Idempotency-Key", "Retry key scoped to actor and decision operation.");
        Apply(operation, new GovernanceEtagMarker(marker.ResponseStatus));
    }

    private static void RequireHeader(IList<IOpenApiParameter> parameters, string name, string description)
    {
        var existing = parameters.FirstOrDefault(x =>
            x.In == ParameterLocation.Header && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is OpenApiParameter concrete)
        {
            concrete.Required = true;
            concrete.Description = description;
            return;
        }
        if (existing is not null)
            parameters.Remove(existing);
        parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}

internal static partial class GovernanceEndpoints
{
    public static void MapGovernanceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/approvals", ListApprovals)
            .RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การอนุมัติ")
            .WithName("ListApprovalRequests")
            .WithSummary("รายการคำขอ maker-checker")
            .WithDescription("คืนคำขอภายใน Admin merchant scope แบบแบ่งหน้า กรอง action, status, merchantId, ช่วงเวลา และค้นหาได้")
            .Produces<PagedResult<ApprovalListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/approvals/{approvalId:guid}", GetApproval)
            .RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new GovernanceEtagMarker("200"))
            .WithTags("การอนุมัติ")
            .WithName("GetApprovalRequest")
            .WithSummary("รายละเอียดคำขอ maker-checker")
            .WithDescription("คืน maker, target, required permission, target version, decision/execution outcome และ ETag หากไม่พบหรือนอก Admin merchant scope -> 404")
            .Produces<ApprovalDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        MapDecision(api, "approve", ApprovalDecision.Approve, "ApproveRequest");
        MapDecision(api, "reject", ApprovalDecision.Reject, "RejectRequest");

        api.MapGet("/audits", ListAudits)
            .RequireAuthorization("admin").RequirePermission(Keys.AuditView)
            .WithTags("บันทึกการตรวจสอบ")
            .WithName("ListAuditRecords")
            .WithSummary("รายการ audit แบบ append-only")
            .WithDescription("ตรวจความสมบูรณ์ของ hash chain ก่อนคืน audit ภายใน Admin merchant scope แบบแบ่งหน้า กรอง actor, action, resource, result, merchantId และช่วงเวลาได้; integrity ผิดปกติ -> 503")
            .Produces<PagedResult<AuditListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/audits/{auditId:guid}", GetAudit)
            .RequireAuthorization("admin").RequirePermission(Keys.AuditView)
            .WithTags("บันทึกการตรวจสอบ")
            .WithName("GetAuditRecord")
            .WithSummary("รายละเอียด audit แบบ append-only")
            .WithDescription("ตรวจ hash chain แล้วคืน actor, action, resource, sanitized changes, approval link, correlationId, previousHash และ hash หากไม่พบ -> 404, integrity ผิดปกติ -> 503")
            .Produces<AuditDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static void MapDecision(
        RouteGroupBuilder api, string segment, ApprovalDecision decision, string operationName)
    {
        api.MapPost($"/approvals/{{approvalId:guid}}/{segment}", async (
            Guid approvalId,
            DecisionRequest body,
            HttpContext http,
            IAdminScope scope,
            IMediator mediator,
            [FromHeader(Name = "If-Match")] string? ifMatch,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            CancellationToken ct) =>
        {
            if (!TryVersion(ifMatch, out var version)
                || string.IsNullOrWhiteSpace(idempotencyKey)
                || idempotencyKey.Length > 200
                || idempotencyKey.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(body.Reason)
                || body.Reason.Trim().Length > 1000
                || string.IsNullOrWhiteSpace(body.TargetVersion)
                || body.TargetVersion.Trim().Length > 200)
                return Problem(http, StatusCodes.Status400BadRequest, "Invalid decision request", "invalid_request");

            try
            {
                var result = await mediator.Send(new DecideApprovalCommand(new DecisionIntent(
                    approvalId,
                    decision,
                    body.Reason,
                    version,
                    body.TargetVersion,
                    idempotencyKey.Trim(),
                    http.TraceIdentifier,
                    Access(scope))), ct);
                http.Response.Headers.ETag = Etag(result.Approval.Version);
                return Results.Accepted(value: result.Approval);
            }
            catch (GovernanceAccessDeniedException ex)
            {
                return Problem(http, StatusCodes.Status403Forbidden, "Decision forbidden", ex.Code);
            }
            catch (NotFoundException)
            {
                return Problem(http, StatusCodes.Status404NotFound, "Approval not found", "not_found");
            }
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new GovernanceDecisionMarker("202"))
            .WithTags("การอนุมัติ")
            .WithName(operationName)
            .WithSummary(decision == ApprovalDecision.Approve ? "อนุมัติคำขอ" : "ปฏิเสธคำขอ")
            .WithDescription(decision == ApprovalDecision.Approve
                ? "อนุมัติคำขอที่ pending แล้ว enqueue การ execute โดย checker ต้องไม่ใช่ maker และต้องมีสิทธิ์ของ action ส่ง reason, targetVersion, If-Match และ Idempotency-Key"
                : "ปฏิเสธคำขอที่ pending โดย checker ต้องไม่ใช่ maker และต้องมีสิทธิ์ของ action ส่ง reason, targetVersion, If-Match และ Idempotency-Key")
            .Produces<ApprovalDetail>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListApprovals(
        HttpContext http,
        IAdminScope scope,
        IMediator mediator,
        int page = 1,
        int limit = 25,
        string? search = null,
        string? action = null,
        string? status = null,
        string? merchantId = null,
        string? from = null,
        string? to = null,
        CancellationToken ct = default)
    {
        if (!ValidPage(page, limit) || !TryGuid(merchantId, out var merchant)
            || !TryInstant(from, out var fromUtc) || !TryInstant(to, out var toUtc)
            || fromUtc > toUtc || !TryApprovalStatus(status, out var parsedStatus)
            || TooLong(search, 200) || TooLong(action, 120))
            return Problem(http, StatusCodes.Status400BadRequest, "Invalid approval filter", "invalid_filter");
        var result = await mediator.Send(new ListApprovalsQuery(new ApprovalQuery(
            page, limit, search, action, parsedStatus, merchant, fromUtc, toUtc, Access(scope))), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetApproval(
        Guid approvalId, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct)
    {
        var result = await mediator.Send(new GetApprovalQuery(approvalId, Access(scope)), ct);
        if (result is null)
            return Problem(http, StatusCodes.Status404NotFound, "Approval not found", "not_found");
        http.Response.Headers.ETag = Etag(result.Version);
        return Results.Ok(result);
    }

    private static async Task<IResult> ListAudits(
        HttpContext http,
        IAdminScope scope,
        IMediator mediator,
        int page = 1,
        int limit = 25,
        string? actor = null,
        string? action = null,
        string? resource = null,
        string? result = null,
        string? merchantId = null,
        string? from = null,
        string? to = null,
        CancellationToken ct = default)
    {
        if (!ValidPage(page, limit) || !TryGuid(actor, out var actorId) || !TryGuid(merchantId, out var merchant)
            || !TryInstant(from, out var fromUtc) || !TryInstant(to, out var toUtc)
            || fromUtc > toUtc || TooLong(action, 120) || TooLong(resource, 200) || TooLong(result, 80))
            return Problem(http, StatusCodes.Status400BadRequest, "Invalid audit filter", "invalid_filter");
        try
        {
            var response = await mediator.Send(new ListAuditsQuery(new AuditQuery(
                page, limit, actorId, action, resource, result, merchant, fromUtc, toUtc, Access(scope))), ct);
            return Results.Ok(response);
        }
        catch (AuditIntegrityException)
        {
            return Problem(http, StatusCodes.Status503ServiceUnavailable,
                "Audit integrity is unhealthy", "audit_integrity_unhealthy");
        }
    }

    private static async Task<IResult> GetAudit(
        Guid auditId, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct)
    {
        try
        {
            var result = await mediator.Send(new GetAuditQuery(auditId, Access(scope)), ct);
            return result is null
                ? Problem(http, StatusCodes.Status404NotFound, "Audit record not found", "not_found")
                : Results.Ok(result);
        }
        catch (AuditIntegrityException)
        {
            return Problem(http, StatusCodes.Status503ServiceUnavailable,
                "Audit integrity is unhealthy", "audit_integrity_unhealthy");
        }
    }

    private static GovernanceAccess Access(IAdminScope scope)
    {
        var current = scope.Current;
        return new GovernanceAccess(
            current.AdminId,
            current.Accessible.IsUnrestricted,
            current.Accessible.Merchants,
            current.Permissions);
    }

    private static IResult Problem(HttpContext http, int status, string title, string code) => Results.Problem(
        statusCode: status,
        title: title,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = http.TraceIdentifier,
        });

    private static bool ValidPage(int page, int limit) => page >= 1 && limit is >= 1 and <= 100;
    private static bool TooLong(string? value, int max) => value?.Trim().Length > max;
    private static string Etag(long version) => $"\"v{version}\"";

    private static bool TryVersion(string? etag, out long version)
    {
        version = 0;
        var match = EtagPattern().Match(etag ?? "");
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static bool TryGuid(string? value, out Guid? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!Guid.TryParseExact(value.Trim(), "D", out var id) || id == Guid.Empty)
            return false;
        parsed = id;
        return true;
    }

    private static bool TryInstant(string? value, out DateTime? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var trimmed = value.Trim();
        if (!OffsetPattern().IsMatch(trimmed)
            || !DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return false;
        parsed = instant.UtcDateTime;
        return true;
    }

    private static bool TryApprovalStatus(string? value, out ApprovalStatus? status)
    {
        status = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "pending" => ApprovalStatus.Pending,
            "approved" => ApprovalStatus.Approved,
            "rejected" => ApprovalStatus.Rejected,
            "succeeded" => ApprovalStatus.Succeeded,
            "failed" => ApprovalStatus.Failed,
            "unknown" => ApprovalStatus.Unknown,
            _ => (ApprovalStatus?)(-1),
        };
        return status != (ApprovalStatus?)(-1);
    }

    [GeneratedRegex("^\\\"v([1-9][0-9]*)\\\"$", RegexOptions.CultureInvariant)]
    private static partial Regex EtagPattern();

    [GeneratedRegex("(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();
}

internal sealed record DecisionRequest(
    [property: Required] string Reason,
    [property: Required] string TargetVersion);
